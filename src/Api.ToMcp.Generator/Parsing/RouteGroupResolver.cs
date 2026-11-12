using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// The route prefixes of the route builder a Minimal API endpoint is mapped on, or why the generator cannot tell them.
    /// </summary>
    internal sealed class RouteGroups
    {
        public static readonly RouteGroups Root = new RouteGroups(new[] { "" }, null);

        private RouteGroups(IReadOnlyList<string> prefixes, string? problem)
        {
            Prefixes = prefixes;
            Problem = problem;
        }

        /// <summary>
        /// Each prefix the builder has, one for every chain of calls that maps the endpoint, the root's empty one included.
        /// </summary>
        public IReadOnlyList<string> Prefixes { get; }

        /// <summary>
        /// Why the generator cannot tell the prefixes, as a clause of MCP010's message, or null when it can.
        /// </summary>
        public string? Problem { get; }

        public static RouteGroups Fail(string problem)
        {
            return new RouteGroups(Array.Empty<string>(), problem);
        }

        public RouteGroups Append(string prefix)
        {
            return Problem is null
                ? new RouteGroups(Prefixes.Select(x => RouteGroupResolver.Combine(x, prefix)).ToList(), null)
                : this;
        }

        /// <summary>
        /// The prefixes of every call that maps the endpoint, or the first problem among them: one call the generator
        /// cannot follow leaves the endpoint at a route it cannot tell.
        /// </summary>
        public static RouteGroups Merge(IEnumerable<RouteGroups> groups)
        {
            var prefixes = new List<string>();
            foreach (var group in groups)
            {
                if (group.Problem is not null)
                {
                    return group;
                }

                prefixes.AddRange(group.Prefixes);
            }

            return new RouteGroups(prefixes.Distinct(StringComparer.Ordinal).ToList(), null);
        }
    }

    /// <summary>
    /// Follows the route builder a Minimal API endpoint is mapped on back to the app: through <c>MapGroup</c> calls, the
    /// ASP.NET Core conventions that return the builder they are called on, local variables assigned once, and the
    /// parameters of the app's own methods, to every call of them in the compilation. A <c>WebApplication</c> is the root.
    /// </summary>
    internal static class RouteGroupResolver
    {
        private const char PathSeparator = '/';

        /// <summary>
        /// How many calls deep the generator follows a builder through the app's methods, and how many routes one endpoint
        /// may get from the methods that map it: the limits keep a cycle the generator does not see, or methods that call
        /// one another many times, from running the build long.
        /// </summary>
        private const int MaxDepth = 16;

        private const int MaxPrefixes = 32;

        /// <summary>
        /// Every invocation of the compilation, by the name of the method it calls, which the search for the calls of a
        /// method looks through. Built once per compilation, the first time an endpoint is mapped on a parameter.
        /// </summary>
        private static readonly CompilationCache<Compilation, ILookup<string, InvocationExpressionSyntax>> InvocationsByName =
            new CompilationCache<Compilation, ILookup<string, InvocationExpressionSyntax>>(EqualityComparer<Compilation>.Default);

        public static RouteGroups Resolve(ExpressionSyntax builder, SemanticModel semanticModel, CancellationToken ct)
        {
            return Resolve(builder, semanticModel, new HashSet<SyntaxReference>(SyntaxReferenceComparer.Instance), depth: 0, ct);
        }

        /// <summary>
        /// The route of a pattern under a prefix, as <c>RoutePatternFactory.Combine</c> joins them: one slash between the
        /// two, and one in front.
        /// </summary>
        public static string Combine(string prefix, string pattern)
        {
            var parts = new[] { prefix, pattern }
                .Select(x => x.Trim(PathSeparator))
                .Where(x => x.Length > 0);

            return PathSeparator + string.Join(PathSeparator.ToString(), parts);
        }

        /// <summary>
        /// The name a call invokes a method by, whatever form the call takes: <c>M()</c>, <c>x.M()</c>, <c>x?.M()</c> or
        /// <c>M&lt;T&gt;()</c>; null for a call of a delegate an expression gives.
        /// </summary>
        public static string? GetInvokedName(ExpressionSyntax expression)
        {
            var simpleName = expression as SimpleNameSyntax;
            if (simpleName is not null)
            {
                return simpleName.Identifier.ValueText;
            }

            var memberAccess = expression as MemberAccessExpressionSyntax;
            if (memberAccess is not null)
            {
                return memberAccess.Name.Identifier.ValueText;
            }

            var memberBinding = expression as MemberBindingExpressionSyntax;
            return memberBinding?.Name.Identifier.ValueText;
        }

        private static RouteGroups Resolve(
            ExpressionSyntax expression,
            SemanticModel semanticModel,
            HashSet<SyntaxReference> methodsBeingFollowed,
            int depth,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            expression = Unwrap(expression);

            if (depth > MaxDepth)
            {
                return RouteGroups.Fail($"the generator follows the route builder '{expression}' no deeper than {MaxDepth} calls");
            }

            var type = semanticModel.GetTypeInfo(expression, ct).Type;
            if (type is not null && IsWebApplication(type))
            {
                return RouteGroups.Root;
            }

            var invocation = expression as InvocationExpressionSyntax;
            if (invocation is not null)
            {
                return ResolveInvocation(invocation, semanticModel, methodsBeingFollowed, depth, ct);
            }

            var identifier = expression as IdentifierNameSyntax;
            if (identifier is not null)
            {
                return ResolveIdentifier(identifier, semanticModel, methodsBeingFollowed, depth, ct);
            }

            return RouteGroups.Fail($"the generator cannot tell which route group '{expression}' is");
        }

        private static RouteGroups ResolveInvocation(
            InvocationExpressionSyntax invocation,
            SemanticModel semanticModel,
            HashSet<SyntaxReference> methodsBeingFollowed,
            int depth,
            CancellationToken ct)
        {
            var operation = semanticModel.GetOperation(invocation, ct) as IInvocationOperation;
            var method = operation?.TargetMethod;
            if (operation is null || method is null || !method.IsExtensionMethod)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{invocation}' gives");
            }

            var receiver = GetArgument(operation, 0)?.Syntax as ExpressionSyntax;
            if (receiver is null)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{invocation}' gives");
            }

            if (IsMapGroup(method))
            {
                var prefix = GetArgument(operation, MinimalApiNames.PatternParameter)?.ConstantValue;
                var prefixText = prefix is null || !prefix.Value.HasValue ? null : prefix.Value.Value as string;
                if (prefixText is null)
                {
                    return RouteGroups.Fail($"the generator cannot read the prefix of the route group '{invocation}', as it is not a constant string");
                }

                return Resolve(receiver, semanticModel, methodsBeingFollowed, depth + 1, ct).Append(prefixText);
            }

            if (ReturnsItsBuilder(method))
            {
                return Resolve(receiver, semanticModel, methodsBeingFollowed, depth + 1, ct);
            }

            return RouteGroups.Fail($"the generator cannot tell which route group '{invocation}' gives, as only MapGroup and ASP.NET Core's conventions are followed");
        }

        private static RouteGroups ResolveIdentifier(
            IdentifierNameSyntax identifier,
            SemanticModel semanticModel,
            HashSet<SyntaxReference> methodsBeingFollowed,
            int depth,
            CancellationToken ct)
        {
            var symbol = semanticModel.GetSymbolInfo(identifier, ct).Symbol;

            var local = symbol as ILocalSymbol;
            if (local is not null)
            {
                return ResolveLocal(local, semanticModel, methodsBeingFollowed, depth, ct);
            }

            var parameter = symbol as IParameterSymbol;
            if (parameter is not null)
            {
                return ResolveParameter(parameter, semanticModel.Compilation, methodsBeingFollowed, depth, ct);
            }

            return RouteGroups.Fail($"the generator cannot tell which route group '{identifier}' is");
        }

        /// <summary>
        /// The groups of the value a local variable is declared with, when nothing assigns it another.
        /// </summary>
        private static RouteGroups ResolveLocal(
            ILocalSymbol local,
            SemanticModel semanticModel,
            HashSet<SyntaxReference> methodsBeingFollowed,
            int depth,
            CancellationToken ct)
        {
            var declarator = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) as VariableDeclaratorSyntax;
            if (declarator?.Initializer is null)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{local.Name}' is, as it is not assigned where it is declared");
            }

            var scope = local.ContainingSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
            var isAssignedAgain = scope?.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(x => x.Identifier.ValueText == local.Name && RouteAttributeReader.IsWritten(RouteAttributeReader.GetReferenceExpression(x)))
                .Any(x => SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(x, ct).Symbol, local)) ?? true;
            if (isAssignedAgain)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{local.Name}' is, as it is assigned again after its declaration");
            }

            return Resolve(declarator.Initializer.Value, semanticModel, methodsBeingFollowed, depth + 1, ct);
        }

        /// <summary>
        /// The groups of a method's parameter: those of the argument every call of the method passes for it. A parameter
        /// of the lambda that <c>app.UseEndpoints</c> calls is the app's root builder.
        /// </summary>
        private static RouteGroups ResolveParameter(
            IParameterSymbol parameter,
            Compilation compilation,
            HashSet<SyntaxReference> methodsBeingFollowed,
            int depth,
            CancellationToken ct)
        {
            var method = parameter.ContainingSymbol as IMethodSymbol;
            var methodReference = method?.DeclaringSyntaxReferences.FirstOrDefault();
            if (method is null || methodReference is null)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group the parameter '{parameter.Name}' is");
            }

            if (method.MethodKind == MethodKind.AnonymousFunction)
            {
                return IsUseEndpointsCallback(methodReference.GetSyntax(ct), compilation, ct)
                    ? RouteGroups.Root
                    : RouteGroups.Fail($"the generator cannot tell which route group the lambda parameter '{parameter.Name}' is");
            }

            var methodName = $"{method.ContainingType?.Name}.{method.Name}";
            if (!methodsBeingFollowed.Add(methodReference))
            {
                return RouteGroups.Fail($"'{methodName}' calls itself, so the generator cannot tell which route group '{parameter.Name}' is");
            }

            try
            {
                var calls = GetCalledMembers(method).SelectMany(x => FindCalls(x, compilation, ct)).ToList();
                if (calls.Count == 0)
                {
                    return RouteGroups.Fail($"the generator finds no call of '{methodName}' in the project to tell which route group '{parameter.Name}' is");
                }

                var groups = new List<RouteGroups>();
                foreach (var (call, semanticModel) in calls)
                {
                    var argument = GetArgument(call, parameter.Ordinal)?.Syntax as ExpressionSyntax;
                    groups.Add(argument is null
                        ? RouteGroups.Fail($"the generator cannot read the route group a call of '{methodName}' passes as '{parameter.Name}'")
                        : Resolve(argument, semanticModel, methodsBeingFollowed, depth + 1, ct));
                }

                var merged = RouteGroups.Merge(groups);
                return merged.Prefixes.Count > MaxPrefixes
                    ? RouteGroups.Fail($"'{methodName}' maps the endpoint at more than {MaxPrefixes} routes")
                    : merged;
            }
            finally
            {
                methodsBeingFollowed.Remove(methodReference);
            }
        }

        /// <summary>
        /// The method, and the methods a call of which may run it: those it overrides and the interface members it
        /// implements, as an app that maps endpoints in classes of its own calls each through their interface.
        /// </summary>
        private static IEnumerable<IMethodSymbol> GetCalledMembers(IMethodSymbol method)
        {
            yield return method;

            for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            {
                yield return overridden;
            }

            var containingType = method.ContainingType;
            if (containingType is null)
            {
                yield break;
            }

            foreach (var interfaceMethod in containingType.AllInterfaces.SelectMany(x => x.GetMembers()).OfType<IMethodSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(interfaceMethod), method))
                {
                    yield return interfaceMethod;
                }
            }
        }

        /// <summary>
        /// The calls of the method in the compilation, each with the semantic model of its file.
        /// </summary>
        private static List<(IInvocationOperation Call, SemanticModel SemanticModel)> FindCalls(IMethodSymbol method, Compilation compilation, CancellationToken ct)
        {
            var invocations = InvocationsByName.GetOrAdd(compilation, compilation, x => IndexInvocations(x, ct));
            var calls = new List<(IInvocationOperation Call, SemanticModel SemanticModel)>();

            foreach (var invocation in invocations[method.Name])
            {
                var semanticModel = compilation.GetSemanticModel(invocation.SyntaxTree);
                var call = semanticModel.GetOperation(invocation, ct) as IInvocationOperation;
                if (call is not null && IsSameMethod(call.TargetMethod, method))
                {
                    calls.Add((call, semanticModel));
                }
            }

            return calls;
        }

        private static ILookup<string, InvocationExpressionSyntax> IndexInvocations(Compilation compilation, CancellationToken ct)
        {
            return compilation.SyntaxTrees
                .SelectMany(x => x.GetRoot(ct).DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Select(x => (Name: GetInvokedName(x.Expression), Invocation: x))
                .Where(x => x.Name is not null)
                .ToLookup(x => x.Name!, x => x.Invocation, StringComparer.Ordinal);
        }

        /// <summary>
        /// Whether a call targets the method. A local function's symbol is made anew by each semantic model that binds it,
        /// so two of them are compared by where they are declared.
        /// </summary>
        private static bool IsSameMethod(IMethodSymbol target, IMethodSymbol method)
        {
            var targetDefinition = target.ReducedFrom ?? target;
            if (SymbolEqualityComparer.Default.Equals(targetDefinition.OriginalDefinition, method.OriginalDefinition))
            {
                return true;
            }

            var targetReference = targetDefinition.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault();
            var methodReference = method.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault();

            return targetReference is not null && methodReference is not null &&
                SyntaxReferenceComparer.Instance.Equals(targetReference, methodReference);
        }

        private static bool IsUseEndpointsCallback(SyntaxNode lambda, Compilation compilation, CancellationToken ct)
        {
            var invocation = (lambda.Parent as ArgumentSyntax)?.Parent?.Parent as InvocationExpressionSyntax;
            if (invocation is null)
            {
                return false;
            }

            var method = compilation.GetSemanticModel(invocation.SyntaxTree).GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
            var definition = method?.ReducedFrom ?? method;

            return definition is not null &&
                definition.Name == MinimalApiNames.UseEndpoints &&
                definition.ContainingType.ToDisplayString() == MinimalApiNames.EndpointRoutingExtensions;
        }

        /// <summary>
        /// The value an invocation passes for the parameter at the position, without the implicit conversion to the
        /// parameter's type, such as from <c>RouteGroupBuilder</c> to <c>IEndpointRouteBuilder</c>. An extension method's
        /// receiver is its first argument.
        /// </summary>
        internal static IOperation? GetArgument(IInvocationOperation invocation, int ordinal)
        {
            var value = invocation.Arguments.FirstOrDefault(x => x.Parameter?.Ordinal == ordinal)?.Value;
            while (value is not null && value.Kind == OperationKind.Conversion && value.IsImplicit)
            {
                value = ((IConversionOperation)value).Operand;
            }

            return value;
        }

        internal static bool IsMapGroup(IMethodSymbol method)
        {
            return method.Name == MinimalApiNames.MapGroup &&
                method.ContainingType.ToDisplayString() == MinimalApiNames.EndpointRouteBuilderExtensions;
        }

        /// <summary>
        /// Whether the method is one of ASP.NET Core's conventions, such as <c>WithTags</c> or <c>RequireAuthorization</c>,
        /// which add to the builder they are called on and return it.
        /// </summary>
        private static bool ReturnsItsBuilder(IMethodSymbol method)
        {
            var definition = method.OriginalDefinition;

            return definition.Parameters.Length > 0 &&
                (definition.ContainingNamespace?.ToDisplayString() + ".").StartsWith(MinimalApiNames.AspNetCoreNamespacePrefix, StringComparison.Ordinal) &&
                SymbolEqualityComparer.Default.Equals(definition.ReturnType, definition.Parameters[0].Type);
        }

        private static bool IsWebApplication(ITypeSymbol type)
        {
            return ResponseTypeResolver.IsOrImplements(type, MinimalApiNames.WebApplication);
        }

        /// <summary>
        /// The expression without the parentheses, casts and null-forgiving operators around it, which give the same builder.
        /// </summary>
        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (true)
            {
                var parenthesized = expression as ParenthesizedExpressionSyntax;
                var cast = expression as CastExpressionSyntax;
                var postfix = expression as PostfixUnaryExpressionSyntax;

                if (parenthesized is not null)
                {
                    expression = parenthesized.Expression;
                }
                else if (cast is not null)
                {
                    expression = cast.Expression;
                }
                else if (postfix is not null && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                {
                    expression = postfix.Operand;
                }
                else
                {
                    return expression;
                }
            }
        }

        private sealed class SyntaxReferenceComparer : IEqualityComparer<SyntaxReference>
        {
            public static readonly SyntaxReferenceComparer Instance = new SyntaxReferenceComparer();

            public bool Equals(SyntaxReference? x, SyntaxReference? y)
            {
                return x is not null && y is not null && x.SyntaxTree == y.SyntaxTree && x.Span == y.Span;
            }

            public int GetHashCode(SyntaxReference obj)
            {
                return obj.Span.GetHashCode();
            }
        }
    }
}
