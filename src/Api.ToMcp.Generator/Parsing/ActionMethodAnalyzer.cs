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
        private const string NonActionAttributeFullName = "Microsoft.AspNetCore.Mvc.NonActionAttribute";
        internal const string RouteAttributeName = "RouteAttribute";
        private const string RouteTemplateProviderFullName = "Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider";
        private const string ActionNameAttributeName = "ActionNameAttribute";
        private const string ApiVersionAttributeName = "ApiVersionAttribute";
        private const string FromRouteAttributeName = "FromRouteAttribute";
        private const string FromQueryAttributeName = "FromQueryAttribute";
        private const string FromBodyAttributeName = "FromBodyAttribute";
        private const string FromHeaderAttributeName = "FromHeaderAttribute";
        private const string FromServicesAttributeName = "FromServicesAttribute";
        private const string FromQueryNamePropertyName = "Name";
        private const string ScopePropertyName = "Scope";
        private const string ToolNamePropertyName = "ToolName";
        private const string TitlePropertyName = "Title";
        private const string DescriptionPropertyName = "Description";
        private const string AsyncSuffix = "Async";
        private const string ReadOnlyHintPropertyName = "ReadOnly";
        private const string DestructiveHintPropertyName = "Destructive";
        private const string IdempotentHintPropertyName = "Idempotent";
        private const string OpenWorldHintPropertyName = "OpenWorld";

        /// <summary>
        /// The HTTP method each of MVC's <c>[Http*]</c> attributes routes, by the attribute's simple name.
        /// </summary>
        private static readonly Dictionary<string, string> HttpMethodAttributes = new Dictionary<string, string>
        {
            ["HttpGetAttribute"] = HttpMethodNames.Get,
            ["HttpPostAttribute"] = HttpMethodNames.Post,
            ["HttpPutAttribute"] = HttpMethodNames.Put,
            ["HttpDeleteAttribute"] = HttpMethodNames.Delete,
            ["HttpPatchAttribute"] = HttpMethodNames.Patch
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

            foreach (var method in GetPublicMethods(controller))
            {
                ct.ThrowIfCancellationRequested();

                if (!IsAction(method))
                    continue;

                var httpInfo = GetHttpMethodInfo(method);
                if (httpInfo == null)
                    continue;

                var parameters = AnalyzeParameters(method, httpInfo.Value.Method);
                var returnType = AnalyzeReturnType(method);
                var responseType = ResponseTypeResolver.Resolve(method, controller, compilation);

                var fullRoute = BuildFullRoute(routePrefix, httpInfo.Value.Template, controller.Name, GetRouteActionName(method));
                fullRoute = ResolveApiVersionInRoute(fullRoute, controller, method);

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

                // Like scope and hints, exposure, tool name, description and title follow the nearest action-level
                // [McpExpose], an overridden base action's included; a controller's title is never read, because one
                // title cannot name all its actions.
                if (actionMcpExposeAttr != null)
                {
                    foreach (var namedArg in actionMcpExposeAttr.NamedArguments)
                    {
                        if (namedArg.Key == ToolNamePropertyName && namedArg.Value.Value is string tn)
                            customToolName = tn;
                        if (namedArg.Key == DescriptionPropertyName && namedArg.Value.Value is string desc)
                            customDescription = desc;
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
                    HasMcpExposeAttribute = actionMcpExposeAttr != null,
                    HasMcpIgnoreAttribute = FindNearestAttribute(method, x => x.OverriddenMethod, IsMcpIgnoreAttribute) != null,
                    XmlDocSummary = GetXmlDocSummary(method),
                    CustomToolName = customToolName,
                    Title = customTitle ?? ToolTitleBuilder.Build(customToolName, TrimSuffix(method.Name, AsyncSuffix)),
                    CustomDescription = customDescription,
                    RequiredScope = requiredScope,
                    Annotations = GetToolAnnotations(httpInfo.Value.Method, requiredScope, actionMcpExposeAttr, controllerMcpExposeAttr),
                    OutputSchemaType = responseType.SuccessType is null ? null : ResponseTypeResolver.ToTypeOfOperand(responseType.SuccessType),
                    IsOutputNullable = responseType.SuccessType is null ||
                        responseType.HasBodylessSuccess ||
                        EmptyResponseAnalyzer.CanSucceedWithoutBody(method, compilation, ct),
                    Location = method.Locations.FirstOrDefault(x => x.IsInSource)
                });
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// The public methods MVC lists for a controller, as reflection's <c>GetMethods()</c> lists them: the class's own,
        /// then those of each base class up to the framework's <c>ControllerBase</c>, whose own are never actions. A
        /// method an override replaces is listed once, as the most derived override; one hidden with <c>new</c> is
        /// listed beside the method that hides it, as reflection lists it.
        /// </summary>
        private static IEnumerable<IMethodSymbol> GetPublicMethods(INamedTypeSymbol controller)
        {
            var overriddenMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

            for (var type = controller; type != null && !IsFrameworkBaseType(type); type = type.BaseType)
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (overriddenMethods.Contains(method))
                        continue;

                    for (var overriddenMethod = method.OverriddenMethod; overriddenMethod != null; overriddenMethod = overriddenMethod.OverriddenMethod)
                        overriddenMethods.Add(overriddenMethod);

                    if (method.DeclaredAccessibility == Accessibility.Public)
                        yield return method;
                }
            }
        }

        private static bool IsFrameworkBaseType(INamedTypeSymbol type)
        {
            return type.SpecialType == SpecialType.System_Object ||
                type.ToDisplayString() is KnownTypeNames.ControllerBase or KnownTypeNames.Controller;
        }

        /// <summary>
        /// MVC's rule for which public methods of a controller are actions: an ordinary instance method without type
        /// parameters and without <c>[NonAction]</c>, which an override inherits from the method it replaces.
        /// </summary>
        private static bool IsAction(IMethodSymbol method)
        {
            return method.MethodKind == MethodKind.Ordinary &&
                !method.IsStatic &&
                !method.IsAbstract &&
                !method.IsGenericMethod &&
                FindNearestAttribute(method, x => x.OverriddenMethod, x => x.AttributeClass?.ToDisplayString() == NonActionAttributeFullName) is null;
        }

        /// <summary>
        /// The HTTP method and template of the action's route attributes, taken as MVC takes them, as a whole: from the
        /// action if it declares any (<c>[Route]</c>, an <c>[Http*]</c> or another route template provider), else from the
        /// method it overrides as first declared, skipping the overrides in between, as reflection's
        /// <c>GetBaseDefinition()</c> does. Null when those attributes name no HTTP method the generator calls, such as a
        /// <c>[Route]</c> alone, which MVC serves for every method.
        /// </summary>
        private static (string Method, string? Template)? GetHttpMethodInfo(IMethodSymbol method)
        {
            var routedMethod = method.GetAttributes().Any(IsRouteTemplateProvider) ? method : GetBaseDefinition(method);

            return GetDeclaredHttpMethodInfo(routedMethod);
        }

        private static IMethodSymbol GetBaseDefinition(IMethodSymbol method)
        {
            var definition = method;
            while (definition.OverriddenMethod != null)
                definition = definition.OverriddenMethod;

            return definition;
        }

        /// <summary>
        /// An attribute MVC reads an action's route from: one implementing <c>IRouteTemplateProvider</c>, which also covers
        /// <c>[HttpHead]</c>, <c>[AcceptVerbs]</c> and custom ones, or one the generator knows by name, as it does elsewhere.
        /// </summary>
        private static bool IsRouteTemplateProvider(AttributeData attribute)
        {
            var attributeClass = attribute.AttributeClass;

            return attributeClass != null &&
                (attributeClass.Name == RouteAttributeName ||
                    HttpMethodAttributes.ContainsKey(attributeClass.Name) ||
                    attributeClass.AllInterfaces.Any(x => x.ToDisplayString() == RouteTemplateProviderFullName));
        }

        private static (string Method, string? Template)? GetDeclaredHttpMethodInfo(IMethodSymbol method)
        {
            // A method-level [Route("...")] supplies the template when the HTTP verb
            // attribute carries none (e.g. [HttpGet] + [Route("search")]).
            var routeAttrTemplate = method.GetAttributes()
                .FirstOrDefault(attr => attr.AttributeClass?.Name == RouteAttributeName &&
                                        attr.ConstructorArguments.Length > 0)
                ?.ConstructorArguments[0].Value?.ToString();

            foreach (var attr in method.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name;
                if (attrName == null || !HttpMethodAttributes.TryGetValue(attrName, out var httpMethod))
                    continue;

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

                // Extract properties from complex types with [FromRoute] or [FromBody]
                if ((source == ParameterSourceModel.Route || source == ParameterSourceModel.Body) && IsComplexType(param.Type))
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
                .Where(x => x.AttributeClass?.Name == FromQueryAttributeName)
                .SelectMany(x => x.NamedArguments)
                .Where(x => x.Key == FromQueryNamePropertyName)
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
                    case FromRouteAttributeName:
                        return ParameterSourceModel.Route;
                    case FromQueryAttributeName:
                        return ParameterSourceModel.Query;
                    case FromBodyAttributeName:
                        return ParameterSourceModel.Body;
                    case FromHeaderAttributeName:
                        return ParameterSourceModel.Header;
                    case FromServicesAttributeName:
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

            var fullName = type.ToDisplayString();

            // Common primitive types
            if (fullName.StartsWith("System."))
            {
                var primitives = new[]
                {
                    "System.String", "System.Int32", "System.Int64", "System.Boolean",
                    "System.Double", "System.Decimal", "System.DateTime", "System.DateTimeOffset",
                    "System.Guid", "System.TimeSpan"
                };
                if (primitives.Any(p => fullName.StartsWith(p)))
                    return false;
            }

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
            var actionNameAttribute = FindNearestAttribute(method, x => x.OverriddenMethod, x => x.AttributeClass?.Name == ActionNameAttributeName);
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
                .LastOrDefault(a => a.AttributeClass?.Name == ApiVersionAttributeName);

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
                if (namedArgument.Key == ScopePropertyName && namedArgument.Value.Value is int scopeValue)
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

        private static bool IsMcpIgnoreAttribute(AttributeData attribute)
        {
            return attribute.AttributeClass?.ToDisplayString() == McpIgnoreAttributeFullName;
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
