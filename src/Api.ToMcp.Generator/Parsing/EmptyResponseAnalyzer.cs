using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// Decides whether an action can succeed without a body: MVC answers a null value with 204, and results such as
    /// <c>NoContent()</c> or <c>Ok()</c> carry none. The answer is "no" only when proven, so a schema that promises a
    /// value is never wrong; anything this cannot read counts as "can be empty".
    /// </summary>
    internal static class EmptyResponseAnalyzer
    {
        private const string ObjectResult = "Microsoft.AspNetCore.Mvc.ObjectResult";
        private const string ValueParameterName = "value";
        private const string StatusCodeMethodName = "StatusCode";
        private const string StatusCodeParameterName = "statusCode";

        private static readonly string[] ActionResultTypes =
        {
            KnownTypeNames.IActionResult,
            KnownTypeNames.IConvertToActionResult
        };

        /// <summary>
        /// Results for a 4xx status: the tool reports them as errors, so they never need a body.
        /// </summary>
        private static readonly string[] ErrorResultTypes =
        {
            "Microsoft.AspNetCore.Mvc.NotFoundResult",
            "Microsoft.AspNetCore.Mvc.NotFoundObjectResult",
            "Microsoft.AspNetCore.Mvc.BadRequestResult",
            "Microsoft.AspNetCore.Mvc.BadRequestObjectResult",
            "Microsoft.AspNetCore.Mvc.UnauthorizedResult",
            "Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult",
            "Microsoft.AspNetCore.Mvc.ConflictResult",
            "Microsoft.AspNetCore.Mvc.ConflictObjectResult",
            "Microsoft.AspNetCore.Mvc.UnprocessableEntityResult",
            "Microsoft.AspNetCore.Mvc.UnprocessableEntityObjectResult",
            "Microsoft.AspNetCore.Mvc.ForbidResult",
            "Microsoft.AspNetCore.Mvc.ChallengeResult"
        };

        /// <summary>
        /// <c>ControllerBase</c> helpers whose result type does not tell the status, but whose status is an error.
        /// </summary>
        private static readonly string[] ErrorHelperNames = { "Problem", "ValidationProblem" };

        public static bool CanSucceedWithoutBody(IMethodSymbol method, Compilation compilation, CancellationToken ct)
        {
            var returnedType = UnwrapTask(method.ReturnType);

            // An action returning the value itself and declaring it nullable can send null. One declaring it not-null is
            // still read like the others: a value from code without nullable annotations reaches it without a warning.
            if (!IsActionResult(returnedType) && !IsNonNullable(returnedType))
                return true;

            var declaration = method.DeclaringSyntaxReferences
                .Select(x => x.GetSyntax(ct))
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Body != null || x.ExpressionBody != null);
            if (declaration == null)
                return true;

            var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);

            return GetReturnedExpressions(declaration)
                .SelectMany(x => GetPossibleResults(x, semanticModel, ct))
                .Any(x => !IsProvenBodyOrError(x, declaration, semanticModel, ct));
        }

        private static ITypeSymbol UnwrapTask(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var definition = namedType.OriginalDefinition.ToDisplayString();
                if (definition is KnownTypeNames.TaskOfTResult or KnownTypeNames.ValueTaskOfTResult)
                    return namedType.TypeArguments[0];
            }

            return type;
        }

        private static bool IsActionResult(ITypeSymbol type)
        {
            return ActionResultTypes.Any(x => ResponseTypeResolver.IsOrImplements(type, x));
        }

        /// <summary>
        /// A value type other than <c>Nullable&lt;T&gt;</c>, or a reference type declared not-null where nullable
        /// annotations are enabled; without them nothing is known.
        /// </summary>
        private static bool IsNonNullable(ITypeSymbol type)
        {
            if (type.IsValueType)
                return type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

            return type.NullableAnnotation == NullableAnnotation.NotAnnotated;
        }

        /// <summary>
        /// The action's own return values; those of lambdas and local functions inside it are not the action's.
        /// </summary>
        private static IEnumerable<ExpressionSyntax> GetReturnedExpressions(MethodDeclarationSyntax declaration)
        {
            if (declaration.ExpressionBody != null)
                return new[] { declaration.ExpressionBody.Expression };

            return declaration.Body!
                .DescendantNodes(x => !(x is AnonymousFunctionExpressionSyntax) && !(x is LocalFunctionStatementSyntax))
                .OfType<ReturnStatementSyntax>()
                .Where(x => x.Expression != null)
                .Select(x => x.Expression!);
        }

        /// <summary>
        /// The expressions a returned expression can evaluate to. The left side of <c>??</c> is only taken when not
        /// null, so it is marked as known to be not null; a <c>throw</c> returns nothing.
        /// </summary>
        private static IEnumerable<PossibleResult> GetPossibleResults(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    return GetPossibleResults(parenthesized.Expression, semanticModel, ct);
                case CastExpressionSyntax cast:
                    return GetPossibleResults(cast.Expression, semanticModel, ct);
                case ConditionalExpressionSyntax conditional:
                    return GetPossibleResults(conditional.WhenTrue, semanticModel, ct)
                        .Concat(GetPossibleResults(conditional.WhenFalse, semanticModel, ct));
                case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                    return GetPossibleResults(coalesce.Left, semanticModel, ct)
                        .Select(x => new PossibleResult(x.Expression, isKnownNotNull: true))
                        .Concat(GetPossibleResults(coalesce.Right, semanticModel, ct));
                case SwitchExpressionSyntax switchExpression:
                    return switchExpression.Arms.SelectMany(x => GetPossibleResults(x.Expression, semanticModel, ct));
                case ThrowExpressionSyntax _:
                    return Enumerable.Empty<PossibleResult>();
                case InvocationExpressionSyntax invocation when IsTaskWrapper(invocation, semanticModel, ct):
                    return GetPossibleResults(invocation.ArgumentList.Arguments[0].Expression, semanticModel, ct);
                case ObjectCreationExpressionSyntax creation when IsValueTaskCreation(creation, semanticModel, ct):
                    return GetPossibleResults(creation.ArgumentList!.Arguments[0].Expression, semanticModel, ct);
                default:
                    return new[] { new PossibleResult(expression, isKnownNotNull: false) };
            }
        }

        /// <summary>
        /// <c>Task.FromResult(x)</c> or <c>ValueTask.FromResult(x)</c>, which a non-async action returns its result through.
        /// </summary>
        private static bool IsTaskWrapper(InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken ct)
        {
            if (invocation.ArgumentList.Arguments.Count != 1 ||
                !(semanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol method) ||
                method.Name != nameof(Task.FromResult))
            {
                return false;
            }

            var containingType = method.ContainingType.ToDisplayString();
            return containingType is KnownTypeNames.Task or KnownTypeNames.ValueTask;
        }

        private static bool IsValueTaskCreation(ObjectCreationExpressionSyntax creation, SemanticModel semanticModel, CancellationToken ct)
        {
            return creation.ArgumentList?.Arguments.Count == 1 &&
                semanticModel.GetTypeInfo(creation, ct).Type is INamedTypeSymbol type &&
                type.OriginalDefinition.ToDisplayString() == KnownTypeNames.ValueTaskOfTResult;
        }

        private static bool IsProvenBodyOrError(PossibleResult result, MethodDeclarationSyntax declaration, SemanticModel semanticModel, CancellationToken ct)
        {
            var typeInfo = semanticModel.GetTypeInfo(result.Expression, ct);

            // The null literal has no type of its own; a task the action did not build from its value hides that value.
            if (typeInfo.Type == null || IsTask(typeInfo.Type))
                return false;

            if (!IsActionResult(typeInfo.Type))
                return result.IsKnownNotNull || IsNotNullLeaf(result.Expression, typeInfo, declaration, semanticModel, ct);

            if (ErrorResultTypes.Any(x => ResponseTypeResolver.IsOrImplements(typeInfo.Type, x)))
                return true;

            switch (semanticModel.GetOperation(result.Expression, ct))
            {
                case IInvocationOperation invocation:
                    return IsProvenHelperResult(invocation.TargetMethod, invocation.Arguments, declaration, semanticModel, ct);
                case IObjectCreationOperation creation when creation.Constructor != null:
                    return IsProvenHelperResult(creation.Constructor, creation.Arguments, declaration, semanticModel, ct);
                default:
                    return false;
            }
        }

        /// <summary>
        /// An <c>ObjectResult</c> built with a not-null <c>value</c> (<c>Ok(x)</c>, <c>CreatedAtAction(..., x)</c>,
        /// <c>new ObjectResult(x)</c>, ...), an error helper, or <c>StatusCode</c> with a constant error status.
        /// </summary>
        private static bool IsProvenHelperResult(
            IMethodSymbol method,
            IEnumerable<IArgumentOperation> arguments,
            MethodDeclarationSyntax declaration,
            SemanticModel semanticModel,
            CancellationToken ct)
        {
            var argumentList = arguments.ToList();
            var resultType = method.MethodKind == MethodKind.Constructor ? method.ContainingType : method.ReturnType;

            if (ResponseTypeResolver.IsOrImplements(resultType, ObjectResult))
            {
                var value = argumentList.FirstOrDefault(x => x.Parameter?.Name == ValueParameterName);
                if (value != null && value.ArgumentKind == ArgumentKind.Explicit)
                {
                    var valueSyntax = UnwrapConversion(value.Value).Syntax as ExpressionSyntax;
                    return valueSyntax != null && IsNotNullValue(valueSyntax, declaration, semanticModel, ct);
                }
            }

            if (!IsControllerBaseMethod(method))
                return false;

            if (ErrorHelperNames.Contains(method.Name))
                return true;

            if (method.Name == StatusCodeMethodName)
            {
                var statusCode = argumentList.FirstOrDefault(x => x.Parameter?.Name == StatusCodeParameterName)?.Value.ConstantValue;
                return statusCode != null && statusCode.Value.HasValue && statusCode.Value.Value is int code && !HttpStatusCodes.IsSuccess(code);
            }

            return false;
        }

        private static IOperation UnwrapConversion(IOperation operation)
        {
            while (operation is IConversionOperation conversion && conversion.IsImplicit)
                operation = conversion.Operand;

            return operation;
        }

        private static bool IsControllerBaseMethod(IMethodSymbol method)
        {
            for (var type = method.ContainingType; type != null; type = type.BaseType)
            {
                if (type.ToDisplayString() == KnownTypeNames.ControllerBase)
                    return true;
            }

            return false;
        }

        private static bool IsTask(ITypeSymbol type)
        {
            var definition = type.OriginalDefinition.ToDisplayString();
            return definition is KnownTypeNames.Task or KnownTypeNames.TaskOfTResult or KnownTypeNames.ValueTask or KnownTypeNames.ValueTaskOfTResult;
        }

        private static bool IsNotNullValue(ExpressionSyntax expression, MethodDeclarationSyntax declaration, SemanticModel semanticModel, CancellationToken ct)
        {
            return GetPossibleResults(expression, semanticModel, ct)
                .All(x => x.IsKnownNotNull || IsNotNullLeaf(x.Expression, semanticModel.GetTypeInfo(x.Expression, ct), declaration, semanticModel, ct));
        }

        /// <summary>
        /// A not-null value by the compiler's flow analysis: a non-nullable value type, or a reference whose null state
        /// is not-null and that does not come from code without nullable annotations. Code without them has no null
        /// state, so nothing in it is proven.
        /// </summary>
        private static bool IsNotNullLeaf(
            ExpressionSyntax expression,
            TypeInfo typeInfo,
            MethodDeclarationSyntax declaration,
            SemanticModel semanticModel,
            CancellationToken ct)
        {
            if (IsNullOrDefaultLiteral(expression))
                return false;

            var type = typeInfo.Type;
            if (type == null)
                return false;

            if (type.IsValueType)
                return type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

            return typeInfo.Nullability.FlowState == NullableFlowState.NotNull &&
                IsFromAnnotatedCode(expression, typeInfo, declaration, semanticModel, ct, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default));
        }

        /// <summary>
        /// The compiler gives a value from code without nullable annotations, such as a legacy library's
        /// <c>Find()</c>, a not-null state and a not-null expression annotation although it can be null; only the
        /// member it comes from shows it, by having no annotation. A local takes the annotation of its declaration, so
        /// whether it holds such a value depends on what is assigned to it.
        /// </summary>
        private static bool IsFromAnnotatedCode(
            ExpressionSyntax expression,
            TypeInfo typeInfo,
            MethodDeclarationSyntax declaration,
            SemanticModel semanticModel,
            CancellationToken ct,
            HashSet<ILocalSymbol> visitedLocals)
        {
            if (IsNullOrDefaultLiteral(expression) || typeInfo.Type?.IsValueType == true)
                return true;

            if (expression is AwaitExpressionSyntax awaitExpression)
                return IsAnnotatedTaskResult(semanticModel.GetSymbolInfo(awaitExpression.Expression, ct).Symbol);

            switch (semanticModel.GetSymbolInfo(expression, ct).Symbol)
            {
                case ILocalSymbol local:
                    return IsAssignedOnlyFromAnnotatedCode(local, declaration, semanticModel, ct, visitedLocals);
                case IMethodSymbol method:
                    return method.MethodKind == MethodKind.Constructor || method.ReturnNullableAnnotation != NullableAnnotation.None;
                case IPropertySymbol property:
                    return property.NullableAnnotation != NullableAnnotation.None;
                case IFieldSymbol field:
                    return field.NullableAnnotation != NullableAnnotation.None;
                case IParameterSymbol parameter:
                    return parameter.NullableAnnotation != NullableAnnotation.None;
                case null:
                    // An array element has no symbol, and its array's annotation is not followed.
                    return !(expression is ElementAccessExpressionSyntax);
                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether the awaited method, property or field declares its task's result with an annotation; a task held in a
        /// local is not followed.
        /// </summary>
        private static bool IsAnnotatedTaskResult(ISymbol? awaitedSymbol)
        {
            ITypeSymbol? taskType;
            switch (awaitedSymbol)
            {
                case IMethodSymbol method:
                    taskType = method.ReturnType;
                    break;
                case IPropertySymbol property:
                    taskType = property.Type;
                    break;
                case IFieldSymbol field:
                    taskType = field.Type;
                    break;
                default:
                    return false;
            }

            return taskType is INamedTypeSymbol namedTaskType &&
                namedTaskType.TypeArgumentNullableAnnotations.Length == 1 &&
                (namedTaskType.TypeArguments[0].IsValueType || namedTaskType.TypeArgumentNullableAnnotations[0] != NullableAnnotation.None);
        }

        /// <summary>
        /// Every value the action assigns to the local is from annotated code. A pattern variable is fine, as a pattern
        /// never binds null; values that arrive another way (<c>out</c>, <c>ref</c>, deconstruction, <c>foreach</c>,
        /// <c>is var</c>) are not followed and count as unknown.
        /// </summary>
        private static bool IsAssignedOnlyFromAnnotatedCode(
            ILocalSymbol local,
            MethodDeclarationSyntax declaration,
            SemanticModel semanticModel,
            CancellationToken ct,
            HashSet<ILocalSymbol> visitedLocals)
        {
            if (!visitedLocals.Add(local))
                return true;

            foreach (var node in declaration.DescendantNodes())
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax declarator when IsLocal(semanticModel.GetDeclaredSymbol(declarator, ct), local):
                        if (declarator.Initializer != null && !IsValueFromAnnotatedCode(declarator.Initializer.Value, declaration, semanticModel, ct, visitedLocals))
                            return false;
                        break;
                    case AssignmentExpressionSyntax assignment when assignment.Left is IdentifierNameSyntax target && IsLocal(semanticModel.GetSymbolInfo(target, ct).Symbol, local):
                        if (!IsValueFromAnnotatedCode(assignment.Right, declaration, semanticModel, ct, visitedLocals))
                            return false;
                        break;
                    case AssignmentExpressionSyntax deconstruction when deconstruction.Left is TupleExpressionSyntax tuple &&
                        tuple.Arguments.Any(x => x.Expression is IdentifierNameSyntax target && IsLocal(semanticModel.GetSymbolInfo(target, ct).Symbol, local)):
                        return false;
                    case SingleVariableDesignationSyntax designation when IsLocal(semanticModel.GetDeclaredSymbol(designation, ct), local):
                        if (!(designation.Parent is DeclarationPatternSyntax) && !(designation.Parent is RecursivePatternSyntax))
                            return false;
                        break;
                    case ForEachStatementSyntax forEach when IsLocal(semanticModel.GetDeclaredSymbol(forEach, ct), local):
                        return false;
                    case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None) &&
                        argument.Expression is IdentifierNameSyntax target && IsLocal(semanticModel.GetSymbolInfo(target, ct).Symbol, local):
                        return false;
                }
            }

            return true;
        }

        private static bool IsValueFromAnnotatedCode(
            ExpressionSyntax value,
            MethodDeclarationSyntax declaration,
            SemanticModel semanticModel,
            CancellationToken ct,
            HashSet<ILocalSymbol> visitedLocals)
        {
            return GetPossibleResults(value, semanticModel, ct).All(x =>
                x.IsKnownNotNull || IsFromAnnotatedCode(x.Expression, semanticModel.GetTypeInfo(x.Expression, ct), declaration, semanticModel, ct, visitedLocals));
        }

        private static bool IsLocal(ISymbol? symbol, ILocalSymbol local)
        {
            return SymbolEqualityComparer.Default.Equals(symbol, local);
        }

        private static bool IsNullOrDefaultLiteral(ExpressionSyntax expression)
        {
            return expression.IsKind(SyntaxKind.NullLiteralExpression) || expression.IsKind(SyntaxKind.DefaultLiteralExpression);
        }

        private readonly struct PossibleResult
        {
            public PossibleResult(ExpressionSyntax expression, bool isKnownNotNull)
            {
                Expression = expression;
                IsKnownNotNull = isKnownNotNull;
            }

            public ExpressionSyntax Expression { get; }

            /// <summary>
            /// Whether the expression is only the result when it is not null, as the left side of <c>??</c>.
            /// </summary>
            public bool IsKnownNotNull { get; }
        }
    }
}
