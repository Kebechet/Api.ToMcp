using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Parsing
{
    internal static class ActionMethodAnalyzer
    {
        private const string McpExposeAttributeFullName = "Api.ToMcp.Abstractions.McpExposeAttribute";
        private const string McpIgnoreAttributeFullName = "Api.ToMcp.Abstractions.McpIgnoreAttribute";
        private const string ApiBehaviorMetadataFullName = "Microsoft.AspNetCore.Mvc.Infrastructure.IApiBehaviorMetadata";
        private const string ToolNamePropertyName = "ToolName";
        private const string TitlePropertyName = "Title";
        private const string DescriptionPropertyName = "Description";
        private const string AsyncSuffix = "Async";
        private const string ReadOnlyHintPropertyName = "ReadOnly";
        private const string DestructiveHintPropertyName = "Destructive";
        private const string IdempotentHintPropertyName = "Idempotent";
        private const string OpenWorldHintPropertyName = "OpenWorld";

        private static readonly HashSet<string> HttpMethodAttributes = new HashSet<string>
        {
            "HttpGetAttribute",
            "HttpPostAttribute",
            "HttpPutAttribute",
            "HttpDeleteAttribute",
            "HttpPatchAttribute"
        };

        public static ImmutableArray<ActionInfoModel> GetActions(
            INamedTypeSymbol controller,
            string? routePrefix,
            Compilation compilation,
            CancellationToken ct)
        {
            var builder = ImmutableArray.CreateBuilder<ActionInfoModel>();
            var controllerMcpExposeAttr = FindNearestAttribute(controller, x => x.BaseType, IsMcpExposeAttribute);
            var controllerScope = GetDeclaredScope(controllerMcpExposeAttr);
            var isApiController = IsApiController(controller);

            foreach (var member in controller.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (!(member is IMethodSymbol method))
                    continue;

                if (method.MethodKind != MethodKind.Ordinary)
                    continue;

                if (method.DeclaredAccessibility != Accessibility.Public)
                    continue;

                var httpInfo = GetHttpMethodInfo(method);
                if (httpInfo == null)
                    continue;

                var parameters = AnalyzeParameters(method, httpInfo.Value.Method);
                var returnType = AnalyzeReturnType(method);
                var responseType = ResponseTypeResolver.Resolve(method, controller, compilation);

                var fullRoute = BuildFullRoute(routePrefix, httpInfo.Value.Template, controller.Name, GetRouteActionName(method));
                fullRoute = ResolveApiVersionInRoute(fullRoute, controller, method);

                var mcpExposeAttr = ControllerScanner.GetAttribute(method, McpExposeAttributeFullName);
                string? customToolName = null;
                string? customTitle = null;
                string? customDescription = null;
                // An action that names a scope, McpScope.None included, overrides the controller's; None then means
                // the HTTP method's scope, so inheriting the controller's Read cannot open a POST to a read-only grant.
                var actionMcpExposeAttr = FindNearestAttribute(method, x => x.OverriddenMethod, IsMcpExposeAttribute);
                var declaredScope = GetDeclaredScope(actionMcpExposeAttr) ?? controllerScope;
                var requiredScope = declaredScope is null or McpScopeModel.None
                    ? GetRequiredScope(httpInfo.Value.Method)
                    : declaredScope.Value;

                if (mcpExposeAttr != null)
                {
                    foreach (var namedArg in mcpExposeAttr.NamedArguments)
                    {
                        if (namedArg.Key == ToolNamePropertyName && namedArg.Value.Value is string tn)
                            customToolName = tn;
                        if (namedArg.Key == DescriptionPropertyName && namedArg.Value.Value is string desc)
                            customDescription = desc;
                    }
                }

                // Like scope and hints, the title follows the nearest action-level [McpExpose], an overridden base
                // action's included; a controller's is never read, because one title cannot name all its actions.
                if (actionMcpExposeAttr != null)
                {
                    foreach (var namedArg in actionMcpExposeAttr.NamedArguments)
                    {
                        if (namedArg.Key == TitlePropertyName && namedArg.Value.Value is string title && !string.IsNullOrWhiteSpace(title))
                            customTitle = title;
                    }
                }

                builder.Add(new ActionInfoModel
                {
                    Name = method.Name,
                    ControllerName = controller.Name,
                    HttpMethod = httpInfo.Value.Method,
                    RouteTemplate = fullRoute,
                    Parameters = parameters,
                    ReturnType = returnType,
                    HasMcpExposeAttribute = mcpExposeAttr != null,
                    HasMcpIgnoreAttribute = ControllerScanner.HasAttribute(method, McpIgnoreAttributeFullName),
                    XmlDocSummary = GetXmlDocSummary(method),
                    CustomToolName = customToolName,
                    Title = customTitle ?? ToolTitleBuilder.Build(customToolName, TrimSuffix(method.Name, AsyncSuffix)),
                    CustomDescription = customDescription,
                    IsApiController = isApiController,
                    RequiredScope = requiredScope,
                    Annotations = GetToolAnnotations(httpInfo.Value.Method, requiredScope, actionMcpExposeAttr, controllerMcpExposeAttr),
                    OutputSchemaType = responseType.SuccessType is null ? null : ResponseTypeResolver.ToTypeOfOperand(responseType.SuccessType),
                    IsOutputNullable = responseType.SuccessType is null ||
                        responseType.HasBodylessSuccess ||
                        EmptyResponseAnalyzer.CanSucceedWithoutBody(method, compilation, ct)
                });
            }

            return builder.ToImmutable();
        }

        private static (string Method, string? Template)? GetHttpMethodInfo(IMethodSymbol method)
        {
            // A method-level [Route("...")] supplies the template when the HTTP verb
            // attribute carries none (e.g. [HttpGet] + [Route("search")]).
            var routeAttrTemplate = method.GetAttributes()
                .FirstOrDefault(attr => attr.AttributeClass?.Name == "RouteAttribute" &&
                                        attr.ConstructorArguments.Length > 0)
                ?.ConstructorArguments[0].Value?.ToString();

            foreach (var attr in method.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name;
                if (attrName == null || !HttpMethodAttributes.Contains(attrName))
                    continue;

                var httpMethod = attrName.Replace("Attribute", "").Replace("Http", "").ToUpperInvariant();
                var template = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value?.ToString()
                    : null;

                if (string.IsNullOrEmpty(template))
                    template = routeAttrTemplate;

                return (httpMethod, template);
            }

            return null;
        }

        private static ImmutableArray<ParameterInfoModel> AnalyzeParameters(
            IMethodSymbol method,
            string httpMethod)
        {
            var builder = ImmutableArray.CreateBuilder<ParameterInfoModel>();
            var paramDocs = GetXmlDocParams(method);

            foreach (var param in method.Parameters)
            {
                var source = DetermineParameterSource(param, httpMethod);

                // Skip service-injected parameters
                if (source == ParameterSourceModel.Services)
                    continue;

                // Skip CancellationToken
                if (param.Type.ToDisplayString() == KnownTypeNames.CancellationToken)
                    continue;

                var properties = ImmutableArray<PropertyInfoModel>.Empty;

                // A [FromRoute] or [FromBody] parameter's properties fill route values whatever its type, as a body is JSON
                // even for a type MVC reads from one string.
                if (source == ParameterSourceModel.Route || source == ParameterSourceModel.Body)
                {
                    properties = ExtractProperties(param.Type);
                }

                builder.Add(new ParameterInfoModel
                {
                    Name = param.Name,
                    Type = param.Type.ToDisplayString(),
                    IsNullable = param.Type.NullableAnnotation == NullableAnnotation.Annotated ||
                                 param.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
                    HasDefaultValue = param.HasExplicitDefaultValue,
                    DefaultValue = param.HasExplicitDefaultValue ? param.ExplicitDefaultValue : null,
                    Source = source,
                    IsSimpleType = SimpleTypeClassifier.IsSimpleType(param.Type),
                    Properties = properties,
                    Description = paramDocs.TryGetValue(param.Name, out var doc) ? doc : null,
                    QueryName = GetFromQueryName(param)
                });
            }

            return builder.ToImmutable();
        }

        private static string? GetFromQueryName(IParameterSymbol param)
        {
            return param.GetAttributes()
                .Where(x => x.AttributeClass?.Name == "FromQueryAttribute")
                .SelectMany(x => x.NamedArguments)
                .Where(x => x.Key == "Name")
                .Select(x => x.Value.Value as string)
                .FirstOrDefault(x => !string.IsNullOrEmpty(x));
        }

        private static ImmutableArray<PropertyInfoModel> ExtractProperties(ITypeSymbol type)
        {
            var builder = ImmutableArray.CreateBuilder<PropertyInfoModel>();

            foreach (var member in type.GetMembers())
            {
                if (!(member is IPropertySymbol property))
                    continue;

                if (property.DeclaredAccessibility != Accessibility.Public)
                    continue;

                if (property.GetMethod == null)
                    continue;

                builder.Add(new PropertyInfoModel
                {
                    Name = property.Name,
                    Type = property.Type.ToDisplayString(),
                    IsNullable = property.Type.NullableAnnotation == NullableAnnotation.Annotated ||
                                 property.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                });
            }

            return builder.ToImmutable();
        }

        private static ParameterSourceModel DetermineParameterSource(
            IParameterSymbol param,
            string httpMethod)
        {
            foreach (var attr in param.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name;
                switch (attrName)
                {
                    case "FromRouteAttribute":
                        return ParameterSourceModel.Route;
                    case "FromQueryAttribute":
                        return ParameterSourceModel.Query;
                    case "FromBodyAttribute":
                        return ParameterSourceModel.Body;
                    case "FromHeaderAttribute":
                        return ParameterSourceModel.Header;
                    case "FromServicesAttribute":
                        return ParameterSourceModel.Services;
                }
            }

            // Auto-infer based on HTTP method and type
            if ((httpMethod == HttpMethodNames.Post || httpMethod == HttpMethodNames.Put || httpMethod == HttpMethodNames.Patch) && IsComplexType(param.Type))
                return ParameterSourceModel.Body;

            return ParameterSourceModel.Auto;
        }

        private static bool IsComplexType(ITypeSymbol type)
        {
            if (type.SpecialType != SpecialType.None)
                return false;

            // A strongly-typed id or an int? is a struct too, but MVC binds it from the route or query, never the body.
            if (SimpleTypeClassifier.IsSimpleType(type))
                return false;

            return type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct;
        }

        private static ReturnTypeInfoModel AnalyzeReturnType(IMethodSymbol method)
        {
            var returnType = method.ReturnType;
            var isAsync = false;
            ITypeSymbol? innerType = returnType;

            // Unwrap Task<T> or ValueTask<T>
            if (returnType is INamedTypeSymbol namedType)
            {
                var typeName = namedType.OriginalDefinition.ToDisplayString();
                if (typeName is KnownTypeNames.TaskOfTResult or KnownTypeNames.ValueTaskOfTResult)
                {
                    isAsync = true;
                    innerType = namedType.TypeArguments[0];
                }
                else if (typeName is KnownTypeNames.Task or KnownTypeNames.ValueTask)
                {
                    isAsync = true;
                    innerType = null;
                }

                // Unwrap ActionResult<T>
                if (innerType is INamedTypeSymbol innerNamed)
                {
                    var innerTypeName = innerNamed.OriginalDefinition.ToDisplayString();
                    if (innerTypeName == KnownTypeNames.ActionResultOfTValue)
                    {
                        innerType = innerNamed.TypeArguments[0];
                    }
                }
            }

            return new ReturnTypeInfoModel
            {
                FullTypeName = returnType.ToDisplayString(),
                IsAsync = isAsync,
                UnwrappedType = innerType?.ToDisplayString() ?? "void"
            };
        }

        private static string BuildFullRoute(string? prefix, string? template, string controllerName, string actionName)
        {
            // An action template starting with "/" or "~/" is absolute: ASP.NET routing ignores the controller prefix.
            var isAbsoluteTemplate = template != null && (template.StartsWith("/") || template.StartsWith("~/"));
            if (isAbsoluteTemplate)
                return ReplaceRouteTokens($"/{template!.TrimStart('~').TrimStart('/').TrimEnd('/')}", controllerName, actionName);

            var parts = new List<string>();

            if (!string.IsNullOrEmpty(prefix))
                parts.Add(prefix!.TrimStart('/').TrimEnd('/'));

            if (!string.IsNullOrEmpty(template))
                parts.Add(template!.TrimStart('/').TrimEnd('/'));

            return ReplaceRouteTokens($"/{string.Join("/", parts)}", controllerName, actionName);
        }

        /// <summary>
        /// ASP.NET Core replaces <c>[controller]</c> and <c>[action]</c> in every attribute route, an absolute one
        /// included, case-insensitively: by the class name without "Controller" and the action's route name.
        /// </summary>
        private static string ReplaceRouteTokens(string route, string controllerName, string actionName)
        {
            var controllerToken = TrimSuffix(controllerName, "Controller").ToLowerInvariant();
            var actionToken = actionName.ToLowerInvariant();

            // An evaluator inserts the token as text: as a replacement pattern, an [ActionName] holding "$&" would be
            // read as a substitution.
            var routeWithController = Regex.Replace(route, @"\[controller\]", _ => controllerToken, RegexOptions.IgnoreCase);

            return Regex.Replace(routeWithController, @"\[action\]", _ => actionToken, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// The name MVC gives an action: an <c>[ActionName]</c>'s, declared on the action or inherited from the base
        /// action it overrides, else the method name without the "Async" suffix MVC drops by default
        /// (<c>SuppressAsyncSuffixInActionNames</c>), which it never applies to an explicit name.
        /// </summary>
        private static string GetRouteActionName(IMethodSymbol method)
        {
            var actionNameAttribute = FindNearestAttribute(method, x => x.OverriddenMethod, x => x.AttributeClass?.Name == "ActionNameAttribute");
            var explicitActionName = actionNameAttribute?.ConstructorArguments.FirstOrDefault().Value as string;

            return string.IsNullOrEmpty(explicitActionName)
                ? TrimSuffix(method.Name, AsyncSuffix)
                : explicitActionName!;
        }

        private static string TrimSuffix(string name, string suffix)
        {
            return name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }

        private static string? GetXmlDocSummary(IMethodSymbol method)
        {
            var xml = method.GetDocumentationCommentXml();
            if (string.IsNullOrEmpty(xml))
                return null;

            var start = xml!.IndexOf("<summary>");
            var end = xml.IndexOf("</summary>");
            if (start >= 0 && end > start)
            {
                var summary = xml.Substring(start + 9, end - start - 9).Trim();
                // Clean up whitespace
                summary = string.Join(" ", summary.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                return summary;
            }
            return null;
        }

        // ASP.NET Core URL-segment versioning uses an "apiVersion"-constrained placeholder
        // (e.g. "api/v{version:apiVersion}/[controller]") whose value is supplied at runtime by
        // the versioning middleware, not by an action parameter. Bake the concrete version from
        // [ApiVersion] into the route so the generated tool targets a fixed version and compiles.
        private static string ResolveApiVersionInRoute(string route, INamedTypeSymbol controller, IMethodSymbol method)
        {
            if (route.IndexOf(":apiVersion", StringComparison.OrdinalIgnoreCase) < 0)
                return route;

            var version = GetApiVersion(method) ?? GetApiVersion(controller) ?? "1.0";

            return System.Text.RegularExpressions.Regex.Replace(
                route, @"\{[^{}]*:apiVersion[^{}]*\}", version,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string? GetApiVersion(ISymbol symbol)
        {
            var attribute = symbol.GetAttributes()
                .LastOrDefault(a => a.AttributeClass?.Name == "ApiVersionAttribute");

            if (attribute == null || attribute.ConstructorArguments.Length == 0)
                return null;

            var args = attribute.ConstructorArguments;

            if (args.Length >= 2 && args[0].Value is int major && args[1].Value is int minor)
                return $"{major}.{minor}";

            var value = args[0].Value;
            switch (value)
            {
                case string s:
                    return s;
                case double d:
                    return d.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return value?.ToString();
            }
        }

        private static Dictionary<string, string> GetXmlDocParams(IMethodSymbol method)
        {
            var result = new Dictionary<string, string>();

            var xml = method.GetDocumentationCommentXml();
            if (string.IsNullOrEmpty(xml))
                return result;

            var matches = System.Text.RegularExpressions.Regex.Matches(
                xml!, "<param name=\"([^\"]+)\">(.*?)</param>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var name = match.Groups[1].Value;
                var text = match.Groups[2].Value;
                text = string.Join(" ", text.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(text))
                    result[name] = text;
            }

            return result;
        }

        /// <summary>
        /// The scope an <c>[McpExpose]</c> on an action or a controller names, <c>McpScope.None</c> included, or null
        /// when it names none. Telling the two apart is what lets an action opt out of its controller's scope.
        /// </summary>
        private static McpScopeModel? GetDeclaredScope(AttributeData? mcpExposeAttribute)
        {
            if (mcpExposeAttribute is null)
            {
                return null;
            }

            foreach (var namedArgument in mcpExposeAttribute.NamedArguments)
            {
                // An enum argument arrives as its underlying value; McpScopeModel mirrors McpScope's values.
                if (namedArgument.Key == "Scope" && namedArgument.Value.Value is int scopeValue)
                {
                    return (McpScopeModel)scopeValue;
                }
            }

            return null;
        }

        /// <summary>
        /// Each hint as the action's <c>[McpExpose]</c> sets it, else as its controller's does, else derived as the more
        /// cautious reading of scope and HTTP method: read-only when the scope is read only and the method is not PUT, PATCH
        /// or DELETE, since a scope is a permission and a loose one must not make a state-changing method look safe (POST
        /// follows the scope, as it serves queries too); destructive when not read-only and a PUT, PATCH or DELETE or a
        /// scope with delete, because the spec reserves a false destructive hint for purely additive updates, which of the
        /// write methods only POST makes; idempotent when read-only or a PUT or DELETE; and never open-world, since a
        /// generated tool calls the host's own API.
        /// </summary>
        private static ToolAnnotationsModel GetToolAnnotations(
            string httpMethod,
            McpScopeModel requiredScope,
            AttributeData? actionMcpExposeAttribute,
            AttributeData? controllerMcpExposeAttribute)
        {
            bool? GetDeclaredHint(string propertyName)
            {
                return GetDeclaredBool(actionMcpExposeAttribute, propertyName) ?? GetDeclaredBool(controllerMcpExposeAttribute, propertyName);
            }

            var isNonAdditiveMethod = httpMethod is HttpMethodNames.Put or HttpMethodNames.Patch or HttpMethodNames.Delete;
            var hasReadOnlyScope = (requiredScope & (McpScopeModel.Write | McpScopeModel.Delete)) == McpScopeModel.None;
            var hasDeleteScope = (requiredScope & McpScopeModel.Delete) != McpScopeModel.None;
            var isIdempotentMethod = httpMethod is HttpMethodNames.Put or HttpMethodNames.Delete;
            var isReadOnly = GetDeclaredHint(ReadOnlyHintPropertyName) ?? (hasReadOnlyScope && !isNonAdditiveMethod);

            return new ToolAnnotationsModel
            {
                IsReadOnly = isReadOnly,
                IsDestructive = GetDeclaredHint(DestructiveHintPropertyName) ?? (!isReadOnly && (isNonAdditiveMethod || hasDeleteScope)),
                IsIdempotent = GetDeclaredHint(IdempotentHintPropertyName) ?? (isReadOnly || isIdempotentMethod),
                IsOpenWorld = GetDeclaredHint(OpenWorldHintPropertyName) ?? false
            };
        }

        /// <summary>
        /// The value an attribute's named argument sets, or null when the attribute does not set it - which a source
        /// generator can tell apart from an explicit false, unlike reflection.
        /// </summary>
        private static bool? GetDeclaredBool(AttributeData? attribute, string propertyName)
        {
            if (attribute is null)
            {
                return null;
            }

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == propertyName && namedArgument.Value.Value is bool value)
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// The matching attribute a symbol declares, else the one its nearest base controller or overridden action
        /// declares. <c>[McpExpose]</c> and MVC's <c>[ActionName]</c> are inherited, while Roslyn's <c>GetAttributes()</c>
        /// returns only what a symbol declares itself; as with reflection, the nearest attribute replaces the base's as
        /// a whole.
        /// </summary>
        internal static AttributeData? FindNearestAttribute<TSymbol>(TSymbol symbol, Func<TSymbol, TSymbol?> getBase, Func<AttributeData, bool> isMatch)
            where TSymbol : class, ISymbol
        {
            for (var current = symbol; current is not null; current = getBase(current))
            {
                var attribute = current.GetAttributes().FirstOrDefault(isMatch);
                if (attribute is not null)
                {
                    return attribute;
                }
            }

            return null;
        }

        private static bool IsMcpExposeAttribute(AttributeData attribute)
        {
            return attribute.AttributeClass?.ToDisplayString() == McpExposeAttributeFullName;
        }

        /// <summary>
        /// Whether MVC applies the <c>[ApiController]</c> conventions to the controller: it does when an attribute
        /// implementing <c>IApiBehaviorMetadata</c>, <c>[ApiController]</c> itself or one derived from it, is on the
        /// controller, on a base class, as <c>[ApiController]</c> is inherited, or on the controller's assembly.
        /// </summary>
        private static bool IsApiController(INamedTypeSymbol controller)
        {
            return FindNearestAttribute(controller, x => x.BaseType, IsApiBehaviorAttribute) is not null ||
                controller.ContainingAssembly.GetAttributes().Any(IsApiBehaviorAttribute);
        }

        private static bool IsApiBehaviorAttribute(AttributeData attribute)
        {
            return attribute.AttributeClass?.AllInterfaces.Any(x => x.ToDisplayString() == ApiBehaviorMetadataFullName) == true;
        }

        private static McpScopeModel GetRequiredScope(string httpMethod)
        {
            switch (httpMethod.ToUpperInvariant())
            {
                // Safe methods - read-only, no server state modification
                case HttpMethodNames.Get:
                    return McpScopeModel.Read;
                case HttpMethodNames.Head:
                    return McpScopeModel.Read;
                case HttpMethodNames.Options:
                    return McpScopeModel.Read;

                // State-modifying methods - create or update resources
                case HttpMethodNames.Post:
                    return McpScopeModel.Write;
                case HttpMethodNames.Put:
                    return McpScopeModel.Write;
                case HttpMethodNames.Patch:
                    return McpScopeModel.Write;

                // Destructive method - separate from Write for fine-grained permission control
                case HttpMethodNames.Delete:
                    return McpScopeModel.Delete;

                // Unknown methods default to Read (principle of least privilege)
                default:
                    return McpScopeModel.Read;
            }
        }
    }
}
