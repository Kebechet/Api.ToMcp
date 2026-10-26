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
        private const string GenericProducesAttribute = "Microsoft.AspNetCore.Mvc.ProducesAttribute<T>";
        private const string StatusCodePropertyName = "StatusCode";
        private const string TypePropertyName = "Type";
        private const string OrderPropertyName = "Order";
        private const int DefaultFilterOrder = 0;

        private const string JsonSubtype = "json";
        private const string JsonStructuredSuffix = "+json";
        private const char MediaTypeSubtypeSeparator = '/';
        private const char MediaTypeParameterSeparator = ';';

        /// <summary>
        /// Types whose body is not the JSON of a describable type: results that write their own body, streams, raw JSON
        /// whose schema would be "anything", and async streams.
        /// </summary>
        private static readonly string[] UndescribableTypes =
        {
            KnownTypeNames.IActionResult,
            KnownTypeNames.IConvertToActionResult,
            KnownTypeNames.IResult,
            "System.IO.Stream",
            "System.IO.Pipelines.PipeReader",
            "System.Text.Json.JsonElement",
            "System.Text.Json.JsonDocument",
            "System.Text.Json.Nodes.JsonNode",
            "System.Collections.Generic.IAsyncEnumerable<T>"
        };

        /// <summary>
        /// Return types that name no body: a declared response type is the only type such an action has.
        /// </summary>
        private static readonly string[] OpenResultTypes =
        {
            KnownTypeNames.IActionResult,
            KnownTypeNames.ActionResult,
            KnownTypeNames.IResult
        };

        public static ResponseTypeInfo Resolve(IMethodSymbol method, INamedTypeSymbol controller, Compilation compilation)
        {
            // An action's [Produces] replaces its controller's: it runs later and sets the content types last. Both are
            // inherited, from the action an override replaces and from base controllers. [Produces] and [Produces<T>] are
            // different classes, so one chain can apply several, a base's after its override's: every one has to allow JSON.
            var actionProduces = GetProducesAttributes(method, x => x.OverriddenMethod);
            var controllerProduces = GetProducesAttributes(controller, x => x.BaseType);
            var produces = actionProduces.Count > 0 ? actionProduces : controllerProduces;
            if (!actionProduces.Concat(controllerProduces).All(CanReadProduces) ||
                !produces.All(x => CanBeJson(GetContentTypes(x, firstContentTypeArgument: 0))))
            {
                return new ResponseTypeInfo(null, hasBodylessSuccess: false);
            }

            var declaredType = GetDeclaredType(method.ReturnType);
            var successTypes = new List<ITypeSymbol>();
            var hasBodylessSuccess = false;
            var hasNonJsonSuccess = false;

            // MVC applies a controller's [ProducesResponseType] to every action, and an action's own declaration of a
            // status replaces it. Both are inherited, from the action an override replaces and from base controllers.
            // A [Produces] that names a type declares a 200 the same way.
            var actionResponses = ReadSuccessResponses(GetInheritedAttributes(method, x => x.OverriddenMethod), actionProduces);
            var actionStatusCodes = new HashSet<int>(actionResponses.Select(x => x.StatusCode));
            var controllerResponses = ReadSuccessResponses(GetInheritedAttributes(controller, x => x.BaseType), controllerProduces)
                .Where(x => !actionStatusCodes.Contains(x.StatusCode));

            foreach (var (statusCode, type, contentTypes) in actionResponses.Concat(controllerResponses))
            {
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

            // Only the action's own declarations stand in for its return type; a controller's apply to every action,
            // whatever each one returns. A return type that names no body adds nothing to them.
            if (actionResponses.Count == 0 && declaredType != null && !OpenResultTypes.Contains(declaredType.OriginalDefinition.ToDisplayString()))
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

        /// <summary>
        /// The attributes of the symbol and of each one it inherits from, nearest first.
        /// </summary>
        private static IEnumerable<AttributeData> GetInheritedAttributes<TSymbol>(TSymbol symbol, Func<TSymbol, TSymbol?> next)
            where TSymbol : class, ISymbol
        {
            for (var current = symbol; current != null; current = next(current))
            {
                foreach (var attribute in current.GetAttributes())
                    yield return attribute;
            }
        }

        /// <summary>
        /// The <c>[Produces]</c> attributes MVC applies to the symbol, its own and those it inherits. As with reflection,
        /// a nearer symbol's attribute replaces a base's only when of the same class, and <c>[Produces]</c> and each
        /// <c>[Produces&lt;T&gt;]</c> are classes of their own.
        /// </summary>
        private static List<AttributeData> GetProducesAttributes<TSymbol>(TSymbol symbol, Func<TSymbol, TSymbol?> next)
            where TSymbol : class, ISymbol
        {
            var produces = new List<AttributeData>();
            for (var current = symbol; current != null; current = next(current))
            {
                var nearerClasses = produces.Select(x => x.AttributeClass).ToList();
                produces.AddRange(current.GetAttributes()
                    .Where(x => IsProducesAttribute(x) && !nearerClasses.Contains(x.AttributeClass, SymbolEqualityComparer.Default)));
            }

            return produces;
        }

        private static List<(int StatusCode, ITypeSymbol? Type, IReadOnlyList<string> ContentTypes)> ReadSuccessResponses(IEnumerable<AttributeData> attributes, IEnumerable<AttributeData> produces)
        {
            var responses = new List<(int StatusCode, ITypeSymbol? Type, IReadOnlyList<string> ContentTypes)>();
            foreach (var attribute in attributes)
            {
                if (TryReadProducesResponseType(attribute, out var statusCode, out var type, out var contentTypes) && HttpStatusCodes.IsSuccess(statusCode))
                    responses.Add((statusCode, type, contentTypes));
            }

            // A [Produces]'s content types apply to every response, not only to its 200, and Resolve checks them.
            foreach (var attribute in produces)
            {
                var type = GetProducesType(attribute);
                if (type != null)
                    responses.Add((HttpStatusCodes.Status200OK, type, Array.Empty<string>()));
            }

            return responses;
        }

        private static bool IsProducesAttribute(AttributeData attribute)
        {
            return attribute.AttributeClass != null && IsOrImplements(attribute.AttributeClass, ProducesAttribute);
        }

        /// <summary>
        /// Whether attribute data shows what MVC does with the <c>[Produces]</c>. A subclass hands its content types to the
        /// base constructor in code, and an <c>Order</c> sorts it before or after the others whatever its scope, so with
        /// either the content types MVC writes are unknown.
        /// </summary>
        private static bool CanReadProduces(AttributeData attribute)
        {
            var definition = attribute.AttributeClass?.OriginalDefinition.ToDisplayString();
            if (definition is not (ProducesAttribute or GenericProducesAttribute))
                return false;

            return !attribute.NamedArguments.Any(x => x.Key == OrderPropertyName && x.Value.Value is int order && order != DefaultFilterOrder);
        }

        /// <summary>
        /// The type a <c>[Produces]</c> names, through <c>[Produces(typeof(T))]</c>, its <c>Type</c> property or
        /// <c>[Produces&lt;T&gt;]</c>, or null when it names only content types or sets <c>Type = null</c>.
        /// </summary>
        private static ITypeSymbol? GetProducesType(AttributeData attribute)
        {
            ITypeSymbol? type = null;
            var attributeClass = attribute.AttributeClass;
            var arguments = attribute.ConstructorArguments;

            if (attributeClass is { IsGenericType: true })
            {
                type = attributeClass.TypeArguments[0];
            }
            else if (arguments.Length > 0 && arguments[0].Kind == TypedConstantKind.Type)
            {
                type = arguments[0].Value as ITypeSymbol;
            }

            // Type = null clears the constructor's type, and with it the 200: ApiExplorer adds no response for a null type.
            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == TypePropertyName)
                    type = namedArgument.Value.Value as ITypeSymbol;
            }

            return type;
        }

        /// <summary>
        /// Reads the response a <c>[ProducesResponseType]</c> or <c>[ProducesResponseType&lt;T&gt;]</c> declares, with a
        /// null <paramref name="type"/> for a typeless one, such as <c>[ProducesResponseType(204)]</c>.
        /// </summary>
        /// <returns>
        /// False for any other attribute, and for one whose <c>Type</c> property is set to null: ApiExplorer adds no
        /// response for a null type, so the attribute neither replaces a controller's response for its status nor
        /// stands in for the action's return type, as <c>Type = null</c> on a <c>[Produces]</c> declares no 200.
        /// </returns>
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
                if (namedArgument.Key == TypePropertyName)
                {
                    if (namedArgument.Value.Value is not ITypeSymbol namedType)
                        return false;

                    type = namedType;
                }
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
        /// No content types means MVC's default, JSON. Of several, MVC picks one by the request's <c>Accept</c> header and
        /// the formatters the app registered, so every one has to be JSON.
        /// </summary>
        private static bool CanBeJson(IReadOnlyList<string> contentTypes)
        {
            return contentTypes.All(IsJsonMediaType);
        }

        /// <summary>
        /// A <c>json</c> subtype or a <c>+json</c> suffix, whatever the parameters: <c>application/json</c>,
        /// <c>text/json</c>, <c>application/problem+json</c>. Not <c>application/x-ndjson</c> or
        /// <c>application/json-seq</c>, whose body is many JSON values rather than one.
        /// </summary>
        private static bool IsJsonMediaType(string contentType)
        {
            var mediaType = contentType.Split(MediaTypeParameterSeparator)[0].Trim();
            var subtypeStart = mediaType.IndexOf(MediaTypeSubtypeSeparator);
            if (subtypeStart < 0)
                return false;

            var subtype = mediaType.Substring(subtypeStart + 1);
            return string.Equals(subtype, JsonSubtype, StringComparison.OrdinalIgnoreCase) ||
                subtype.EndsWith(JsonStructuredSuffix, StringComparison.OrdinalIgnoreCase);
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
        /// parameter. A generic base controller's <c>T</c> arrives as the concrete controller's type argument, and generic
        /// controllers and actions are skipped, as MVC serves neither, so a type parameter is a safeguard only.
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
