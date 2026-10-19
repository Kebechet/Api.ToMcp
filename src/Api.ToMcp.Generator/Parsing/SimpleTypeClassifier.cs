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
                DeclaresTryParse(type);
        }

        /// <summary>
        /// Whether <c>TypeDescriptor</c> gives the type a converter that reads a string: the one the nearest
        /// <c>[TypeConverter]</c> of the type or a base type names, else a built-in one.
        /// </summary>
        private static bool HasConverterFromString(ITypeSymbol type)
        {
            var selfAndBaseTypes = GetSelfAndBaseTypes(type).ToList();

            var converterAttribute = selfAndBaseTypes
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
        /// Whether the type declares a public static <c>bool TryParse(string, out T)</c> or
        /// <c>bool TryParse(string, IFormatProvider, out T)</c>, the two signatures MVC binds through.
        /// </summary>
        private static bool DeclaresTryParse(ITypeSymbol type)
        {
            return type
                .GetMembers(TryParseMethodName)
                .OfType<IMethodSymbol>()
                .Any(x => x.IsStatic &&
                    x.DeclaredAccessibility == Accessibility.Public &&
                    x.ReturnType.SpecialType == SpecialType.System_Boolean &&
                    IsTryParseSignature(x.Parameters, type));
        }

        private static bool IsTryParseSignature(ImmutableArray<IParameterSymbol> parameters, ITypeSymbol type)
        {
            if (parameters.Length is not (2 or 3))
            {
                return false;
            }

            var text = parameters[0];
            var result = parameters[parameters.Length - 1];
            var hasFormatProviderOrNone = parameters.Length == 2 ||
                (parameters[1].RefKind == RefKind.None && parameters[1].Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() == FormatProvider);

            return text.RefKind == RefKind.None &&
                text.Type.SpecialType == SpecialType.System_String &&
                hasFormatProviderOrNone &&
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
