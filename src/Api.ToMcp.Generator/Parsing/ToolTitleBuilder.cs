using System.Linq;
using System.Text.RegularExpressions;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// Derives the title of a tool whose <c>[McpExpose]</c> sets none. MCP clients show the title in place of the tool
    /// name, in a connector directory and when asking the user to approve a call, so an action named <c>LogWorkout</c>
    /// reads "Log workout" there rather than as an identifier.
    /// </summary>
    internal static class ToolTitleBuilder
    {
        private const char ToolNameSeparator = '_';

        /// <summary>
        /// One word of an identifier: an acronym that no lowercase letter follows (<c>MCP</c> in <c>MCPTools</c>, <c>X</c>
        /// in <c>XRepMax</c>), a capitalized or lowercase word, or a number.
        /// </summary>
        private static readonly Regex WordPattern = new Regex("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+");

        /// <summary>
        /// The title for an action named <paramref name="actionName"/>, which <c>[McpExpose(ToolName)]</c> may rename to
        /// <paramref name="customToolName"/>. A custom name wins, because it is what the client lists: its part after the
        /// last underscore when that part is PascalCase, so <c>Reports_GetSummary</c> reads "Get summary", and the whole
        /// name otherwise, so <c>get_report_summary</c> keeps every word.
        /// </summary>
        public static string Build(string? customToolName, string actionName)
        {
            var titleSource = string.IsNullOrEmpty(customToolName)
                ? actionName
                : GetActionPart(customToolName!);

            return ToSentence(titleSource);
        }

        /// <summary>
        /// The identifier's words as a sentence: the first capitalized, the rest lowercase, acronyms and numbers as written.
        /// An identifier with no word in it is returned unchanged, so a tool never ends up with an empty title.
        /// </summary>
        internal static string ToSentence(string identifier)
        {
            var words = WordPattern.Matches(identifier)
                .Cast<Match>()
                .Select(x => x.Value)
                .ToList();
            if (words.Count == 0)
            {
                return identifier;
            }

            var sentenceWords = words.Select((word, index) => FormatWord(word, isFirst: index == 0));
            return string.Join(" ", sentenceWords);
        }

        private static string GetActionPart(string toolName)
        {
            var separatorIndex = toolName.LastIndexOf(ToolNameSeparator);
            var hasPascalCaseActionPart = separatorIndex >= 0 &&
                separatorIndex < toolName.Length - 1 &&
                char.IsUpper(toolName[separatorIndex + 1]);

            return hasPascalCaseActionPart
                ? toolName.Substring(separatorIndex + 1)
                : toolName;
        }

        private static string FormatWord(string word, bool isFirst)
        {
            if (word.All(char.IsUpper) || word.All(char.IsDigit))
            {
                return word;
            }

            var lowercaseWord = word.ToLowerInvariant();
            return isFirst
                ? char.ToUpperInvariant(lowercaseWord[0]) + lowercaseWord.Substring(1)
                : lowercaseWord;
        }
    }
}
