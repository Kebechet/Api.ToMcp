using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Api.ToMcp.Generator.Emitting;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// Whether the config can select the endpoint a <c>Map*</c> call maps, as far as a look at its handler tells.
    /// </summary>
    internal enum EndpointSelection
    {
        /// <summary>
        /// The config may select it, or the generator cannot tell, so it analyzes the endpoint.
        /// </summary>
        Possible,

        /// <summary>
        /// Nothing selects it: no opt-in, no entry naming its class, no <c>[McpExpose]</c>.
        /// </summary>
        LeftOut,

        /// <summary>
        /// <c>[McpIgnore]</c> keeps it from MCP.
        /// </summary>
        Ignored
    }

    /// <summary>
    /// Minimal API endpoints as actions: the same model a controller's action has, so the same emitter writes their tools,
    /// with each parameter's source decided by the Minimal API binding rules
    /// (https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding).
    /// </summary>
    internal static partial class ActionMethodAnalyzer
    {
        /// <summary>
        /// What a derived endpoint name puts before a route value's name: <c>GET /orders/{id}</c> is <c>GetOrdersById</c>.
        /// </summary>
        private const string RouteValueWord = "By";

        /// <summary>
        /// The <c>?</c> that makes a type nullable in the tool's C#.
        /// </summary>
        private const string NullableAnnotationSuffix = "?";

        /// <summary>
        /// What joins the words that tell an endpoint's routes apart to a tool name <c>[McpExpose(ToolName)]</c> gives.
        /// </summary>
        private const string ToolNameSeparator = "_";

        /// <summary>
        /// The HTTP methods for which a Minimal API infers no body (<c>RouteEndpointDataSource.ShouldDisableInferredBodyParameters</c>):
        /// an endpoint mapped for any of them reads a complex parameter without an attribute from the services only.
        /// </summary>
        private static readonly HashSet<string> BodilessHttpMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            HttpMethodNames.Get,
            HttpMethodNames.Delete,
            HttpMethodNames.Head,
            HttpMethodNames.Options,
            HttpMethodNames.Trace,
            HttpMethodNames.Connect
        };

        /// <summary>
        /// The HTTP methods a tool sends a body with.
        /// </summary>
        private static readonly HashSet<string> BodyHttpMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            HttpMethodNames.Post,
            HttpMethodNames.Put,
            HttpMethodNames.Patch
        };

        /// <summary>
        /// The types a Minimal API fills from the request or the server itself, which are not inputs of a tool.
        /// </summary>
        private static readonly HashSet<string> RequestTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            MinimalApiNames.HttpContext,
            MinimalApiNames.HttpRequest,
            MinimalApiNames.HttpResponse,
            MinimalApiNames.ClaimsPrincipal,
            KnownTypeNames.CancellationToken
        };

        private static readonly HashSet<string> FormTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            KnownTypeNames.FormFile,
            KnownTypeNames.FormFileCollection,
            KnownTypeNames.FormCollection
        };

        private static readonly HashSet<string> RawBodyTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            MinimalApiNames.Stream,
            MinimalApiNames.PipeReader
        };

        /// <summary>
        /// Where a Minimal API binds a parameter from, as far as a tool is concerned.
        /// </summary>
        private enum EndpointBinding
        {
            /// <summary>
            /// Not an input of the tool: a service, the request itself, or an optional header.
            /// </summary>
            None,

            /// <summary>
            /// A value parsed from one string: from the route when the route names it, else from the query string.
            /// </summary>
            RouteOrQuery,

            Route,
            Query,
            Body,

            /// <summary>
            /// A complex parameter without an attribute on an endpoint that infers a body: the body, unless the app
            /// registered its type as a service, which only the running app knows.
            /// </summary>
            BodyOrService
        }

        public static bool IsToolHttpMethod(string httpMethod)
        {
            return ToolHttpMethods.Contains(httpMethod);
        }

        /// <summary>
        /// Whether the config can select the endpoint a <c>Map*</c> call maps, read without binding the call: always
        /// with <c>"minimalApis": true</c>, else when an include or exclude entry names the class that declares the
        /// handler, or one of its endpoints, or <c>[McpExpose]</c> marks the handler or the class. The analysis of an
        /// endpoint, its route followed through the app's methods, is what costs, so a project that never opts in skips it.
        /// </summary>
        public static EndpointSelection GetEndpointSelection(
            InvocationExpressionSyntax invocation,
            ExpressionSyntax? handler,
            SemanticModel semanticModel,
            GeneratorConfigModel config,
            CancellationToken ct)
        {
            if (config.MinimalApis == true)
            {
                return EndpointSelection.Possible;
            }

            var (type, methods, attributes) = FindHandlerCheaply(invocation, handler, semanticModel, ct);
            if (type is null)
            {
                return EndpointSelection.Possible;
            }

            var classNames = new[] { type.Name, TrimSuffix(type.Name, ConventionNames.ControllerSuffix) };
            var isNamedByEntry = config.Include.Concat(config.Exclude).Any(entry => entry is not null &&
                classNames.Any(name => entry == name || entry.StartsWith(name + ConventionNames.EntryActionSeparator, StringComparison.Ordinal)));
            if (isNamedByEntry)
            {
                return EndpointSelection.Possible;
            }

            var attributeClasses = attributes
                .Select(x => semanticModel.GetSymbolInfo(x, ct).Symbol?.ContainingType?.ToDisplayString())
                .ToList();
            var isIgnored = FindNearestAttribute(type, x => x.BaseType, IsMcpIgnoreAttribute) is not null ||
                attributeClasses.Contains(McpIgnoreAttributeFullName) ||
                (methods.Count > 0 && methods.All(x => FindNearestAttribute(x, method => method.OverriddenMethod, IsMcpIgnoreAttribute) is not null));
            if (isIgnored)
            {
                return EndpointSelection.Ignored;
            }

            var isExposed = FindNearestAttribute(type, x => x.BaseType, IsMcpExposeAttribute) is not null ||
                attributeClasses.Contains(McpExposeAttributeFullName) ||
                methods.Any(x => FindNearestAttribute(x, method => method.OverriddenMethod, IsMcpExposeAttribute) is not null);

            return isExposed ? EndpointSelection.Possible : EndpointSelection.LeftOut;
        }

        /// <summary>
        /// The class that declares a handler, the methods its name may stand for, and a lambda's attributes, read by binding
        /// the handler's names rather than the call: the class a lambda is written in; the class or the type of the value a
        /// method group's member access starts from; the methods a simple name finds in scope. Null when the handler is
        /// written some other way.
        /// </summary>
        private static (INamedTypeSymbol? Type, List<IMethodSymbol> Methods, IReadOnlyList<AttributeSyntax> Attributes) FindHandlerCheaply(
            InvocationExpressionSyntax invocation,
            ExpressionSyntax? handler,
            SemanticModel semanticModel,
            CancellationToken ct)
        {
            var noMethods = new List<IMethodSymbol>();
            var noAttributes = Array.Empty<AttributeSyntax>();

            var lambda = handler as LambdaExpressionSyntax;
            if (lambda is not null || handler is AnonymousMethodExpressionSyntax)
            {
                var attributes = lambda?.AttributeLists.SelectMany(x => x.Attributes).ToList() ?? new List<AttributeSyntax>();
                return (semanticModel.GetEnclosingSymbol(invocation.SpanStart, ct)?.ContainingType, noMethods, attributes);
            }

            var memberAccess = handler as MemberAccessExpressionSyntax;
            if (memberAccess is not null)
            {
                var receiver = semanticModel.GetSymbolInfo(memberAccess.Expression, ct).Symbol as INamedTypeSymbol ??
                    semanticModel.GetTypeInfo(memberAccess.Expression, ct).Type as INamedTypeSymbol;
                var methods = receiver is null
                    ? noMethods
                    : GetBaseTypes(receiver).Prepend(receiver).SelectMany(x => x.GetMembers(memberAccess.Name.Identifier.ValueText)).OfType<IMethodSymbol>().ToList();

                return (methods.FirstOrDefault()?.ContainingType ?? receiver, methods, noAttributes);
            }

            var identifier = handler as IdentifierNameSyntax;
            if (identifier is not null)
            {
                var methods = semanticModel.LookupSymbols(invocation.SpanStart, name: identifier.Identifier.ValueText).OfType<IMethodSymbol>().ToList();
                return (methods.FirstOrDefault()?.ContainingType, methods, noAttributes);
            }

            return (null, noMethods, noAttributes);
        }

        /// <summary>
        /// The endpoints a <c>Map*</c> call maps, one for each route the groups give it, as actions of the class that
        /// declares the handler. When the route cannot be told, one endpoint carries the problem, at the pattern alone.
        /// </summary>
        public static ControllerInfoModel GetEndpointClass(EndpointDeclaration endpoint, RouteGroups groups, Compilation compilation, CancellationToken ct)
        {
            var type = endpoint.DeclaringType;
            var classMcpExpose = FindNearestAttribute(type, x => x.BaseType, IsMcpExposeAttribute);

            List<(string Route, string? Problem, string NamePrefix)> routes;
            if (endpoint.Pattern is null)
            {
                routes = [("", endpoint.Problem, "")];
            }
            else if (groups.Problem is not null)
            {
                routes = [(RouteGroupResolver.Combine("", endpoint.Pattern), endpoint.Problem ?? groups.Problem, "")];
            }
            else
            {
                var pattern = endpoint.Pattern;
                var prefixes = groups.Prefixes
                    .GroupBy(x => RouteGroupResolver.Combine(x, pattern), StringComparer.Ordinal)
                    .Select(x => x.First())
                    .ToList();
                var namePrefixes = GetDistinguishingWords(prefixes);
                routes = prefixes.Select((x, i) => (RouteGroupResolver.Combine(x, pattern), endpoint.Problem, namePrefixes[i])).ToList();
            }

            var actions = routes
                .Select(x => CreateEndpointAction(endpoint, x.Route, x.Problem, x.NamePrefix, classMcpExpose, compilation, ct))
                .ToImmutableArray();

            return new ControllerInfoModel
            {
                Name = type.Name,
                // The class's namespace or the class it is nested in, which tells apart two classes of the same name.
                Namespace = type.ContainingSymbol?.ToDisplayString() ?? "",
                HasMcpExposeAttribute = classMcpExpose is not null,
                HasMcpIgnoreAttribute = FindNearestAttribute(type, x => x.BaseType, IsMcpIgnoreAttribute) is not null,
                Actions = actions,
                ActionNames = actions.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToImmutableArray()
            };
        }

        /// <param name="namePrefix">
        /// What tells this route of the endpoint apart from its others, when one call maps it at several, put before a name
        /// that does not come from the route.
        /// </param>
        private static ActionInfoModel CreateEndpointAction(
            EndpointDeclaration endpoint,
            string route,
            string? routeProblem,
            string namePrefix,
            AttributeData? classMcpExpose,
            Compilation compilation,
            CancellationToken ct)
        {
            var handler = endpoint.Handler;
            var handlerMcpExpose = handler is null ? null : FindNearestAttribute(handler, x => x.OverriddenMethod, IsMcpExposeAttribute);
            var hasHandlerMcpIgnore = handler is not null && FindNearestAttribute(handler, x => x.OverriddenMethod, IsMcpIgnoreAttribute) is not null;

            // As on a controller, a scope the handler names, McpScope.None included, overrides its class's.
            var declaredScope = GetDeclaredScope(handlerMcpExpose) ?? GetDeclaredScope(classMcpExpose);
            var requiredScope = declaredScope is null or McpScopeModel.None
                ? GetRequiredScope(endpoint.HttpMethod)
                : declaredScope.Value;

            var parameters = ImmutableArray<ParameterInfoModel>.Empty;
            string? parameterProblem = null;
            var hasNonPublicParameterTypes = false;
            var responseType = new ResponseTypeInfo(null, hasBodylessSuccess: false);
            if (handler is not null)
            {
                (parameters, parameterProblem, hasNonPublicParameterTypes) = AnalyzeEndpointParameters(handler, endpoint, route, compilation, ct);
                responseType = ResponseTypeResolver.ResolveEndpoint(handler, compilation);
            }

            var successType = responseType.SuccessType;
            var givenName = endpoint.Name ??
                GetAttributeText(handler, MinimalApiNames.EndpointNameAttribute) ??
                (handler is not null && handler.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction ? handler.Name : null);
            var name = givenName is null ? DeriveEndpointName(endpoint.HttpMethod, route) : namePrefix + givenName;
            var (customToolName, customTitle, customDescription) = GetExposeTexts(handlerMcpExpose);
            if (customToolName is not null && namePrefix.Length > 0)
            {
                customToolName = namePrefix + ToolNameSeparator + customToolName;
            }

            return new ActionInfoModel
            {
                Name = name,
                ControllerName = endpoint.DeclaringType.Name,
                HttpMethod = endpoint.HttpMethod,
                RouteTemplate = route,
                RouteProblem = routeProblem ?? parameterProblem,
                Parameters = parameters,
                HasNonPublicParameterTypes = hasNonPublicParameterTypes,
                ReturnType = handler is null ? new ReturnTypeInfoModel() : AnalyzeReturnType(handler),
                HasMcpExposeAttribute = handlerMcpExpose is not null,
                HasMcpIgnoreAttribute = hasHandlerMcpIgnore,
                XmlDocSummary = handler is null || handler.MethodKind != MethodKind.Ordinary
                    ? null
                    : XmlDocSummaries.GetOrAdd(compilation, handler.OriginalDefinition, GetXmlDocSummary),
                CustomToolName = customToolName,
                Title = customTitle ?? ToolTitleBuilder.Build(customToolName, TrimSuffix(name, AsyncSuffix)),
                CustomDescription = customDescription,
                EndpointDescription = GetEndpointDescription(endpoint, handler),
                // A Minimal API never binds a parameter property by property from the route, query and form values, which
                // MVC does only outside [ApiController]; every parameter here has the source the endpoint reads it from.
                IsApiController = true,
                RequiredScope = requiredScope,
                Annotations = GetToolAnnotations(endpoint.HttpMethod, requiredScope, handlerMcpExpose, classMcpExpose),
                OutputSchemaType = successType is null ? null : ResponseTypeResolver.ToTypeOfOperand(successType),
                IsOutputNullable = successType is null ||
                    responseType.HasBodylessSuccess ||
                    (!IsNonNullableValueType(successType) && EmptyResponseAnalyzer.CanSucceedWithoutBody(handler!, compilation, ct)),
                SourceLocation = SourceLocationModel.From(endpoint.Location),
                IsMinimalApiEndpoint = true,
                OtherHttpMethods = endpoint.HttpMethods
                    .Select(x => x.ToUpperInvariant())
                    .Where(x => IsToolHttpMethod(x) && x != endpoint.HttpMethod)
                    .Distinct(StringComparer.Ordinal)
                    .ToImmutableArray()
            };
        }

        /// <summary>
        /// The tool name, title and description an <c>[McpExpose]</c> sets, each null when it sets none.
        /// </summary>
        private static (string? ToolName, string? Title, string? Description) GetExposeTexts(AttributeData? mcpExposeAttribute)
        {
            string? toolName = null;
            string? title = null;
            string? description = null;

            foreach (var namedArgument in mcpExposeAttribute?.NamedArguments ?? ImmutableArray<KeyValuePair<string, TypedConstant>>.Empty)
            {
                var value = namedArgument.Value.Value as string;
                if (namedArgument.Key == ToolNamePropertyName && value is not null)
                    toolName = value;
                if (namedArgument.Key == DescriptionPropertyName && value is not null)
                    description = value;
                if (namedArgument.Key == TitlePropertyName && !string.IsNullOrWhiteSpace(value))
                    title = value;
            }

            return (toolName, title, description);
        }

        /// <summary>
        /// The description an endpoint's metadata gives it, as ASP.NET Core's OpenAPI reads it: a convention chained to the
        /// <c>Map*</c> call wins over the handler's attribute of the same kind, as it is added later, and a description
        /// over a summary. <c>[Description]</c> on the handler comes last.
        /// </summary>
        private static string? GetEndpointDescription(EndpointDeclaration endpoint, IMethodSymbol? handler)
        {
            return endpoint.Description ??
                GetAttributeText(handler, MinimalApiNames.EndpointDescriptionAttribute) ??
                endpoint.Summary ??
                GetAttributeText(handler, MinimalApiNames.EndpointSummaryAttribute) ??
                GetAttributeText(handler, MinimalApiNames.DescriptionAttribute);
        }

        /// <summary>
        /// The text an attribute of the symbol takes as its first constructor argument, or null when it has no such
        /// attribute or the text is empty.
        /// </summary>
        private static string? GetAttributeText(ISymbol? symbol, string attributeFullName)
        {
            var text = symbol?.GetAttributes()
                .FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == attributeFullName)?
                .ConstructorArguments.FirstOrDefault().Value as string;

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>
        /// A name for an endpoint whose handler has none, from its HTTP method and route: the method, then each word of the
        /// route's text and each route value after <c>By</c>, so <c>GET /api/order-items/{id}</c> is
        /// <c>GetApiOrderItemsById</c>. The same route gives the same name on every build.
        /// </summary>
        internal static string DeriveEndpointName(string httpMethod, string route)
        {
            return Capitalize(httpMethod.ToLowerInvariant()) + string.Concat(GetRouteWords(route));
        }

        /// <summary>
        /// The words of a route as a derived name spells them: each word of its text capitalized, and each route value
        /// after <c>By</c>.
        /// </summary>
        private static List<string> GetRouteWords(string route)
        {
            var words = new List<string>();
            foreach (var part in RouteTemplate.Parse(route).Parts)
            {
                if (part.IsParameter)
                {
                    words.Add(RouteValueWord + Capitalize(part.Text));
                    continue;
                }

                words.AddRange(SplitRouteWords(part.Text).Select(Capitalize));
            }

            return words;
        }

        /// <summary>
        /// For the route prefixes one call maps an endpoint under, the words in which each differs from the others, so
        /// that its name differs too: <c>/v1</c> and <c>/v2</c> give <c>V1</c> and <c>V2</c>, and <c>/api/v1/admin</c>
        /// and <c>/api/v2/admin</c> the same, as the words all share at their start and end are left out. Empty for one
        /// prefix alone.
        /// </summary>
        private static List<string> GetDistinguishingWords(IReadOnlyList<string> prefixes)
        {
            if (prefixes.Count < 2)
            {
                return prefixes.Select(_ => "").ToList();
            }

            var words = prefixes.Select(GetRouteWords).ToList();
            var shortest = words.Min(x => x.Count);
            var sharedStart = 0;
            while (sharedStart < shortest && words.All(x => x[sharedStart] == words[0][sharedStart]))
            {
                sharedStart++;
            }

            var sharedEnd = 0;
            while (sharedEnd < shortest - sharedStart && words.All(x => x[x.Count - 1 - sharedEnd] == words[0][words[0].Count - 1 - sharedEnd]))
            {
                sharedEnd++;
            }

            return words.Select(x => string.Concat(x.Skip(sharedStart).Take(x.Count - sharedStart - sharedEnd))).ToList();
        }

        private static IEnumerable<string> SplitRouteWords(string text)
        {
            var word = new StringBuilder();
            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character))
                {
                    word.Append(character);
                    continue;
                }

                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }
            }

            if (word.Length > 0)
            {
                yield return word.ToString();
            }
        }

        private static string Capitalize(string word)
        {
            return word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);
        }

        /// <summary>
        /// Whether code of any assembly can name the type: it, the types it is nested in, its type arguments and its
        /// array's elements are public. A Minimal API handler takes internal types, such as a record declared without a
        /// modifier in <c>Program.cs</c>, which a public tool method cannot.
        /// </summary>
        private static bool IsEffectivelyPublic(ITypeSymbol type)
        {
            var arrayType = type as IArrayTypeSymbol;
            if (arrayType is not null)
            {
                return IsEffectivelyPublic(arrayType.ElementType);
            }

            var namedType = type as INamedTypeSymbol;
            if (namedType is null)
            {
                return true;
            }

            for (var current = namedType; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility != Accessibility.Public)
                {
                    return false;
                }
            }

            return namedType.TypeArguments.All(IsEffectivelyPublic);
        }

        /// <summary>
        /// Whether the type is a reference type of code without nullable annotations, which ASP.NET Core's binding takes
        /// for optional, as its nullability is not known to be not-null.
        /// </summary>
        private static bool IsObliviousReferenceType(ITypeSymbol type)
        {
            return !type.IsValueType && type.TypeKind != TypeKind.TypeParameter && type.NullableAnnotation == NullableAnnotation.None;
        }

        private static bool IsNonNullableValueType(ITypeSymbol type)
        {
            return type.IsValueType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;
        }

        /// <summary>
        /// The tool inputs of an endpoint, each with the source a Minimal API binds it from, in the handler's order; the
        /// members of an <c>[AsParameters]</c> parameter take its place. A problem, as a clause of MCP010's message, when
        /// the tool cannot send what the endpoint reads.
        /// </summary>
        private static (ImmutableArray<ParameterInfoModel> Parameters, string? Problem, bool HasNonPublicTypes) AnalyzeEndpointParameters(
            IMethodSymbol handler,
            EndpointDeclaration endpoint,
            string route,
            Compilation compilation,
            CancellationToken ct)
        {
            var paramDocs = XmlDocParams.GetOrAdd(compilation, handler.OriginalDefinition, GetXmlDocParams);
            var inputs = new List<EndpointInput>();
            foreach (var parameter in handler.Parameters)
            {
                ct.ThrowIfCancellationRequested();

                if (!HasAttribute(parameter.GetAttributes(), MinimalApiNames.AsParametersAttribute))
                {
                    inputs.Add(EndpointInput.From(parameter, paramDocs.TryGetValue(parameter.Name, out var doc) ? doc : null));
                    continue;
                }

                var (members, membersProblem) = GetAsParametersMembers(parameter);
                if (membersProblem is not null)
                {
                    return (ImmutableArray<ParameterInfoModel>.Empty, membersProblem, false);
                }

                inputs.AddRange(members);
            }

            var isBodyInferred = !endpoint.HttpMethods.Any(BodilessHttpMethods.Contains);
            var bindings = new List<(EndpointInput Input, EndpointBinding Binding, string? QueryName)>();
            foreach (var input in inputs)
            {
                var (binding, queryName, problem) = GetEndpointBinding(input, isBodyInferred);
                if (problem is not null)
                {
                    return (ImmutableArray<ParameterInfoModel>.Empty, problem, false);
                }

                if (binding != EndpointBinding.None)
                {
                    bindings.Add((input, binding, queryName));
                }
            }

            var bodyProblem = ResolveBody(bindings, endpoint.HttpMethod);
            if (bodyProblem is not null)
            {
                return (ImmutableArray<ParameterInfoModel>.Empty, bodyProblem, false);
            }

            var sharedName = bindings
                .GroupBy(x => x.Input.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1)?.Key;
            if (sharedName is not null)
            {
                return (ImmutableArray<ParameterInfoModel>.Empty, $"two of its inputs are named '{sharedName}', which the endpoint reads from the same value and a tool cannot tell apart", false);
            }

            var hidden = bindings.FirstOrDefault(x => !compilation.IsSymbolAccessibleWithin(x.Input.Type, compilation.Assembly)).Input;
            if (hidden is not null)
            {
                return (ImmutableArray<ParameterInfoModel>.Empty, $"its input '{hidden.Name}' is of type {hidden.Type.ToDisplayString()}, which the generated tool cannot refer to", false);
            }

            var parameters = bindings
                .Select(x => CreateEndpointParameter(x.Input, ToParameterSource(x.Binding), x.QueryName, compilation))
                .ToImmutableArray();

            return (parameters, null, bindings.Any(x => !IsEffectivelyPublic(x.Input.Type)));
        }

        /// <summary>
        /// Decides which input is the body, as a Minimal API does. One with <c>[FromBody]</c> is; then every other complex
        /// input without an attribute is a service, as an endpoint reads one body at most. Without one, a single such
        /// input is the body; of several, all but one are services, and only the running app knows which.
        /// </summary>
        private static string? ResolveBody(List<(EndpointInput Input, EndpointBinding Binding, string? QueryName)> bindings, string httpMethod)
        {
            var declaredBodies = bindings.Where(x => x.Binding == EndpointBinding.Body).ToList();
            var candidates = bindings.Where(x => x.Binding == EndpointBinding.BodyOrService).ToList();

            if (declaredBodies.Count > 1)
            {
                return $"it reads {QuoteInputs(declaredBodies)} from the body, of which a request has one";
            }

            if (declaredBodies.Count == 0 && candidates.Count > 1)
            {
                return $"the generator cannot tell which of {QuoteInputs(candidates)} it reads from the body and which are services: mark the services [FromServices], or the body [FromBody]";
            }

            if (declaredBodies.Count == 1)
            {
                bindings.RemoveAll(x => x.Binding == EndpointBinding.BodyOrService);
            }

            for (var i = 0; i < bindings.Count; i++)
            {
                if (bindings[i].Binding == EndpointBinding.BodyOrService)
                {
                    bindings[i] = (bindings[i].Input, EndpointBinding.Body, null);
                }
            }

            var body = bindings.FirstOrDefault(x => x.Binding == EndpointBinding.Body).Input;
            return body is not null && !BodyHttpMethods.Contains(httpMethod)
                ? $"it reads '{body.Name}' from the body, which a tool sends only with POST, PUT and PATCH"
                : null;
        }

        private static string QuoteInputs(IEnumerable<(EndpointInput Input, EndpointBinding Binding, string? QueryName)> bindings)
        {
            var names = bindings.Select(x => $"'{x.Input.Name}'").ToList();

            return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[names.Count - 1]}";
        }

        /// <summary>
        /// Where a Minimal API binds an input from (<c>RequestDelegateFactory.CreateArgument</c>), in its order: a binding
        /// attribute, a type it fills itself, a <c>BindAsync</c>, a type it parses from one string, an array of those on an
        /// endpoint without a body, and otherwise a service or the body.
        /// </summary>
        private static (EndpointBinding Binding, string? QueryName, string? Problem) GetEndpointBinding(EndpointInput input, bool isBodyInferred)
        {
            var attributes = input.Attributes;
            var type = input.Type;

            if (attributes.Any(x => Implements(x, MinimalApiNames.FromRouteMetadata)))
                return (EndpointBinding.Route, null, null);

            if (attributes.Any(x => Implements(x, MinimalApiNames.FromQueryMetadata)))
                return (EndpointBinding.Query, GetMetadataName(attributes, MinimalApiNames.FromQueryMetadata), null);

            // The tool sends no headers. An optional one the endpoint does without; a required one it refuses with 400.
            if (attributes.Any(x => Implements(x, MinimalApiNames.FromHeaderMetadata)))
                return input.IsOptional
                    ? (EndpointBinding.None, null, null)
                    : (EndpointBinding.None, null, $"it reads '{input.Name}' from a header, which a tool does not send");

            if (attributes.Any(x => Implements(x, MinimalApiNames.FromBodyMetadata)))
                return (EndpointBinding.Body, null, null);

            if (attributes.Any(x => Implements(x, MinimalApiNames.FromFormMetadata)))
                return (EndpointBinding.None, null, $"it reads '{input.Name}' from a form, which a tool does not send");

            if (attributes.Any(x => Implements(x, MinimalApiNames.FromServiceMetadata)) || HasAttribute(attributes, MinimalApiNames.FromKeyedServicesAttribute))
                return (EndpointBinding.None, null, null);

            if (HasAttribute(attributes, MinimalApiNames.AsParametersAttribute))
                return (EndpointBinding.None, null, $"its [AsParameters] input '{input.Name}' holds another [AsParameters] one, which a Minimal API does not bind");

            var typeName = GetTypeName(type);
            if (RequestTypes.Contains(typeName))
                return (EndpointBinding.None, null, null);

            if (FormTypes.Contains(typeName))
                return (EndpointBinding.None, null, $"it reads '{input.Name}' from a form, which a tool does not send");

            if (RawBodyTypes.Contains(typeName))
                return (EndpointBinding.None, null, $"it reads the request body of '{input.Name}' as a stream, whose format the generator cannot know");

            if (HasBindAsync(type))
                return (EndpointBinding.None, null, $"it binds '{input.Name}' through {typeName}.BindAsync, which reads the request in a way the generator cannot know");

            if (SimpleTypeClassifier.IsParsable(type))
                return (EndpointBinding.RouteOrQuery, null, null);

            if (!isBodyInferred && IsParsableCollection(type))
                return (EndpointBinding.Query, null, null);

            // Without a body to read, such an input has to be a service, or the app would refuse to start.
            if (!isBodyInferred || IsLikelyService(type))
                return (EndpointBinding.None, null, null);

            return (EndpointBinding.BodyOrService, null, null);
        }

        private static ParameterSourceModel ToParameterSource(EndpointBinding binding)
        {
            switch (binding)
            {
                case EndpointBinding.Route:
                    return ParameterSourceModel.Route;
                case EndpointBinding.Query:
                    return ParameterSourceModel.Query;
                case EndpointBinding.Body:
                    return ParameterSourceModel.Body;
                default:
                    // A value parsed from one string goes where MVC's inference sends a simple type: the route value of
                    // its name, else the query string, which is where a Minimal API reads it from too.
                    return ParameterSourceModel.Auto;
            }
        }

        private static ParameterInfoModel CreateEndpointParameter(EndpointInput input, ParameterSourceModel source, string? queryName, Compilation compilation)
        {
            var type = input.Type;
            var isSimpleType = SimpleTypeClassifier.IsParsable(type);

            // System.Text.Json cannot fill a StringValues, so the tool takes its strings as an array, which it sends as the
            // repeated key the endpoint reads them from; as an optional argument when it has a default.
            if (GetTypeName(type) == MinimalApiNames.StringValues)
            {
                var isOptional = input.IsOptional;

                return new ParameterInfoModel
                {
                    Name = input.Name,
                    Type = isOptional ? MinimalApiNames.StringValuesToolType + NullableAnnotationSuffix : MinimalApiNames.StringValuesToolType,
                    IsNullable = isOptional,
                    Source = source,
                    IsCollection = true,
                    Description = input.Description,
                    QueryName = queryName
                };
            }

            return new ParameterInfoModel
            {
                Name = input.Name,
                // A reference type of nullable-oblivious code is optional, and declared nullable for the tool's
                // #nullable enable context to give it the default null.
                Type = IsObliviousReferenceType(type)
                    ? type.WithNullableAnnotation(NullableAnnotation.Annotated).ToDisplayString()
                    : type.ToDisplayString(),
                IsNullable = input.IsNullable,
                IsValueType = type.IsValueType,
                HasDefaultValue = input.HasDefaultValue,
                DefaultValueExpression = input.HasDefaultValue ? DefaultValueExpressionBuilder.Build(input.DefaultValue, type) : null,
                Source = source,
                IsSimpleType = isSimpleType,
                IsCollection = IsCollection(type),
                // A body's properties name the route values the URL needs a segment for, as of a controller's [FromBody].
                Properties = source == ParameterSourceModel.Body && !isSimpleType
                    ? ExtractProperties(type, compilation)
                    : ImmutableArray<PropertyInfoModel>.Empty,
                Description = input.Description,
                QueryName = queryName
            };
        }

        /// <summary>
        /// The members a Minimal API binds an <c>[AsParameters]</c> parameter's type through, each as a handler parameter
        /// of its own (<c>RequestDelegateFactory.BindParameterFromProperties</c>): the parameters of the constructor it
        /// creates the type with, each matched to the property of its name, or else its public settable properties.
        /// </summary>
        private static (List<EndpointInput> Members, string? Problem) GetAsParametersMembers(IParameterSymbol parameter)
        {
            var type = parameter.Type as INamedTypeSymbol;
            if (type is null || type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                return (new List<EndpointInput>(), $"the generator cannot read the members of its [AsParameters] input '{parameter.Name}'");
            }

            var properties = GetVisibleProperties(type);
            var constructor = GetAsParametersConstructor(type);
            if (constructor is null || constructor.Parameters.Length == 0)
            {
                var settableProperties = properties
                    .Where(x => x.SetMethod is not null && x.SetMethod.DeclaredAccessibility == Accessibility.Public)
                    .Select(x => EndpointInput.From(x, constructorParameter: null))
                    .ToList();

                return (settableProperties, null);
            }

            var members = new List<EndpointInput>();
            foreach (var constructorParameter in constructor.Parameters)
            {
                var property = properties.FirstOrDefault(x =>
                    string.Equals(x.Name, constructorParameter.Name, StringComparison.OrdinalIgnoreCase) &&
                    SymbolEqualityComparer.Default.Equals(x.Type, constructorParameter.Type));
                if (property is null)
                {
                    return (new List<EndpointInput>(), $"the constructor of its [AsParameters] input '{parameter.Name}' takes '{constructorParameter.Name}', which no property of the same name and type matches");
                }

                members.Add(EndpointInput.From(property, constructorParameter));
            }

            return (members, null);
        }

        /// <summary>
        /// The constructor a Minimal API creates an <c>[AsParameters]</c> type with (<c>ParameterBindingMethodCache.FindConstructor</c>):
        /// its only public one, else its public parameterless one. A struct's implicit parameterless constructor, which
        /// reflection does not list, counts for neither.
        /// </summary>
        private static IMethodSymbol? GetAsParametersConstructor(INamedTypeSymbol type)
        {
            var constructors = type.InstanceConstructors
                .Where(x => x.DeclaredAccessibility == Accessibility.Public && !(type.IsValueType && x.IsImplicitlyDeclared))
                .ToList();

            return constructors.Count == 1
                ? constructors[0]
                : constructors.FirstOrDefault(x => x.Parameters.Length == 0);
        }

        /// <summary>
        /// Whether a Minimal API binds the type through a <c>BindAsync</c> of its own: one implementing
        /// <c>IBindableFromHttpContext&lt;T&gt;</c>, or with a public static <c>BindAsync</c> taking the <c>HttpContext</c>.
        /// </summary>
        private static bool HasBindAsync(ITypeSymbol type)
        {
            type = UnwrapNullable(type);
            if (type.AllInterfaces.Any(x => x.OriginalDefinition.ToDisplayString() == MinimalApiNames.BindableFromHttpContext))
            {
                return true;
            }

            return GetBaseTypes(type).Prepend(type)
                .SelectMany(x => x.GetMembers(MinimalApiNames.BindAsyncMethodName).OfType<IMethodSymbol>())
                .Any(x => x.IsStatic &&
                    x.DeclaredAccessibility == Accessibility.Public &&
                    x.Parameters.Length > 0 &&
                    x.Parameters[0].Type.ToDisplayString() == MinimalApiNames.HttpContext);
        }

        /// <summary>
        /// An array of a type parsed from one string, or <c>StringValues</c>, which an endpoint without a body reads from
        /// the query string, its key repeated.
        /// </summary>
        private static bool IsParsableCollection(ITypeSymbol type)
        {
            var arrayType = type as IArrayTypeSymbol;
            if (arrayType is not null)
            {
                return SimpleTypeClassifier.IsParsable(arrayType.ElementType);
            }

            return GetTypeName(type) == MinimalApiNames.StringValues;
        }

        /// <summary>
        /// Whether a complex type without an attribute is surely a service rather than a body, as the generator cannot see
        /// which types the app registers: an interface, a delegate or an abstract class, which no body is read into but a
        /// collection interface <c>System.Text.Json</c> fills and <c>JsonNode</c>; an Entity Framework context; and the few
        /// classes of the framework an app registers as services, such as <c>HttpClient</c>. Any other class, the
        /// framework's included, may be the body.
        /// </summary>
        private static bool IsLikelyService(ITypeSymbol type)
        {
            type = UnwrapNullable(type);

            if (type.TypeKind == TypeKind.Interface)
                return !MinimalApiNames.BodyCollectionInterfaces.Contains(type.OriginalDefinition.ToDisplayString());

            if (type.TypeKind == TypeKind.Delegate)
                return true;

            if (ResponseTypeResolver.IsOrImplements(type, MinimalApiNames.JsonNode))
                return false;

            return type.IsAbstract ||
                ResponseTypeResolver.IsOrImplements(type, MinimalApiNames.DbContext) ||
                MinimalApiNames.FrameworkServiceClasses.Any(x => ResponseTypeResolver.IsOrImplements(type, x));
        }

        private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
        {
            return type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                ? ((INamedTypeSymbol)type).TypeArguments[0]
                : type;
        }

        /// <summary>
        /// The type's name as the generator's constants spell it: without <c>Nullable&lt;T&gt;</c> or a nullable
        /// annotation around it.
        /// </summary>
        private static string GetTypeName(ITypeSymbol type)
        {
            return UnwrapNullable(type).WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();
        }

        private static bool HasAttribute(IEnumerable<AttributeData> attributes, string attributeFullName)
        {
            return attributes.Any(x => x.AttributeClass is not null && ResponseTypeResolver.IsOrImplements(x.AttributeClass, attributeFullName));
        }

        /// <summary>
        /// The <c>Name</c> the first binding attribute of the kind sets, or null when it sets none.
        /// </summary>
        private static string? GetMetadataName(IEnumerable<AttributeData> attributes, string metadataInterface)
        {
            return attributes
                .Where(x => Implements(x, metadataInterface))
                .SelectMany(x => x.NamedArguments)
                .Where(x => x.Key == MinimalApiNames.MetadataNamePropertyName)
                .Select(x => x.Value.Value as string)
                .FirstOrDefault(x => !string.IsNullOrEmpty(x));
        }

        /// <summary>
        /// A value an endpoint's handler takes: one of its parameters, or a member of an <c>[AsParameters]</c> one.
        /// </summary>
        private sealed class EndpointInput
        {
            private EndpointInput(string name, ITypeSymbol type, ImmutableArray<AttributeData> attributes, bool hasDefaultValue, object? defaultValue, string? description)
            {
                Name = name;
                Type = type;
                Attributes = attributes;
                HasDefaultValue = hasDefaultValue;
                DefaultValue = defaultValue;
                Description = description;
            }

            public string Name { get; }
            public ITypeSymbol Type { get; }
            public ImmutableArray<AttributeData> Attributes { get; }
            public bool HasDefaultValue { get; }
            public object? DefaultValue { get; }
            public string? Description { get; }

            public bool IsNullable => Type.NullableAnnotation == NullableAnnotation.Annotated ||
                Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ||
                IsObliviousReferenceType(Type);

            public bool IsOptional => HasDefaultValue || IsNullable;

            /// <summary>
            /// A handler parameter, described by its <c>[Description]</c>, else by the handler's XML <c>&lt;param&gt;</c>.
            /// </summary>
            public static EndpointInput From(IParameterSymbol parameter, string? paramDoc)
            {
                var attributes = parameter.GetAttributes();

                return new EndpointInput(
                    parameter.Name,
                    parameter.Type,
                    attributes,
                    parameter.HasExplicitDefaultValue,
                    parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue : null,
                    GetAttributeText(parameter, MinimalApiNames.DescriptionAttribute) ?? paramDoc);
            }

            /// <summary>
            /// A member of an <c>[AsParameters]</c> type, bound under its property's name with the attributes of the
            /// constructor parameter that sets it and of the property, and described by a <c>[Description]</c> on either,
            /// else by the record's XML <c>&lt;param&gt;</c> or the property's <c>&lt;summary&gt;</c>.
            /// </summary>
            public static EndpointInput From(IPropertySymbol property, IParameterSymbol? constructorParameter)
            {
                var attributes = (constructorParameter?.GetAttributes() ?? ImmutableArray<AttributeData>.Empty)
                    .AddRange(property.GetAttributes());
                var constructor = constructorParameter?.ContainingSymbol as IMethodSymbol;
                var paramDoc = constructor is null || constructorParameter is null
                    ? null
                    : GetXmlDocParams(constructor).TryGetValue(constructorParameter.Name, out var doc) ? doc : null;
                var description = GetAttributeText(constructorParameter, MinimalApiNames.DescriptionAttribute) ??
                    GetAttributeText(property, MinimalApiNames.DescriptionAttribute) ??
                    paramDoc ??
                    GetDeclaredXmlDocSummary(property);
                var hasDefaultValue = constructorParameter?.HasExplicitDefaultValue == true;

                return new EndpointInput(
                    property.Name,
                    property.Type,
                    attributes,
                    hasDefaultValue,
                    hasDefaultValue ? constructorParameter!.ExplicitDefaultValue : null,
                    description);
            }
        }
    }
}
