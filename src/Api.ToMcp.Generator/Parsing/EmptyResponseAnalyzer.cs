using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private const string ControllerBase = "Microsoft.AspNetCore.Mvc.ControllerBase";
        private const string ObjectResult = "Microsoft.AspNetCore.Mvc.ObjectResult";
        private const string ValueParameterName = "value";

        private static readonly string[] ActionResultTypes =
        {
            "Microsoft.AspNetCore.Mvc.IActionResult",
            "Microsoft.AspNetCore.Mvc.Infrastructure.IConvertToActionResult"
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

            // An action returning the value itself: the compiler enforces its nullable annotation.
            if (!IsActionResult(returnedType))
                return !IsNonNullable(returnedType);

            var declaration = method.DeclaringSyntaxReferences
                .Select(x => x.GetSyntax(ct))
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(x => x.Body != null || x.ExpressionBody != null);
            if (declaration == null)
                return true;

            var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);

            return GetReturnedExpressions(declaration)
                .SelectMany(x => GetPossibleResults(x, semanticModel, ct))
                .Any(x => !IsProvenBodyOrError(x, semanticModel, ct));
        }

        private static ITypeSymbol UnwrapTask(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var definition = namedType.OriginalDefinition.ToDisplayString();
                if (definition == "System.Threading.Tasks.Task<TResult>" || definition == "System.Threading.Tasks.ValueTask<TResult>")
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
                method.Name != "FromResult")
            {
                return false;
            }

            var containingType = method.ContainingType.ToDisplayString();
            return containingType == "System.Threading.Tasks.Task" || containingType == "System.Threading.Tasks.ValueTask";
        }

        private static bool IsValueTaskCreation(ObjectCreationExpressionSyntax creation, SemanticModel semanticModel, CancellationToken ct)
        {
            return creation.ArgumentList?.Arguments.Count == 1 &&
                semanticModel.GetTypeInfo(creation, ct).Type is INamedTypeSymbol type &&
                type.OriginalDefinition.ToDisplayString() == "System.Threading.Tasks.ValueTask<TResult>";
        }

        private static bool IsProvenBodyOrError(PossibleResult result, SemanticModel semanticModel, CancellationToken ct)
        {
            var typeInfo = semanticModel.GetTypeInfo(result.Expression, ct);

            // The null literal has no type of its own.
            if (typeInfo.Type == null)
                return false;

            if (!IsActionResult(typeInfo.Type))
                return result.IsKnownNotNull || IsNotNullValue(result.Expression, typeInfo);

            if (ErrorResultTypes.Any(x => ResponseTypeResolver.IsOrImplements(typeInfo.Type, x)))
                return true;

            switch (semanticModel.GetOperation(result.Expression, ct))
            {
                case IInvocationOperation invocation:
                    return IsProvenHelperResult(invocation.TargetMethod, invocation.Arguments, semanticModel, ct);
                case IObjectCreationOperation creation when creation.Constructor != null:
                    return IsProvenHelperResult(creation.Constructor, creation.Arguments, semanticModel, ct);
                default:
                    return false;
            }
        }

        /// <summary>
        /// An <c>ObjectResult</c> built with a not-null <c>value</c> (<c>Ok(x)</c>, <c>CreatedAtAction(..., x)</c>,
        /// <c>new ObjectResult(x)</c>, ...), an error helper, or <c>StatusCode</c> with a constant error status.
        /// </summary>
        private static bool IsProvenHelperResult(IMethodSymbol method, IEnumerable<IArgumentOperation> arguments, SemanticModel semanticModel, CancellationToken ct)
        {
            var argumentList = arguments.ToList();
            var resultType = method.MethodKind == MethodKind.Constructor ? method.ContainingType : method.ReturnType;

            if (ResponseTypeResolver.IsOrImplements(resultType, ObjectResult))
            {
                var value = argumentList.FirstOrDefault(x => x.Parameter?.Name == ValueParameterName);
                if (value != null && value.ArgumentKind == ArgumentKind.Explicit)
                {
                    var valueSyntax = UnwrapConversion(value.Value).Syntax as ExpressionSyntax;
                    return valueSyntax != null && IsNotNullValue(valueSyntax, semanticModel.GetTypeInfo(valueSyntax, ct));
                }
            }

            if (!IsControllerBaseMethod(method))
                return false;

            if (ErrorHelperNames.Contains(method.Name))
                return true;

            if (method.Name == "StatusCode")
            {
                var statusCode = argumentList.FirstOrDefault(x => x.Parameter?.Name == "statusCode")?.Value.ConstantValue;
                return statusCode != null && statusCode.Value.HasValue && statusCode.Value.Value is int code && (code < 200 || code > 299);
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
                if (type.ToDisplayString() == ControllerBase)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// A not-null value by the compiler's flow analysis: a non-nullable value type, or a reference whose null state
        /// is not-null. Code without nullable annotations has no null state, so nothing in it is proven.
        /// </summary>
        private static bool IsNotNullValue(ExpressionSyntax expression, TypeInfo typeInfo)
        {
            if (expression.IsKind(SyntaxKind.NullLiteralExpression) || expression.IsKind(SyntaxKind.DefaultLiteralExpression))
                return false;

            var type = typeInfo.Type;
            if (type == null)
                return false;

            if (type.IsValueType)
                return type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

            return typeInfo.Nullability.FlowState == NullableFlowState.NotNull;
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
