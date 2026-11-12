using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// What a <c>Map*</c> call says about the Minimal API endpoint it maps, before the route groups it is mapped in.
    /// </summary>
    internal sealed class EndpointDeclaration
    {
        /// <summary>
        /// The lambda, local function or method that handles the endpoint, or null when the generator cannot see it.
        /// </summary>
        public IMethodSymbol? Handler { get; init; }

        /// <summary>
        /// The class that declares the handler, which stands for the controller in tool names and config entries.
        /// </summary>
        public INamedTypeSymbol DeclaringType { get; init; } = null!;

        /// <summary>
        /// The HTTP method the tool calls the endpoint with: the first one a tool sends that it is mapped for.
        /// </summary>
        public string HttpMethod { get; init; } = "";

        /// <summary>
        /// Every HTTP method the endpoint is mapped for, as the call writes it, which decides whether a Minimal API infers a
        /// body: it compares them with <c>GET</c>, <c>DELETE</c> and the others case and all.
        /// </summary>
        public IReadOnlyList<string> HttpMethods { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The route pattern of the call itself, without its groups' prefixes, or null when it is not a constant.
        /// </summary>
        public string? Pattern { get; init; }

        /// <summary>
        /// Why the call itself cannot become a tool, as a clause of MCP010's message, or null.
        /// </summary>
        public string? Problem { get; init; }

        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Summary { get; init; }

        /// <summary>
        /// The name of the <c>Map*</c> method in the call, where diagnostics point.
        /// </summary>
        public Location Location { get; init; } = Location.None;
    }

    /// <summary>
    /// Finds Minimal API endpoints: the calls of ASP.NET Core's <c>MapGet</c>, <c>MapPost</c>, <c>MapPut</c>,
    /// <c>MapPatch</c>, <c>MapDelete</c> and <c>MapMethods</c>, with the conventions chained to them.
    /// </summary>
    internal static class MinimalApiScanner
    {
        private static readonly Dictionary<string, string?> HttpMethodByMapMethod = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MinimalApiNames.MapGet] = HttpMethodNames.Get,
            [MinimalApiNames.MapPost] = HttpMethodNames.Post,
            [MinimalApiNames.MapPut] = HttpMethodNames.Put,
            [MinimalApiNames.MapPatch] = HttpMethodNames.Patch,
            [MinimalApiNames.MapDelete] = HttpMethodNames.Delete,
            [MinimalApiNames.MapMethods] = null
        };

        /// <summary>
        /// Whether the node calls a method by the name of one that maps an endpoint, which the transform then checks.
        /// </summary>
        public static bool IsMapCall(SyntaxNode node)
        {
            var invocation = node as InvocationExpressionSyntax;
            var name = invocation is null ? null : RouteGroupResolver.GetInvokedName(invocation.Expression);

            return name is not null && HttpMethodByMapMethod.ContainsKey(name);
        }

        /// <summary>
        /// The endpoints a <c>Map*</c> call maps, one for each route its groups give it, as the actions of the class that
        /// declares its handler; null when the call maps none a tool can call, such as one for <c>HEAD</c> only.
        /// </summary>
        public static ControllerInfoModel? TryGetEndpoints(GeneratorSyntaxContext context, CancellationToken ct)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var semanticModel = context.SemanticModel;
            var operation = semanticModel.GetOperation(invocation, ct) as IInvocationOperation;
            if (operation is null || !IsMapMethod(operation.TargetMethod))
            {
                return null;
            }

            var mapMethod = operation.TargetMethod;
            var (httpMethods, httpMethodsProblem) = mapMethod.Name == MinimalApiNames.MapMethods
                ? ReadHttpMethods(RouteGroupResolver.GetArgument(operation, MinimalApiNames.HttpMethodsParameter))
                : (new List<string> { HttpMethodByMapMethod[mapMethod.Name]! }, null);
            // Routing matches a request's method to these ignoring case, so the tool sends the method upper-cased.
            var toolHttpMethods = httpMethods
                .Select(x => x.ToUpperInvariant())
                .Where(ActionMethodAnalyzer.IsToolHttpMethod)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (httpMethodsProblem is null && toolHttpMethods.Count == 0)
            {
                return null;
            }

            var handler = GetHandler(RouteGroupResolver.GetArgument(operation, mapMethod.Parameters.Length - 1));
            var declaringType = handler?.ContainingType ?? semanticModel.GetEnclosingSymbol(invocation.SpanStart, ct)?.ContainingType;
            if (declaringType is null)
            {
                return null;
            }

            var patternValue = RouteGroupResolver.GetArgument(operation, MinimalApiNames.PatternParameter)?.ConstantValue;
            var pattern = patternValue is null || !patternValue.Value.HasValue ? null : patternValue.Value.Value as string;
            var (name, description, summary) = ReadConventions(invocation, semanticModel, ct);

            var endpoint = new EndpointDeclaration
            {
                Handler = handler,
                DeclaringType = declaringType,
                HttpMethod = toolHttpMethods.FirstOrDefault() ?? HttpMethodNames.Get,
                HttpMethods = httpMethods,
                Pattern = pattern,
                Problem = httpMethodsProblem ??
                    (pattern is null ? "the generator cannot read its route pattern, as it is not a constant string" : null) ??
                    (handler is null ? "the generator cannot see its handler, which is neither a lambda nor a method" : null),
                Name = name,
                Description = description,
                Summary = summary,
                Location = GetNameLocation(invocation)
            };

            var builder = RouteGroupResolver.GetArgument(operation, 0)?.Syntax as ExpressionSyntax;
            var groups = builder is null
                ? RouteGroups.Fail("the generator cannot tell the route builder it is mapped on")
                : RouteGroupResolver.Resolve(builder, semanticModel, ct);

            return ActionMethodAnalyzer.GetEndpointClass(endpoint, groups, semanticModel.Compilation, ct);
        }

        private static bool IsMapMethod(IMethodSymbol method)
        {
            return HttpMethodByMapMethod.ContainsKey(method.Name) &&
                method.ContainingType.ToDisplayString() == MinimalApiNames.EndpointRouteBuilderExtensions;
        }

        /// <summary>
        /// The lambda, local function or method a handler argument refers to, through the conversions to <c>Delegate</c>.
        /// Null for any other value, such as a variable holding a delegate.
        /// </summary>
        private static IMethodSymbol? GetHandler(IOperation? value)
        {
            while (value is not null)
            {
                switch (value.Kind)
                {
                    case OperationKind.Conversion:
                        value = ((IConversionOperation)value).Operand;
                        break;
                    case OperationKind.DelegateCreation:
                        value = ((IDelegateCreationOperation)value).Target;
                        break;
                    case OperationKind.AnonymousFunction:
                        return ((IAnonymousFunctionOperation)value).Symbol;
                    case OperationKind.MethodReference:
                        return ((IMethodReferenceOperation)value).Method;
                    default:
                        return null;
                }
            }

            return null;
        }

        /// <summary>
        /// The HTTP methods <c>MapMethods</c> is given as an array or a collection written in the call, of constants or of
        /// <c>HttpMethods</c>' fields, as written; a problem for anything else.
        /// </summary>
        private static (List<string> HttpMethods, string? Problem) ReadHttpMethods(IOperation? value)
        {
            const string problem = "the generator cannot read the HTTP methods passed to MapMethods, as they are not a list of constants written in the call";
            var elements = GetCollectionElements(value?.Syntax as ExpressionSyntax);
            if (value is null || elements is null)
            {
                return (new List<string>(), problem);
            }

            var semanticModel = value.SemanticModel;
            var httpMethods = new List<string>();
            foreach (var element in elements)
            {
                var httpMethod = semanticModel is null ? null : ReadHttpMethod(element, semanticModel);
                if (httpMethod is null)
                {
                    return (new List<string>(), problem);
                }

                httpMethods.Add(httpMethod);
            }

            return (httpMethods, null);
        }

        private static string? ReadHttpMethod(ExpressionSyntax element, SemanticModel semanticModel)
        {
            var constant = semanticModel.GetConstantValue(element);
            if (constant.HasValue)
            {
                return constant.Value as string;
            }

            // HttpMethods.Get and the others are static readonly fields, each holding its name in capitals.
            var field = semanticModel.GetSymbolInfo(element).Symbol as IFieldSymbol;
            return field is not null && field.IsStatic && field.ContainingType.ToDisplayString() == MinimalApiNames.HttpMethods
                ? field.Name.ToUpperInvariant()
                : null;
        }

        private static IReadOnlyList<ExpressionSyntax>? GetCollectionElements(ExpressionSyntax? expression)
        {
            var arrayCreation = expression as ArrayCreationExpressionSyntax;
            if (arrayCreation is not null)
            {
                return arrayCreation.Initializer?.Expressions;
            }

            var implicitArrayCreation = expression as ImplicitArrayCreationExpressionSyntax;
            if (implicitArrayCreation is not null)
            {
                return implicitArrayCreation.Initializer.Expressions;
            }

            var objectCreation = expression as BaseObjectCreationExpressionSyntax;
            if (objectCreation is not null)
            {
                return objectCreation.Initializer?.Expressions;
            }

            var collection = expression as CollectionExpressionSyntax;
            if (collection is not null && collection.Elements.All(x => x is ExpressionElementSyntax))
            {
                return collection.Elements.Cast<ExpressionElementSyntax>().Select(x => x.Expression).ToList();
            }

            return null;
        }

        /// <summary>
        /// The name, description and summary the conventions chained to the call give the endpoint, such as
        /// <c>app.MapGet(...).WithName("GetOrder")</c>. Of two, the later wins, as ASP.NET Core reads the last metadata.
        /// </summary>
        private static (string? Name, string? Description, string? Summary) ReadConventions(
            InvocationExpressionSyntax mapCall,
            SemanticModel semanticModel,
            CancellationToken ct)
        {
            string? name = null;
            string? description = null;
            string? summary = null;

            SyntaxNode current = mapCall;
            while (true)
            {
                var memberAccess = current.Parent as MemberAccessExpressionSyntax;
                var call = memberAccess?.Parent as InvocationExpressionSyntax;
                if (memberAccess is null || call is null || memberAccess.Expression != current)
                {
                    break;
                }

                var method = semanticModel.GetSymbolInfo(call, ct).Symbol as IMethodSymbol;
                var definition = method?.ReducedFrom ?? method;
                var argument = call.ArgumentList.Arguments.FirstOrDefault();
                var value = definition is null || argument is null ? null : semanticModel.GetConstantValue(argument.Expression, ct).Value as string;
                var containingType = definition?.ContainingType.ToDisplayString();

                if (value is not null && containingType == MinimalApiNames.RoutingConventions && definition!.Name == MinimalApiNames.WithName)
                {
                    name = value;
                }
                else if (value is not null && containingType == MinimalApiNames.OpenApiConventions && definition!.Name == MinimalApiNames.WithDescription)
                {
                    description = value;
                }
                else if (value is not null && containingType == MinimalApiNames.OpenApiConventions && definition!.Name == MinimalApiNames.WithSummary)
                {
                    summary = value;
                }

                current = call;
            }

            return (name, description, summary);
        }

        private static Location GetNameLocation(InvocationExpressionSyntax invocation)
        {
            var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
            return memberAccess is null ? invocation.GetLocation() : memberAccess.Name.GetLocation();
        }
    }
}
