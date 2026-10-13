using System;
using System.Collections.Generic;
using System.Linq;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// What an action's successful responses carry, decided the way ApiExplorer (and so Swagger) decides it.
    /// </summary>
    internal readonly struct ResponseTypeInfo
    {
        public ResponseTypeInfo(ITypeSymbol? successType, bool hasBodylessSuccess)
        {
            SuccessType = successType;
            HasBodylessSuccess = hasBodylessSuccess;
        }

        /// <summary>
        /// The one type every successful JSON body has, or null when the tool can advertise no output schema.
        /// </summary>
        public ITypeSymbol? SuccessType { get; }

        /// <summary>
        /// Whether the action declares a success status without a body, such as <c>[ProducesResponseType(204)]</c>.
        /// </summary>
        public bool HasBodylessSuccess { get; }
    }

    internal static class ResponseTypeResolver
    {
        private const string ProducesResponseTypeAttribute = "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute";
        private const string GenericProducesResponseTypeAttribute = "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute<T>";
        private const string ProducesAttribute = "Microsoft.AspNetCore.Mvc.ProducesAttribute";
        private const string StatusCodePropertyName = "StatusCode";
        private const string TypePropertyName = "Type";

        /// <summary>
        /// Found in every JSON media type: <c>application/json</c>, <c>text/json</c>, <c>application/problem+json</c>.
        /// </summary>
        private const string JsonMediaTypeToken = "json";

        /// <summary>
        /// Types whose body is not the JSON of a describable type: results that write their own body, streams, raw JSON
        /// whose schema would be "anything", and async streams.
        /// </summary>
        private static readonly string[] UndescribableTypes =
        {
            KnownTypeNames.IActionResult,
            KnownTypeNames.IConvertToActionResult,
            "Microsoft.AspNetCore.Http.IResult",
            "System.IO.Stream",
            "System.IO.Pipelines.PipeReader",
            "System.Text.Json.JsonElement",
            "System.Text.Json.JsonDocument",
            "System.Text.Json.Nodes.JsonNode",
            "System.Collections.Generic.IAsyncEnumerable<T>"
        };

        public static ResponseTypeInfo Resolve(IMethodSymbol method, INamedTypeSymbol controller, Compilation compilation)
        {
            // An action's [Produces] replaces its controller's: it runs later and sets the content types last. Both are
            // inherited, from the action an override replaces and from base controllers.
            var produces = ActionMethodAnalyzer.FindNearestAttribute(method, x => x.OverriddenMethod, IsProducesAttribute) ??
                ActionMethodAnalyzer.FindNearestAttribute(controller, x => x.BaseType, IsProducesAttribute);
            if (produces != null && !CanBeJson(GetContentTypes(produces, firstContentTypeArgument: 0)))
            {
                return new ResponseTypeInfo(null, hasBodylessSuccess: false);
            }

            var declaredType = GetDeclaredType(method.ReturnType);
            var successTypes = new List<ITypeSymbol>();
            var hasSuccessAttribute = false;
            var hasBodylessSuccess = false;
            var hasNonJsonSuccess = false;

            foreach (var attribute in method.GetAttributes())
            {
                if (!TryReadProducesResponseType(attribute, out var statusCode, out var type, out var contentTypes) ||
                    !HttpStatusCodes.IsSuccess(statusCode))
                {
                    continue;
                }

                hasSuccessAttribute = true;
                hasNonJsonSuccess |= !CanBeJson(contentTypes);

                if (type != null && type.SpecialType != SpecialType.System_Void)
                {
                    successTypes.Add(type);
                }
                // As in ApiExplorer, a typeless 200 or 201 carries the action's declared type; any other typeless
                // success, such as 204, has no body.
                else if (statusCode is HttpStatusCodes.Status200OK or HttpStatusCodes.Status201Created)
                {
                    if (declaredType != null)
                        successTypes.Add(declaredType);
                }
                else
                {
                    hasBodylessSuccess = true;
                }
            }

            if (!hasSuccessAttribute && declaredType != null)
                successTypes.Add(declaredType);

            var distinctTypes = successTypes.Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToList();
            if (hasNonJsonSuccess || distinctTypes.Count != 1 || !CanDescribe(distinctTypes[0], compilation))
            {
                return new ResponseTypeInfo(null, hasBodylessSuccess);
            }

            return new ResponseTypeInfo(distinctTypes[0], hasBodylessSuccess);
        }

        /// <summary>
        /// The <c>typeof</c> operand for the type: <c>global::</c>-qualified and without nullable reference annotations,
        /// which <c>typeof</c> does not accept.
        /// </summary>
        public static string ToTypeOfOperand(ITypeSymbol type)
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        /// <summary>
        /// The return type without <c>Task</c>/<c>ValueTask</c> and then <c>ActionResult&lt;T&gt;</c>, or null for none.
        /// </summary>
        internal static ITypeSymbol? GetDeclaredType(ITypeSymbol returnType)
        {
            var type = returnType;
            if (type is INamedTypeSymbol namedType)
            {
                var definition = namedType.OriginalDefinition.ToDisplayString();
                if (definition is KnownTypeNames.Task or KnownTypeNames.ValueTask)
                    return null;

                if (definition is KnownTypeNames.TaskOfTResult or KnownTypeNames.ValueTaskOfTResult)
                    type = namedType.TypeArguments[0];
            }

            if (type.SpecialType == SpecialType.System_Void)
                return null;

            if (type is INamedTypeSymbol actionResult && actionResult.OriginalDefinition.ToDisplayString() == KnownTypeNames.ActionResultOfTValue)
                type = actionResult.TypeArguments[0];

            return type;
        }

        internal static bool IsOrImplements(ITypeSymbol type, string fullName)
        {
            if (type.OriginalDefinition.ToDisplayString() == fullName)
                return true;

            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (baseType.OriginalDefinition.ToDisplayString() == fullName)
                    return true;
            }

            return type.AllInterfaces.Any(x => x.OriginalDefinition.ToDisplayString() == fullName);
        }

        private static bool IsProducesAttribute(AttributeData attribute)
        {
            return attribute.AttributeClass?.ToDisplayString() == ProducesAttribute;
        }

        private static bool TryReadProducesResponseType(AttributeData attribute, out int statusCode, out ITypeSymbol? type, out IReadOnlyList<string> contentTypes)
        {
            statusCode = HttpStatusCodes.Status200OK;
            type = null;
            contentTypes = Array.Empty<string>();

            var attributeClass = attribute.AttributeClass;
            if (attributeClass == null)
                return false;

            var arguments = attribute.ConstructorArguments;
            var firstContentTypeArgument = 1;

            if (attributeClass.IsGenericType && attributeClass.OriginalDefinition.ToDisplayString() == GenericProducesResponseTypeAttribute)
            {
                type = attributeClass.TypeArguments[0];
            }
            else if (attributeClass.ToDisplayString() == ProducesResponseTypeAttribute)
            {
                if (arguments.Length > 0 && arguments[0].Kind == TypedConstantKind.Type)
                {
                    type = arguments[0].Value as ITypeSymbol;
                    arguments = arguments.RemoveAt(0);
                }
            }
            else
            {
                return false;
            }

            if (arguments.Length > 0 && arguments[0].Value is int constructorStatusCode)
                statusCode = constructorStatusCode;

            contentTypes = GetContentTypes(arguments, firstContentTypeArgument);

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == StatusCodePropertyName && namedArgument.Value.Value is int namedStatusCode)
                    statusCode = namedStatusCode;
                if (namedArgument.Key == TypePropertyName && namedArgument.Value.Value is ITypeSymbol namedType)
                    type = namedType;
            }

            return true;
        }

        private static IReadOnlyList<string> GetContentTypes(AttributeData attribute, int firstContentTypeArgument)
        {
            return GetContentTypes(attribute.ConstructorArguments, firstContentTypeArgument);
        }

        /// <summary>
        /// The content types from a <c>(string contentType, params string[] additionalContentTypes)</c> tail of
        /// constructor arguments, starting at <paramref name="firstContentTypeArgument"/>.
        /// </summary>
        private static IReadOnlyList<string> GetContentTypes(IReadOnlyList<TypedConstant> arguments, int firstContentTypeArgument)
        {
            var contentTypes = new List<string>();
            for (var i = firstContentTypeArgument; i < arguments.Count; i++)
            {
                if (arguments[i].Kind == TypedConstantKind.Array)
                {
                    contentTypes.AddRange(arguments[i].Values.Select(x => x.Value).OfType<string>());
                }
                else if (arguments[i].Value is string contentType)
                {
                    contentTypes.Add(contentType);
                }
            }

            return contentTypes;
        }

        /// <summary>
        /// No content types means MVC's default, JSON.
        /// </summary>
        private static bool CanBeJson(IReadOnlyList<string> contentTypes)
        {
            return contentTypes.Count == 0 || contentTypes.Any(x => x.IndexOf(JsonMediaTypeToken, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool CanDescribe(ITypeSymbol type, Compilation compilation)
        {
            if (!CanBeNamed(type))
                return false;

            // MVC writes a string as text/plain; an object's schema would be "anything".
            if (type.SpecialType == SpecialType.System_String || type.SpecialType == SpecialType.System_Object)
                return false;

            if (UndescribableTypes.Any(x => IsOrImplements(type, x)))
                return false;

            // The generated tool names the type in typeof(...), so it must be visible from the generated code.
            return compilation.IsSymbolAccessibleWithin(type, compilation.Assembly);
        }

        /// <summary>
        /// Whether <c>typeof(...)</c> in the generated tool can name the type: not when any part of it is a type
        /// parameter, such as the <c>T</c> of a generic base controller's <c>ActionResult&lt;List&lt;T&gt;&gt;</c>.
        /// </summary>
        private static bool CanBeNamed(ITypeSymbol type)
        {
            switch (type.TypeKind)
            {
                case TypeKind.Error:
                case TypeKind.TypeParameter:
                case TypeKind.Dynamic:
                case TypeKind.Pointer:
                case TypeKind.FunctionPointer:
                    return false;
            }

            if (type is IArrayTypeSymbol arrayType)
                return CanBeNamed(arrayType.ElementType);

            if (type is INamedTypeSymbol namedType)
            {
                if (!namedType.TypeArguments.All(CanBeNamed))
                    return false;

                return namedType.ContainingType == null || CanBeNamed(namedType.ContainingType);
            }

            return true;
        }
    }
}
