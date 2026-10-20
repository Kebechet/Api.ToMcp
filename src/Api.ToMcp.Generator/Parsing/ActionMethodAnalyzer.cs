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
        private const string HttpGetAttributeName = "HttpGetAttribute";
        private const string HttpPostAttributeName = "HttpPostAttribute";
        private const string HttpPutAttributeName = "HttpPutAttribute";
        private const string HttpDeleteAttributeName = "HttpDeleteAttribute";
        private const string HttpPatchAttributeName = "HttpPatchAttribute";
        private const string RouteTemplateProviderFullName = "Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider";
        private const string ActionNameAttributeName = "ActionNameAttribute";
        private const string ApiVersionAttributeName = "ApiVersionAttribute";
        private const string ScopePropertyName = "Scope";
        private const string XmlDocSummaryStartTag = "<summary>";
        private const string XmlDocSummaryEndTag = "</summary>";

        /// <summary>
        /// A <c>&lt;param name="..."&gt;</c> element of an XML doc comment: its name, then its text.
        /// </summary>
        private const string XmlDocParamPattern = "<param name=\"([^\"]+)\">(.*?)</param>";
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
            [HttpGetAttributeName] = HttpMethodNames.Get,
            [HttpPostAttributeName] = HttpMethodNames.Post,
            [HttpPutAttributeName] = HttpMethodNames.Put,
            [HttpDeleteAttributeName] = HttpMethodNames.Delete,
            [HttpPatchAttributeName] = HttpMethodNames.Patch
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

            foreach (var method in GetPublicMethods(controller))
            {
                ct.ThrowIfCancellationRequested();

                if (!IsAction(method))
                    continue;

                var httpInfo = GetHttpMethodInfo(method);
                if (httpInfo == null)
                    continue;

                var parameters = AnalyzeParameters(method, httpInfo.Value.Method, isApiController);
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
                    IsApiController = isApiController,
                    RequiredScope = requiredScope,
                    Annotations = GetToolAnnotations(httpInfo.Value.Method, requiredScope, actionMcpExposeAttr, controllerMcpExposeAttr),
                    OutputSchemaType = responseType.SuccessType is null ? null : ResponseTypeResolver.ToTypeOfOperand(responseType.SuccessType),
                    IsOutputNullable = responseType.SuccessType is null ||
                        responseType.HasBodylessSuccess ||
                        EmptyResponseAnalyzer.CanSucceedWithoutBody(method, compilation, ct),
                    SourceLocation = SourceLocationModel.From(method.Locations.FirstOrDefault(x => x.IsInSource))
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

        internal static bool IsFrameworkBaseType(INamedTypeSymbol type)
        {
            return type.SpecialType == SpecialType.System_Object ||
                type.ToDisplayString() is KnownTypeNames.ControllerBase or KnownTypeNames.Controller;
        }

        /// <summary>
        /// MVC's rule for which public methods of a controller are actions (<c>DefaultApplicationModelProvider.IsAction</c>):
        /// an ordinary instance method without type parameters and without <c>[NonAction]</c>, which an override inherits
        /// from the method it replaces, that was not first declared on <c>object</c>, such as an override of
        /// <c>ToString()</c>, and does not implement <c>IDisposable.Dispose</c>.
        /// </summary>
        private static bool IsAction(IMethodSymbol method)
        {
            var baseDefinition = GetBaseDefinition(method);

            return method.MethodKind == MethodKind.Ordinary &&
                !method.IsStatic &&
                !method.IsAbstract &&
                !method.IsGenericMethod &&
                baseDefinition.ContainingType.SpecialType != SpecialType.System_Object &&
                !IsDisposeImplementation(baseDefinition) &&
                FindNearestAttribute(method, x => x.OverriddenMethod, x => x.AttributeClass?.ToDisplayString() == NonActionAttributeFullName) is null;
        }

        /// <summary>
        /// Whether the method, as first declared, is how its class implements <c>IDisposable.Dispose</c>. MVC checks the
        /// first declaration, so an override of it is no action either, while a method hiding it with <c>new</c> is one.
        /// </summary>
        private static bool IsDisposeImplementation(IMethodSymbol baseDefinition)
        {
            var declaringType = baseDefinition.ContainingType;
            var dispose = declaringType.AllInterfaces
                .FirstOrDefault(x => x.SpecialType == SpecialType.System_IDisposable)?
                .GetMembers(nameof(IDisposable.Dispose))
                .FirstOrDefault();

            return dispose != null &&
                SymbolEqualityComparer.Default.Equals(declaringType.FindImplementationForInterfaceMember(dispose), baseDefinition);
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
            string httpMethod,
            bool isApiController)
        {
            var builder = ImmutableArray.CreateBuilder<ParameterInfoModel>();
            var paramDocs = GetXmlDocParams(method);

            foreach (var param in method.Parameters)
            {
                var bindingAttributes = GetBindingAttributes(param);
                var declaredSource = GetDeclaredParameterSource(bindingAttributes);

                // Skip service-injected parameters
                if (declaredSource == ParameterSourceModel.Services)
                    continue;

                // Skip CancellationToken
                if (param.Type.ToDisplayString() == KnownTypeNames.CancellationToken)
                    continue;

                var isSimpleType = SimpleTypeClassifier.IsSimpleType(param.Type);

                // A [FromRoute] or [FromBody] parameter's properties fill route values whatever its type, as a body is JSON
                // even for a type MVC reads from one string. A complex parameter's also decide whether MVC reads it from
                // the body, and which of them fill the route or the query string when it does not.
                var properties = isSimpleType && declaredSource is not (ParameterSourceModel.Route or ParameterSourceModel.Body)
                    ? ImmutableArray<PropertyInfoModel>.Empty
                    : ExtractProperties(param.Type);

                builder.Add(new ParameterInfoModel
                {
                    Name = param.Name,
                    Type = param.Type.ToDisplayString(),
                    IsNullable = param.Type.NullableAnnotation == NullableAnnotation.Annotated ||
                                 param.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
                    HasDefaultValue = param.HasExplicitDefaultValue,
                    DefaultValue = param.HasExplicitDefaultValue ? param.ExplicitDefaultValue : null,
                    Source = declaredSource ?? InferParameterSource(param.Type, httpMethod, isApiController, properties),
                    IsSimpleType = isSimpleType,
                    Properties = properties,
                    Description = paramDocs.TryGetValue(param.Name, out var doc) ? doc : null,
                    QueryName = GetFromQueryName(bindingAttributes)
                });
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// The attributes MVC reads a parameter's binding source and the name it binds under from: the parameter's own, then
        /// those of the parameter at its position in each method the action overrides, nearest first. MVC reads them as
        /// reflection inherits them, so an override without <c>[FromQuery]</c> binds as its base action declares, even
        /// under another parameter name, while a nearer attribute replaces a base one of the same class whole: an
        /// override's bare <c>[FromQuery]</c> drops the base's <c>[FromQuery(Name = "c")]</c>, name included.
        /// </summary>
        private static List<AttributeData> GetBindingAttributes(IParameterSymbol parameter)
        {
            var attributes = new List<AttributeData>(parameter.GetAttributes());
            for (var method = (parameter.ContainingSymbol as IMethodSymbol)?.OverriddenMethod; method != null; method = method.OverriddenMethod)
            {
                var nearerClasses = attributes.Select(x => x.AttributeClass).ToList();
                attributes.AddRange(method.Parameters[parameter.Ordinal].GetAttributes()
                    .Where(x => !nearerClasses.Contains(x.AttributeClass, SymbolEqualityComparer.Default)));
            }

            return attributes;
        }

        private static string? GetFromQueryName(IEnumerable<AttributeData> attributes)
        {
            return attributes
                .Where(x => x.AttributeClass?.Name == BindingAttributeNames.FromQuery)
                .Select(GetModelName)
                .FirstOrDefault(x => x is not null);
        }

        /// <summary>
        /// The properties MVC binds a type through, each with where its binding attribute makes MVC read it from and the
        /// name it binds it under.
        /// </summary>
        private static ImmutableArray<PropertyInfoModel> ExtractProperties(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
            }

            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                return ImmutableArray<PropertyInfoModel>.Empty;
            }

            var properties = GetVisibleProperties(type);
            var constructorParameters = GetRecordConstructorParameters(type, properties);

            return properties
                .Select(property =>
                {
                    // MVC binds a property the record's constructor sets through the constructor parameter, with the
                    // parameter's binding attributes; a [property: ...] one it ignores.
                    var isSetByConstructor = constructorParameters.TryGetValue(property, out var constructorParameter);
                    var attributes = isSetByConstructor ? constructorParameter!.GetAttributes() : property.GetAttributes();

                    return new PropertyInfoModel
                    {
                        Name = property.Name,
                        Type = property.Type.ToDisplayString(),
                        IsNullable = property.Type.NullableAnnotation == NullableAnnotation.Annotated ||
                                     property.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
                        ModelName = attributes.Select(GetModelName).FirstOrDefault(x => x is not null),
                        Source = GetBindingSource(attributes),
                        IsSetByConstructor = isSetByConstructor
                    };
                })
                .ToImmutableArray();
        }

        /// <summary>
        /// The properties MVC binds (<c>PropertyHelper.GetVisibleProperties</c>): public ones with a public getter, those
        /// the type inherits included, a property hiding a base one of the same name. A property whose getter is not
        /// public MVC never binds, and the generated tool could not read it.
        /// </summary>
        private static List<IPropertySymbol> GetVisibleProperties(ITypeSymbol type)
        {
            var properties = new List<IPropertySymbol>();
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (!(member is IPropertySymbol property))
                        continue;

                    if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer)
                        continue;

                    if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public)
                        continue;

                    if (!propertyNames.Add(property.Name))
                        continue;

                    properties.Add(property);
                }
            }

            return properties;
        }

        /// <summary>
        /// The properties a record's primary constructor sets, each with the parameter that sets it, which MVC binds
        /// through that constructor (<c>DefaultBindingMetadataProvider.GetBoundConstructor</c>): of a record class that is
        /// not abstract and has exactly one public constructor, every parameter of which has a property of the same name
        /// and type.
        /// </summary>
        private static Dictionary<IPropertySymbol, IParameterSymbol> GetRecordConstructorParameters(ITypeSymbol type, List<IPropertySymbol> properties)
        {
            var constructorParameters = new Dictionary<IPropertySymbol, IParameterSymbol>(SymbolEqualityComparer.Default);
            if (!(type is INamedTypeSymbol { IsRecord: true, IsAbstract: false, TypeKind: TypeKind.Class } record))
            {
                return constructorParameters;
            }

            var publicConstructors = record.InstanceConstructors
                .Where(x => x.DeclaredAccessibility == Accessibility.Public)
                .ToList();
            if (publicConstructors.Count != 1 || publicConstructors[0].Parameters.Length == 0)
            {
                return constructorParameters;
            }

            foreach (var parameter in publicConstructors[0].Parameters)
            {
                var property = properties.FirstOrDefault(x =>
                    string.Equals(x.Name, parameter.Name, StringComparison.Ordinal) &&
                    SymbolEqualityComparer.Default.Equals(x.Type, parameter.Type));
                if (property is null)
                {
                    // Not a primary constructor: MVC binds the record's properties like any other type's.
                    return new Dictionary<IPropertySymbol, IParameterSymbol>(SymbolEqualityComparer.Default);
                }

                constructorParameters[property] = parameter;
            }

            return constructorParameters;
        }

        /// <summary>
        /// The name an attribute implementing <c>IModelNameProvider</c>, such as <c>[FromQuery(Name = "q")]</c>, makes MVC
        /// bind a value under, or null when it names none.
        /// </summary>
        private static string? GetModelName(AttributeData attribute)
        {
            if (!Implements(attribute, BindingAttributeNames.ModelNameProvider))
            {
                return null;
            }

            return attribute.NamedArguments
                .Where(x => x.Key == BindingAttributeNames.ModelNamePropertyName)
                .Select(x => x.Value.Value as string)
                .FirstOrDefault(x => !string.IsNullOrEmpty(x));
        }

        /// <summary>
        /// Where the first of a property's or constructor parameter's attributes that gives a binding source makes MVC read
        /// it from, as <c>DefaultBindingMetadataProvider</c> takes that one.
        /// </summary>
        private static ParameterSourceModel GetBindingSource(ImmutableArray<AttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                switch (attribute.AttributeClass?.Name)
                {
                    case BindingAttributeNames.FromRoute:
                        return ParameterSourceModel.Route;
                    case BindingAttributeNames.FromQuery:
                        return ParameterSourceModel.Query;
                }

                if (GivesBindingSource(attribute))
                {
                    return ParameterSourceModel.Other;
                }
            }

            return ParameterSourceModel.Auto;
        }

        /// <summary>
        /// Whether an attribute implementing <c>IBindingSourceMetadata</c> gives a binding source. <c>[ModelBinder]</c> and
        /// <c>[BindProperty]</c> give one only together with a binder type, so <c>[ModelBinder(Name = "c")]</c> merely
        /// renames the key. Any other attribute, one derived from those two included, is taken to give one, as its
        /// <c>BindingSource</c> getter runs only in the app.
        /// </summary>
        private static bool GivesBindingSource(AttributeData attribute)
        {
            if (!Implements(attribute, BindingAttributeNames.BindingSourceMetadata))
            {
                return false;
            }

            if (attribute.AttributeClass?.ToDisplayString() is not (BindingAttributeNames.ModelBinder or BindingAttributeNames.BindProperty))
            {
                return true;
            }

            return attribute.ConstructorArguments.Any(x => x.Value is not null) ||
                attribute.NamedArguments.Any(x => x.Key == BindingAttributeNames.BinderTypePropertyName && x.Value.Value is not null);
        }

        private static ParameterSourceModel? GetDeclaredParameterSource(IEnumerable<AttributeData> attributes)
        {
            foreach (var attr in attributes)
            {
                var attrName = attr.AttributeClass?.Name;
                switch (attrName)
                {
                    case BindingAttributeNames.FromRoute:
                        return ParameterSourceModel.Route;
                    case BindingAttributeNames.FromQuery:
                        return ParameterSourceModel.Query;
                    case BindingAttributeNames.FromBody:
                        return ParameterSourceModel.Body;
                    case BindingAttributeNames.FromHeader:
                        return ParameterSourceModel.Header;
                    case BindingAttributeNames.FromServices:
                        return ParameterSourceModel.Services;
                }
            }

            return null;
        }

        /// <summary>
        /// The body for a complex type <c>[ApiController]</c> reads from it, on the methods a tool sends a body with. MVC
        /// infers no source for a type one of whose properties names a binding source of its own, and none at all without
        /// <c>[ApiController]</c>; such a parameter, like a simple one, is bound from the route, query and form values,
        /// which the emitter tells apart.
        /// </summary>
        private static ParameterSourceModel InferParameterSource(
            ITypeSymbol type,
            string httpMethod,
            bool isApiController,
            ImmutableArray<PropertyInfoModel> properties)
        {
            var isBodyMethod = httpMethod is HttpMethodNames.Post or HttpMethodNames.Put or HttpMethodNames.Patch;
            var hasBindingSourceProperties = properties.Any(x => x.NamesBindingSource);

            return isApiController && isBodyMethod && IsComplexType(type) && !hasBindingSourceProperties
                ? ParameterSourceModel.Body
                : ParameterSourceModel.Auto;
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
            var controllerToken = TrimSuffix(controllerName, ConventionNames.ControllerSuffix).ToLowerInvariant();
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

        /// <summary>
        /// The action's XML <c>&lt;summary&gt;</c>, else that of the method it overrides, and so on up: an override
        /// without one of its own, or with only <c>&lt;inheritdoc/&gt;</c>, means the description of the action it replaces.
        /// </summary>
        private static string? GetXmlDocSummary(IMethodSymbol method)
        {
            for (var current = method; current != null; current = current.OverriddenMethod)
            {
                var summary = GetDeclaredXmlDocSummary(current);
                if (!string.IsNullOrEmpty(summary))
                    return summary;
            }

            return null;
        }

        private static string? GetDeclaredXmlDocSummary(IMethodSymbol method)
        {
            var xml = method.GetDocumentationCommentXml();
            if (string.IsNullOrEmpty(xml))
                return null;

            var start = xml!.IndexOf(XmlDocSummaryStartTag);
            var end = xml.IndexOf(XmlDocSummaryEndTag);
            if (start >= 0 && end > start)
            {
                var contentStart = start + XmlDocSummaryStartTag.Length;
                var summary = xml.Substring(contentStart, end - contentStart).Trim();
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

        /// <summary>
        /// The XML <c>&lt;param&gt;</c> docs of the action's parameters, by name. A parameter the action leaves undocumented
        /// takes the docs of the method it overrides, and so on up, matched by position, as an override may rename it.
        /// </summary>
        private static Dictionary<string, string> GetXmlDocParams(IMethodSymbol method)
        {
            var result = new Dictionary<string, string>();

            for (var current = method; current != null; current = current.OverriddenMethod)
            {
                var declaredDocs = GetDeclaredXmlDocParams(current);
                for (var i = 0; i < method.Parameters.Length; i++)
                {
                    var name = method.Parameters[i].Name;
                    if (!result.ContainsKey(name) && declaredDocs.TryGetValue(current.Parameters[i].Name, out var text))
                        result[name] = text;
                }
            }

            return result;
        }

        private static Dictionary<string, string> GetDeclaredXmlDocParams(IMethodSymbol method)
        {
            var result = new Dictionary<string, string>();

            var xml = method.GetDocumentationCommentXml();
            if (string.IsNullOrEmpty(xml))
                return result;

            var matches = System.Text.RegularExpressions.Regex.Matches(
                xml!, XmlDocParamPattern,
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
            return Implements(attribute, BindingAttributeNames.ApiBehaviorMetadata);
        }

        private static bool Implements(AttributeData attribute, string interfaceFullName)
        {
            return attribute.AttributeClass?.AllInterfaces.Any(x => x.ToDisplayString() == interfaceFullName) == true;
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
