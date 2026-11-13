using System;
using System.Collections.Generic;
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
    /// Follows the route builder a Minimal API endpoint is mapped on back to the app. It goes through <c>MapGroup</c>
    /// calls, ASP.NET Core's conventions that return the builder they are called on, local variables assigned once, the
    /// app's own methods that return a builder, and the parameters of the app's own methods, to every call of them in the
    /// compilation. A <c>WebApplication</c> is the root.
    /// </summary>
    internal static class RouteGroupResolver
    {
        private const char PathSeparator = RouteTokens.PathSeparator;

        /// <summary>
        /// How many steps deep the generator follows a builder through the app's methods, and how many routes one endpoint
        /// may get from the methods that map it: the limits keep a cycle the generator does not see, or methods that call
        /// one another many times, from running the build long.
        /// </summary>
        private const int MaxDepth = 16;

        private const int MaxPrefixes = 32;

        /// <summary>
        /// The calls of each method, which every endpoint the method maps asks for. A local function is not kept, as each
        /// semantic model makes its symbol anew.
        /// </summary>
        private static readonly CompilationCache<IMethodSymbol, List<(IInvocationOperation Call, SemanticModel SemanticModel)>> CallsByMethod =
            new CompilationCache<IMethodSymbol, List<(IInvocationOperation Call, SemanticModel SemanticModel)>>(SymbolEqualityComparer.Default);

        public static RouteGroups Resolve(ExpressionSyntax builder, SemanticModel semanticModel, CancellationToken ct)
        {
            var context = new ResolveContext(semanticModel.Compilation, ct);

            return Resolve(builder, semanticModel, frame: null, context, depth: 0);
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

        private static RouteGroups Resolve(ExpressionSyntax expression, SemanticModel semanticModel, Frame? frame, ResolveContext context, int depth)
        {
            context.Ct.ThrowIfCancellationRequested();
            expression = Unwrap(expression);

            if (depth > MaxDepth)
            {
                return RouteGroups.Fail($"the generator follows the route builder '{expression}' no deeper than {MaxDepth} steps");
            }

            var type = semanticModel.GetTypeInfo(expression, context.Ct).Type;
            if (type is not null && IsWebApplication(type))
            {
                return RouteGroups.Root;
            }

            var invocation = expression as InvocationExpressionSyntax;
            if (invocation is not null)
            {
                return ResolveInvocation(invocation, semanticModel, frame, context, depth);
            }

            var identifier = expression as IdentifierNameSyntax;
            if (identifier is not null)
            {
                return ResolveIdentifier(identifier, semanticModel, frame, context, depth);
            }

            if (expression is ConditionalExpressionSyntax || expression.IsKind(SyntaxKind.CoalesceExpression))
            {
                return ResolveNullCheck(expression, semanticModel, frame, context, depth);
            }

            return RouteGroups.Fail($"the generator cannot tell which route group '{expression}' is");
        }

        private static RouteGroups ResolveInvocation(InvocationExpressionSyntax invocation, SemanticModel semanticModel, Frame? frame, ResolveContext context, int depth)
        {
            var operation = semanticModel.GetOperation(invocation, context.Ct) as IInvocationOperation;
            var method = operation?.TargetMethod;
            if (operation is null || method is null)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{invocation}' gives");
            }

            if (IsMapGroup(method) || ReturnsItsReceiver(method))
            {
                var receiver = GetArgument(operation, 0)?.Syntax as ExpressionSyntax;
                if (receiver is null)
                {
                    return RouteGroups.Fail($"the generator cannot tell which route group '{invocation}' gives");
                }

                var receiverGroups = Resolve(receiver, semanticModel, frame, context, depth + 1);
                if (!IsMapGroup(method))
                {
                    return receiverGroups;
                }

                var prefix = GetArgument(operation, MinimalApiNames.PatternParameter)?.ConstantValue;
                var prefixText = prefix is null || !prefix.Value.HasValue ? null : prefix.Value.Value as string;

                return prefixText is null
                    ? RouteGroups.Fail($"the generator cannot read the prefix of the route group '{invocation}', as it is not a constant string")
                    : receiverGroups.Append(prefixText);
            }

            return ResolveReturnedBuilder(invocation, operation, semanticModel, frame, context, depth);
        }

        /// <summary>
        /// The groups of the builder a method of the app's own code returns, such as <c>MapTodos(this RouteGroupBuilder
        /// group)</c> that returns <c>group</c>, or a sub-group of it: each of its <c>return</c>s, with its parameters bound
        /// to the call's arguments. A method of a referenced assembly has no code to read.
        /// </summary>
        private static RouteGroups ResolveReturnedBuilder(
            InvocationExpressionSyntax invocation,
            IInvocationOperation call,
            SemanticModel semanticModel,
            Frame? frame,
            ResolveContext context,
            int depth)
        {
            var method = call.TargetMethod.OriginalDefinition;
            var methodReference = method.DeclaringSyntaxReferences.FirstOrDefault();
            var returnedExpressions = methodReference is null ? new List<ExpressionSyntax>() : GetReturnedExpressions(methodReference.GetSyntax(context.Ct));
            if (methodReference is null || returnedExpressions.Count == 0)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{invocation}' gives, as it follows only MapGroup, ASP.NET Core's conventions and the project's own methods that return a builder");
            }

            var methodName = $"{method.ContainingType?.Name}.{method.Name}";
            if (!context.MethodsBeingFollowed.Add(methodReference))
            {
                return RouteGroups.Fail($"'{methodName}' calls itself, so the generator cannot tell which route group '{invocation}' gives");
            }

            try
            {
                var methodFrame = new Frame(methodReference, call, semanticModel, frame);
                var methodModel = SyntaxIndex.GetSemanticModel(context.Compilation, methodReference.SyntaxTree);

                return RouteGroups.Merge(returnedExpressions.Select(x => Resolve(x, methodModel, methodFrame, context, depth + 1)));
            }
            finally
            {
                context.MethodsBeingFollowed.Remove(methodReference);
            }
        }

        /// <summary>
        /// The expressions a method's body returns: the expression body, or the value of each <c>return</c> outside its
        /// lambdas and local functions.
        /// </summary>
        private static List<ExpressionSyntax> GetReturnedExpressions(SyntaxNode declaration)
        {
            var method = declaration as BaseMethodDeclarationSyntax;
            var localFunction = declaration as LocalFunctionStatementSyntax;
            var expressionBody = method?.ExpressionBody ?? localFunction?.ExpressionBody;
            if (expressionBody is not null)
            {
                return new List<ExpressionSyntax> { expressionBody.Expression };
            }

            var body = method?.Body ?? localFunction?.Body;
            if (body is null)
            {
                return new List<ExpressionSyntax>();
            }

            return body
                .DescendantNodes(x => !(x is AnonymousFunctionExpressionSyntax) && !(x is LocalFunctionStatementSyntax))
                .OfType<ReturnStatementSyntax>()
                .Select(x => x.Expression)
                .OfType<ExpressionSyntax>()
                .ToList();
        }

        private static RouteGroups ResolveIdentifier(IdentifierNameSyntax identifier, SemanticModel semanticModel, Frame? frame, ResolveContext context, int depth)
        {
            var symbol = semanticModel.GetSymbolInfo(identifier, context.Ct).Symbol;

            var local = symbol as ILocalSymbol;
            if (local is not null)
            {
                return ResolveLocal(local, semanticModel, frame, context, depth);
            }

            var parameter = symbol as IParameterSymbol;
            if (parameter is not null)
            {
                return ResolveParameter(parameter, frame, context, depth);
            }

            return RouteGroups.Fail($"the generator cannot tell which route group '{identifier}' is");
        }

        /// <summary>
        /// The groups of the value a local variable is declared with, when nothing assigns it another.
        /// </summary>
        private static RouteGroups ResolveLocal(ILocalSymbol local, SemanticModel semanticModel, Frame? frame, ResolveContext context, int depth)
        {
            var declarator = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.Ct) as VariableDeclaratorSyntax;
            if (declarator?.Initializer is null)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{local.Name}' is, as it is not assigned where it is declared");
            }

            var scope = local.ContainingSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.Ct);
            var isAssignedAgain = scope?.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(x => x.Identifier.ValueText == local.Name && RouteAttributeReader.IsWritten(RouteAttributeReader.GetReferenceExpression(x)))
                .Any(x => SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(x, context.Ct).Symbol, local)) ?? true;
            if (isAssignedAgain)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{local.Name}' is, as it is assigned again after its declaration");
            }

            return Resolve(declarator.Initializer.Value, semanticModel, frame, context, depth + 1);
        }

        /// <summary>
        /// The groups of a method's parameter: those of the argument the call being followed passes for it, or else of the
        /// argument every call of the method passes for it. A parameter of the lambda that <c>app.UseEndpoints</c> calls is
        /// the app's root builder.
        /// </summary>
        private static RouteGroups ResolveParameter(IParameterSymbol parameter, Frame? frame, ResolveContext context, int depth)
        {
            var boundFrame = frame?.Find(parameter.ContainingSymbol);
            if (boundFrame is not null)
            {
                var argument = boundFrame.GetArgument(parameter.Ordinal);
                var argumentExpression = argument?.Syntax as ExpressionSyntax;

                return argument is null || IsNullConstant(argument) || argumentExpression is null || argument.IsImplicit
                    ? RouteGroups.Fail($"the generator cannot tell which route group the parameter '{parameter.Name}' is")
                    : Resolve(argumentExpression, boundFrame.CallModel, boundFrame.Parent, context, depth + 1);
            }

            var method = parameter.ContainingSymbol as IMethodSymbol;
            var methodReference = method?.DeclaringSyntaxReferences.FirstOrDefault();
            if (method is null || methodReference is null)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group the parameter '{parameter.Name}' is");
            }

            if (method.MethodKind == MethodKind.AnonymousFunction)
            {
                return IsUseEndpointsCallback(methodReference.GetSyntax(context.Ct), context)
                    ? RouteGroups.Root
                    : RouteGroups.Fail($"the generator cannot tell which route group the lambda parameter '{parameter.Name}' is");
            }

            return ResolveForEachCall(method, methodReference, parameter.Name, context, depth, (callFrame, callDepth) =>
            {
                var argument = callFrame.GetArgument(parameter.Ordinal);
                var argumentExpression = argument?.Syntax as ExpressionSyntax;

                return argument is null || argument.IsImplicit || argumentExpression is null
                    ? RouteGroups.Fail($"the generator cannot read the route group a call of '{callFrame.MethodName}' passes as '{parameter.Name}'")
                    : Resolve(argumentExpression, callFrame.CallModel, callFrame.Parent, context, callDepth);
            });
        }

        /// <summary>
        /// The groups of <c>group is null ? app : group</c> or <c>group ?? app</c>, where <c>group</c> is a parameter of the
        /// method the expression is in: the branch each call of the method takes, as it passes <c>null</c>, or leaves out
        /// an argument whose default is <c>null</c>, or passes a builder.
        /// </summary>
        private static RouteGroups ResolveNullCheck(ExpressionSyntax expression, SemanticModel semanticModel, Frame? frame, ResolveContext context, int depth)
        {
            var (checkedExpression, whenNull, whenNotNull) = GetNullCheckBranches(expression);
            var parameter = checkedExpression is null || whenNull is null || whenNotNull is null
                ? null
                : semanticModel.GetSymbolInfo(Unwrap(checkedExpression), context.Ct).Symbol as IParameterSymbol;
            if (parameter is null)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{expression}' is, as it decides only a null check of a parameter");
            }

            return ResolveNullCheck(expression, parameter, whenNull!, whenNotNull!, semanticModel, frame, context, depth);
        }

        private static RouteGroups ResolveNullCheck(
            ExpressionSyntax expression,
            IParameterSymbol parameter,
            ExpressionSyntax whenNull,
            ExpressionSyntax whenNotNull,
            SemanticModel semanticModel,
            Frame? frame,
            ResolveContext context,
            int depth)
        {
            var boundFrame = frame?.Find(parameter.ContainingSymbol);
            if (boundFrame is not null)
            {
                var isNull = IsNullArgument(boundFrame.GetArgument(parameter.Ordinal), boundFrame);
                return isNull is null
                    ? RouteGroups.Fail($"the generator cannot tell whether '{parameter.Name}' is null, which '{expression}' decides on")
                    : Resolve(isNull.Value ? whenNull : whenNotNull, semanticModel, frame, context, depth + 1);
            }

            var method = parameter.ContainingSymbol as IMethodSymbol;
            var methodReference = method?.DeclaringSyntaxReferences.FirstOrDefault();
            if (method is null || methodReference is null || method.MethodKind == MethodKind.AnonymousFunction)
            {
                return RouteGroups.Fail($"the generator cannot tell which route group '{expression}' is");
            }

            return ResolveForEachCall(method, methodReference, parameter.Name, context, depth, (callFrame, callDepth) =>
                ResolveNullCheck(expression, parameter, whenNull, whenNotNull, semanticModel, callFrame, context, callDepth));
        }

        /// <summary>
        /// What a conditional or a <c>??</c> checks for null, and the builder it gives when that is null and when not.
        /// </summary>
        private static (ExpressionSyntax? Checked, ExpressionSyntax? WhenNull, ExpressionSyntax? WhenNotNull) GetNullCheckBranches(ExpressionSyntax expression)
        {
            var coalesce = expression as BinaryExpressionSyntax;
            if (coalesce is not null)
            {
                return (coalesce.Left, coalesce.Right, coalesce.Left);
            }

            var conditional = (ConditionalExpressionSyntax)expression;
            var (checkedExpression, isTrueWhenNull) = GetNullCheck(conditional.Condition);

            return isTrueWhenNull
                ? (checkedExpression, conditional.WhenTrue, conditional.WhenFalse)
                : (checkedExpression, conditional.WhenFalse, conditional.WhenTrue);
        }

        /// <summary>
        /// The expression a condition checks for null, and whether the condition is true when it is: <c>x is null</c>,
        /// <c>x == null</c> and <c>null == x</c> are; <c>x is not null</c> and <c>x != null</c> are not.
        /// </summary>
        private static (ExpressionSyntax? Expression, bool IsTrueWhenNull) GetNullCheck(ExpressionSyntax condition)
        {
            condition = Unwrap(condition);

            var isPattern = condition as IsPatternExpressionSyntax;
            if (isPattern is not null)
            {
                var constant = isPattern.Pattern as ConstantPatternSyntax;
                var notPattern = isPattern.Pattern as UnaryPatternSyntax;
                var negated = notPattern?.Pattern as ConstantPatternSyntax;

                if (constant is not null && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                    return (isPattern.Expression, true);

                if (negated is not null && negated.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                    return (isPattern.Expression, false);

                return (null, false);
            }

            var binary = condition as BinaryExpressionSyntax;
            if (binary is not null && (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression)))
            {
                var checkedExpression = binary.Right.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Left
                    : binary.Left.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Right
                    : null;

                return (checkedExpression, binary.IsKind(SyntaxKind.EqualsExpression));
            }

            return (null, false);
        }

        /// <summary>
        /// Whether a call passes null for a parameter: a null constant, or a left-out argument whose default is null.
        /// Unknown for a parameter of the caller that no call being followed binds.
        /// </summary>
        private static bool? IsNullArgument(IOperation? argument, Frame frame)
        {
            if (argument is null)
            {
                return null;
            }

            if (IsNullConstant(argument))
            {
                return true;
            }

            var parameterReference = argument as IParameterReferenceOperation;
            if (parameterReference is not null)
            {
                var outerFrame = frame.Parent?.Find(parameterReference.Parameter.ContainingSymbol);
                return outerFrame is null ? null : IsNullArgument(outerFrame.GetArgument(parameterReference.Parameter.Ordinal), outerFrame);
            }

            return false;
        }

        private static bool IsNullConstant(IOperation operation)
        {
            return operation.ConstantValue.HasValue && operation.ConstantValue.Value is null;
        }

        /// <summary>
        /// The groups the resolution gives for each call of the method, with the method's parameters bound to the call's
        /// arguments; a method called through an interface it implements or a base method it overrides counts too.
        /// </summary>
        private static RouteGroups ResolveForEachCall(
            IMethodSymbol method,
            SyntaxReference methodReference,
            string parameterName,
            ResolveContext context,
            int depth,
            Func<Frame, int, RouteGroups> resolve)
        {
            var methodName = $"{method.ContainingType?.Name}.{method.Name}";
            if (!context.MethodsBeingFollowed.Add(methodReference))
            {
                return RouteGroups.Fail($"'{methodName}' calls itself, so the generator cannot tell which route group '{parameterName}' is");
            }

            try
            {
                var calls = GetCalledMembers(method).SelectMany(x => FindCalls(x, context.Compilation, context.Ct)).ToList();
                if (calls.Count == 0)
                {
                    return RouteGroups.Fail($"the generator finds no call of '{methodName}' in the project to tell which route group '{parameterName}' is");
                }

                var merged = RouteGroups.Merge(calls.Select(x => resolve(new Frame(methodReference, x.Call, x.SemanticModel, parent: null), depth + 1)));
                return merged.Prefixes.Count > MaxPrefixes
                    ? RouteGroups.Fail($"'{methodName}' maps the endpoint at more than {MaxPrefixes} routes")
                    : merged;
            }
            finally
            {
                context.MethodsBeingFollowed.Remove(methodReference);
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
            return method.MethodKind == MethodKind.LocalFunction
                ? FindUncachedCalls(method, compilation, ct)
                : CallsByMethod.GetOrAdd(compilation, method.OriginalDefinition, x => FindUncachedCalls(x, compilation, ct));
        }

        /// <summary>
        /// The calls among the invocations of the method's name, told apart by the symbol each binds to, which is cheaper
        /// to get than the operation of a call whose arguments hold lambdas.
        /// </summary>
        private static List<(IInvocationOperation Call, SemanticModel SemanticModel)> FindUncachedCalls(IMethodSymbol method, Compilation compilation, CancellationToken ct)
        {
            var calls = new List<(IInvocationOperation Call, SemanticModel SemanticModel)>();

            foreach (var tree in compilation.SyntaxTrees)
            {
                foreach (var invocation in SyntaxIndex.GetInvocations(tree)[method.Name])
                {
                    var semanticModel = SyntaxIndex.GetSemanticModel(compilation, tree);
                    var target = semanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
                    if (target is null || !IsSameMethod(target, method))
                    {
                        continue;
                    }

                    var call = semanticModel.GetOperation(invocation, ct) as IInvocationOperation;
                    if (call is not null)
                    {
                        calls.Add((call, semanticModel));
                    }
                }
            }

            return calls;
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

        private static bool IsUseEndpointsCallback(SyntaxNode lambda, ResolveContext context)
        {
            var invocation = (lambda.Parent as ArgumentSyntax)?.Parent?.Parent as InvocationExpressionSyntax;
            if (invocation is null)
            {
                return false;
            }

            var method = SyntaxIndex.GetSemanticModel(context.Compilation, invocation.SyntaxTree).GetSymbolInfo(invocation, context.Ct).Symbol as IMethodSymbol;
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
        /// Whether the method is a convention that returns the builder it is called on, as ASP.NET Core's <c>WithTags</c>,
        /// <c>RequireAuthorization</c> and the others do: <c>TBuilder M&lt;TBuilder&gt;(this TBuilder builder, ...)</c>,
        /// which can return no other builder. A method that returns its builder's type itself may return a sub-group.
        /// </summary>
        private static bool ReturnsItsReceiver(IMethodSymbol method)
        {
            var definition = method.OriginalDefinition;
            var receiverType = definition.Parameters.FirstOrDefault()?.Type as ITypeParameterSymbol;

            return definition.IsExtensionMethod &&
                receiverType is not null &&
                SymbolEqualityComparer.Default.Equals(receiverType.DeclaringMethod, definition) &&
                SymbolEqualityComparer.Default.Equals(definition.ReturnType, receiverType);
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

        /// <summary>
        /// What one resolution shares: the compilation, and the methods it is following, which it must not enter again.
        /// </summary>
        private sealed class ResolveContext
        {
            public ResolveContext(Compilation compilation, CancellationToken ct)
            {
                Compilation = compilation;
                Ct = ct;
            }

            public Compilation Compilation { get; }
            public CancellationToken Ct { get; }
            public HashSet<SyntaxReference> MethodsBeingFollowed { get; } = new HashSet<SyntaxReference>(SyntaxReferenceComparer.Instance);
        }

        /// <summary>
        /// A call being followed into a method of the app's code: the method's parameters stand for the call's arguments,
        /// which are read in the caller's file, in the frame of the call the caller is in.
        /// </summary>
        private sealed class Frame
        {
            private readonly SyntaxReference _method;
            private readonly IInvocationOperation _call;

            public Frame(SyntaxReference method, IInvocationOperation call, SemanticModel callModel, Frame? parent)
            {
                _method = method;
                _call = call;
                CallModel = callModel;
                Parent = parent;
            }

            public SemanticModel CallModel { get; }
            public Frame? Parent { get; }
            public string MethodName => $"{_call.TargetMethod.ContainingType?.Name}.{_call.TargetMethod.Name}";

            /// <summary>
            /// The frame of the nearest call being followed into the method, or null.
            /// </summary>
            public Frame? Find(ISymbol method)
            {
                var methodReference = method.DeclaringSyntaxReferences.FirstOrDefault();
                for (var frame = this; frame is not null; frame = frame.Parent)
                {
                    if (methodReference is not null && SyntaxReferenceComparer.Instance.Equals(frame._method, methodReference))
                    {
                        return frame;
                    }
                }

                return null;
            }

            /// <summary>
            /// The value the call passes for the parameter at the position, its default when the call leaves it out.
            /// </summary>
            public IOperation? GetArgument(int ordinal)
            {
                return RouteGroupResolver.GetArgument(_call, ordinal);
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
