using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Api.ToMcp.Generator.Emitting
{
    /// <summary>
    /// An attribute route template split as ASP.NET Core's route pattern parser splits it: literal text, in which
    /// <c>{{</c> and <c>}}</c> stand for one brace, and route parameters such as <c>{id}</c>, <c>{id:int?}</c>,
    /// <c>{page=1}</c> or <c>{*path}</c>.
    /// </summary>
    internal sealed class RouteTemplate
    {
        private const char ParameterStart = '{';
        private const char ParameterEnd = '}';
        private const char ConstraintSeparator = ':';
        private const char DefaultValueSeparator = '=';
        private const char OptionalMarker = '?';
        private const char CatchAllMarker = '*';
        private const char ConstraintArgumentsStart = '(';
        private const char ConstraintArgumentsEnd = ')';

        private RouteTemplate(IReadOnlyList<RouteTemplatePart> parts)
        {
            Parts = parts;
        }

        public IReadOnlyList<RouteTemplatePart> Parts { get; }

        /// <summary>
        /// The names of the route parameters, each once, in the order the template names them first. Route values are
        /// matched ignoring case.
        /// </summary>
        public IReadOnlyList<string> ParameterNames => Parts
            .Where(x => x.IsParameter)
            .Select(x => x.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        public bool HasParameter(string name)
        {
            return Parts.Any(x => x.IsParameter && string.Equals(x.Text, name, StringComparison.OrdinalIgnoreCase));
        }

        public static RouteTemplate Parse(string template)
        {
            var parts = new List<RouteTemplatePart>();
            var literal = new StringBuilder();
            var index = 0;

            while (index < template.Length)
            {
                var character = template[index];
                var isEscapedBrace = (character == ParameterStart || character == ParameterEnd) &&
                    index + 1 < template.Length && template[index + 1] == character;

                if (isEscapedBrace)
                {
                    literal.Append(character);
                    index += 2;
                }
                else if (character == ParameterStart)
                {
                    if (literal.Length > 0)
                    {
                        parts.Add(RouteTemplatePart.Literal(literal.ToString()));
                        literal.Clear();
                    }

                    index = ReadParameter(template, index + 1, parts);
                }
                else
                {
                    literal.Append(character);
                    index++;
                }
            }

            if (literal.Length > 0)
            {
                parts.Add(RouteTemplatePart.Literal(literal.ToString()));
            }

            return new RouteTemplate(parts);
        }

        /// <summary>
        /// Reads the route parameter that starts at the index, just after its opening brace, up to the brace that closes
        /// it, which is the first one not doubled: a regex constraint doubles the braces it contains.
        /// </summary>
        private static int ReadParameter(string template, int index, List<RouteTemplatePart> parts)
        {
            var content = new StringBuilder();
            while (index < template.Length)
            {
                var character = template[index];
                var isDoubled = index + 1 < template.Length && template[index + 1] == character;

                if (character == ParameterEnd && !isDoubled)
                {
                    index++;
                    break;
                }

                content.Append(character);
                index += (character == ParameterStart || character == ParameterEnd) && isDoubled ? 2 : 1;
            }

            parts.Add(CreateParameter(content.ToString()));

            return index;
        }

        /// <summary>
        /// A parameter is optional when it ends in <c>?</c>, has a default value or catches all the rest of the path: MVC
        /// matches a URL that leaves it out at the end.
        /// </summary>
        private static RouteTemplatePart CreateParameter(string content)
        {
            var isCatchAll = content.StartsWith(CatchAllMarker.ToString(), StringComparison.Ordinal);
            var definition = content.TrimStart(CatchAllMarker);
            var nameEnd = definition.IndexOfAny(new[] { ConstraintSeparator, DefaultValueSeparator, OptionalMarker });
            var name = nameEnd < 0 ? definition : definition.Substring(0, nameEnd);
            var hasDefaultValue = HasDefaultValue(definition);
            var isOptional = isCatchAll ||
                definition.EndsWith(OptionalMarker.ToString(), StringComparison.Ordinal) ||
                hasDefaultValue;

            return RouteTemplatePart.Parameter(name, isOptional, isCatchAll, hasDefaultValue);
        }

        /// <summary>
        /// Whether an <c>=</c> outside a constraint's parentheses gives the parameter a default value.
        /// </summary>
        private static bool HasDefaultValue(string definition)
        {
            var depth = 0;
            foreach (var character in definition)
            {
                if (character == ConstraintArgumentsStart)
                    depth++;
                else if (character == ConstraintArgumentsEnd && depth > 0)
                    depth--;
                else if (character == DefaultValueSeparator && depth == 0)
                    return true;
            }

            return false;
        }
    }

    internal sealed class RouteTemplatePart
    {
        private RouteTemplatePart(string text, bool isParameter, bool isOptional, bool isCatchAll, bool hasDefaultValue)
        {
            Text = text;
            IsParameter = isParameter;
            IsOptional = isOptional;
            IsCatchAll = isCatchAll;
            HasDefaultValue = hasDefaultValue;
        }

        /// <summary>
        /// The literal text, braces unescaped, or the parameter's name.
        /// </summary>
        public string Text { get; }

        public bool IsParameter { get; }
        public bool IsOptional { get; }
        public bool IsCatchAll { get; }

        /// <summary>
        /// Whether the template gives the parameter a default value, <c>{page=1}</c>, which MVC binds when a URL leaves it out.
        /// </summary>
        public bool HasDefaultValue { get; }

        public static RouteTemplatePart Literal(string text)
        {
            return new RouteTemplatePart(text, isParameter: false, isOptional: false, isCatchAll: false, hasDefaultValue: false);
        }

        public static RouteTemplatePart Parameter(string name, bool isOptional, bool isCatchAll, bool hasDefaultValue)
        {
            return new RouteTemplatePart(name, isParameter: true, isOptional, isCatchAll, hasDefaultValue);
        }
    }
}
