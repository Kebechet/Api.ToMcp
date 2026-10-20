using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// MVC's split of parameter types into simple and complex (<c>ModelMetadata.IsComplexType</c>). A simple type is
    /// read from one string, through a type converter that converts from string or a public static <c>TryParse</c>, so
    /// <c>[ApiController]</c> binds a parameter of it from the route or the query string on every HTTP method. A complex
    /// type is bound property by property, from the body when inferred. The runtime's <c>McpRequestValueFormatter</c>
    /// sends a query value whole by the same rule, so a strongly-typed id put in the query string arrives as one value.
    /// </summary>
    internal static class SimpleTypeClassifier
    {
        private const string TryParseMethodName = "TryParse";
        private const string CanConvertFromMethodName = "CanConvertFrom";
        private const string TypeConverterAttribute = "System.ComponentModel.TypeConverterAttribute";
        private const string TypeConverter = "System.ComponentModel.TypeConverter";
        private const string ReferenceConverter = "System.ComponentModel.ReferenceConverter";
        private const string FormatProvider = "System.IFormatProvider";
        private const string ParsableInterface = "System.IParsable";
        private const string Uri = "System.Uri";
        private const string CultureInfo = "System.Globalization.CultureInfo";

        /// <summary>
        /// Whether MVC reads a value of <paramref name="type"/> from one string. Every primitive, <c>Guid</c>, the date
        /// and time types and the like declare a <c>TryParse</c>; <c>string</c>, enums, <c>Uri</c> and
        /// <c>CultureInfo</c>, which declare none, are read through the converters <c>TypeDescriptor</c> has built in.
        /// </summary>
        public static bool IsSimpleType(ITypeSymbol type)
        {
            // MVC asks the converter and TryParse of the type a Nullable<T> wraps.
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
            }

            return type.TypeKind == TypeKind.Enum ||
                type.SpecialType is SpecialType.System_String or SpecialType.System_Enum ||
                HasConverterFromString(type) ||
                HasTryParse(type);
        }

        /// <summary>
        /// Whether <c>TypeDescriptor</c> gives the type a converter that reads a string: the one the first
        /// <c>[TypeConverter]</c> names in the order it merges attributes in, the type's, its base classes' and then its
        /// public interfaces', else a built-in one. An interface's converter thus applies only when no class names one,
        /// and that of an interface that is not public never.
        /// </summary>
        private static bool HasConverterFromString(ITypeSymbol type)
        {
            var selfAndBaseTypes = GetSelfAndBaseTypes(type).ToList();
            var publicInterfaces = type.AllInterfaces.Where(x => x.DeclaredAccessibility == Accessibility.Public);

            var converterAttribute = selfAndBaseTypes
                .Concat(publicInterfaces)
                .Select(x => x.GetAttributes().FirstOrDefault(IsTypeConverterAttribute))
                .FirstOrDefault(x => x is not null);
            if (converterAttribute is not null)
            {
                return CanConvertFromString(converterAttribute);
            }

            return selfAndBaseTypes.Any(x => x.ToDisplayString() is Uri or CultureInfo);
        }

        /// <summary>
        /// Whether the converter an attribute names reads a string, which it does only by overriding
        /// <c>CanConvertFrom</c>: <c>TypeConverter</c>'s own answers no, so a converter made for a property grid, such as
        /// <c>ExpandableObjectConverter</c>, leaves its type complex. <c>ReferenceConverter</c>, whose
        /// <c>ComponentConverter</c> every component inherits, reads one only given a context, which MVC never passes. A
        /// converter named by a string cannot be looked into and is taken to read one.
        /// </summary>
        private static bool CanConvertFromString(AttributeData converterAttribute)
        {
            var converterArgument = converterAttribute.ConstructorArguments.FirstOrDefault();
            if (converterArgument.Value is string)
            {
                return true;
            }

            if (converterArgument.Value is not ITypeSymbol converter)
            {
                return false;
            }

            foreach (var current in GetSelfAndBaseTypes(converter))
            {
                if (current.ToDisplayString() is TypeConverter or ReferenceConverter)
                {
                    return false;
                }

                if (current.GetMembers(CanConvertFromMethodName).Any(x => x.IsOverride))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether MVC finds a <c>TryParse</c> to read the type with (<c>ParameterBindingMethodCache.FindTryParseMethod</c>):
        /// an explicit <c>IParsable&lt;T&gt;.TryParse</c> the type declares, else a public static
        /// <c>bool TryParse(string, IFormatProvider, out T)</c> or <c>bool TryParse(string, out T)</c> that the type or a
        /// base class declares, or that exactly one of its interfaces implements with a body.
        /// </summary>
        private static bool HasTryParse(ITypeSymbol type)
        {
            return DeclaresExplicitParsableTryParse(type) ||
                HasTryParse(type, hasFormatProvider: true) ||
                HasTryParse(type, hasFormatProvider: false);
        }

        /// <summary>
        /// MVC looks the explicit implementation up by the name the compiler gives it, which holds the type's full name.
        /// A generic type's never matches, as the compiler names it after the type parameters.
        /// </summary>
        private static bool DeclaresExplicitParsableTryParse(ITypeSymbol type)
        {
            var typeName = type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();

            return type
                .GetMembers($"{ParsableInterface}<{typeName}>.{TryParseMethodName}")
                .Any(x => x.IsStatic);
        }

        private static bool HasTryParse(ITypeSymbol type, bool hasFormatProvider)
        {
            bool IsTryParse(IMethodSymbol method)
            {
                return method.IsStatic &&
                    !method.IsAbstract &&
                    method.DeclaredAccessibility == Accessibility.Public &&
                    method.ReturnType.SpecialType == SpecialType.System_Boolean &&
                    IsTryParseSignature(method.Parameters, type, hasFormatProvider);
            }

            if (GetSelfAndBaseTypes(type).Any(x => GetTryParseMethods(x).Any(IsTryParse)))
            {
                return true;
            }

            // When two interfaces offer one, MVC takes neither.
            return type.AllInterfaces.Count(x => GetTryParseMethods(x).Any(IsTryParse)) == 1;
        }

        private static IEnumerable<IMethodSymbol> GetTryParseMethods(ITypeSymbol type)
        {
            return type.GetMembers(TryParseMethodName).OfType<IMethodSymbol>();
        }

        private static bool IsTryParseSignature(ImmutableArray<IParameterSymbol> parameters, ITypeSymbol type, bool hasFormatProvider)
        {
            if (parameters.Length != (hasFormatProvider ? 3 : 2))
            {
                return false;
            }

            var text = parameters[0];
            var result = parameters[parameters.Length - 1];
            var isFormatProviderOrNone = !hasFormatProvider ||
                (parameters[1].RefKind == RefKind.None && parameters[1].Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == FormatProvider);

            return text.RefKind == RefKind.None &&
                text.Type.SpecialType == SpecialType.System_String &&
                isFormatProviderOrNone &&
                result.RefKind == RefKind.Out &&
                SymbolEqualityComparer.Default.Equals(result.Type, type);
        }

        private static bool IsTypeConverterAttribute(AttributeData attribute)
        {
            return attribute.AttributeClass?.ToDisplayString() == TypeConverterAttribute;
        }

        private static IEnumerable<ITypeSymbol> GetSelfAndBaseTypes(ITypeSymbol type)
        {
            // A nullable reference type displays with its "?", which no name compared here carries.
            for (ITypeSymbol? current = type.WithNullableAnnotation(NullableAnnotation.None); current is not null; current = current.BaseType)
            {
                yield return current;
            }
        }
    }
}
