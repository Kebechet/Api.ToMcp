using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// Writes a parameter's default value, which Roslyn gives as a plain constant, back as a C# expression of the
    /// parameter's type for the tool method to declare. The constant alone would not compile everywhere: a <c>1.5</c> is a
    /// double that no <c>float</c> or <c>decimal</c> takes, an enum's constant is its underlying number, and the current
    /// culture can write a decimal comma. A NaN or infinite default becomes <c>null</c>: the MCP SDK writes the default
    /// into the tool's input schema, as JSON, which has neither, and throws at startup; a tool sends nothing for null, so
    /// MVC still applies the action's own default.
    /// </summary>
    internal static class DefaultValueExpressionBuilder
    {
        private const string NullLiteral = "null";
        private const string FloatSuffix = "F";
        private const string DoubleSuffix = "D";
        private const string DecimalSuffix = "M";
        private const string LongSuffix = "L";
        private const string UnsignedSuffix = "U";
        private const string UnsignedLongSuffix = "UL";

        /// <summary>
        /// The format that writes a float or a double so that it parses back to the same value.
        /// </summary>
        private const string RoundTripFormat = "R";

        public static string Build(object? value, ITypeSymbol type)
        {
            if (value is null)
            {
                return NullLiteral;
            }

            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
            }

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                return BuildEnumValue(value, enumType);
            }

            switch (value)
            {
                case string text:
                    return SymbolDisplay.FormatLiteral(text, quote: true);
                case char character:
                    return SymbolDisplay.FormatLiteral(character, quote: true);
                case bool boolean:
                    return boolean ? "true" : "false";
                case float number:
                    return BuildFloat(number);
                case double number:
                    return BuildDouble(number);
                case decimal number:
                    return number.ToString(CultureInfo.InvariantCulture) + DecimalSuffix;
                case long number:
                    return number.ToString(CultureInfo.InvariantCulture) + LongSuffix;
                case uint number:
                    return number.ToString(CultureInfo.InvariantCulture) + UnsignedSuffix;
                case ulong number:
                    return number.ToString(CultureInfo.InvariantCulture) + UnsignedLongSuffix;
                default:
                    return FormatNumber(value);
            }
        }

        /// <summary>
        /// The enum member of that value, or a cast of the number when no member has it, as for a combination of flags.
        /// </summary>
        private static string BuildEnumValue(object value, INamedTypeSymbol enumType)
        {
            var typeName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var member = enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .FirstOrDefault(x => x.HasConstantValue && Equals(x.ConstantValue, value));

            if (member != null)
            {
                return $"{typeName}.{EscapeIdentifier(member.Name)}";
            }

            var number = FormatNumber(value);

            // Without the parentheses, (T)-1 would be read as a subtraction from T.
            return number.StartsWith("-", StringComparison.Ordinal)
                ? $"({typeName})({number})"
                : $"({typeName}){number}";
        }

        private static string BuildFloat(float number)
        {
            return float.IsNaN(number) || float.IsInfinity(number)
                ? NullLiteral
                : number.ToString(RoundTripFormat, CultureInfo.InvariantCulture) + FloatSuffix;
        }

        private static string BuildDouble(double number)
        {
            return double.IsNaN(number) || double.IsInfinity(number)
                ? NullLiteral
                : number.ToString(RoundTripFormat, CultureInfo.InvariantCulture) + DoubleSuffix;
        }

        private static string FormatNumber(object value)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? NullLiteral;
        }

        private static string EscapeIdentifier(string name)
        {
            return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
        }
    }
}
