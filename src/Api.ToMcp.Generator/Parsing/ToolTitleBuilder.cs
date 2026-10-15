using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
            var words = SplitWords(identifier);
            if (words.Count == 0)
            {
                return identifier;
            }

            var sentenceWords = words.Select((word, index) => FormatWord(word, isFirst: index == 0));
            return string.Join(" ", sentenceWords);
        }

        /// <summary>
        /// Splits an identifier into words by Unicode category, so no letter outside ASCII is lost: a lowercase letter or
        /// digit followed by an uppercase one starts a word (<c>logWorkout</c>), as does the last capital of an acronym
        /// that a lowercase letter follows (<c>XRepMax</c>), a switch between letters and digits, and a switch between
        /// cased letters and letters that have no case (<c>Get用户</c>). A combining mark stays with its letter. Anything
        /// that is not a letter, digit or mark separates words and is dropped.
        /// </summary>
        private static List<string> SplitWords(string identifier)
        {
            var words = new List<string>();
            var currentWord = new StringBuilder();
            var previousKind = CharacterKind.Separator;
            var index = 0;

            while (index < identifier.Length)
            {
                var length = char.IsSurrogatePair(identifier, index) ? 2 : 1;
                var kind = GetKind(identifier, index);

                if (kind == CharacterKind.Separator)
                {
                    AddWord(words, currentWord);
                    previousKind = CharacterKind.Separator;
                }
                else if (kind == CharacterKind.Mark)
                {
                    currentWord.Append(identifier, index, length);
                }
                else
                {
                    var isAcronymEnd = previousKind == CharacterKind.Upper &&
                        kind == CharacterKind.Upper &&
                        GetNextBaseKind(identifier, index + length) == CharacterKind.Lower;
                    if (currentWord.Length > 0 && (StartsWord(previousKind, kind) || isAcronymEnd))
                    {
                        AddWord(words, currentWord);
                    }

                    currentWord.Append(identifier, index, length);
                    previousKind = kind;
                }

                index += length;
            }

            AddWord(words, currentWord);
            return words;
        }

        private static bool StartsWord(CharacterKind previousKind, CharacterKind kind)
        {
            if (previousKind == CharacterKind.Lower && kind == CharacterKind.Upper)
            {
                return true;
            }

            var isPreviousDigit = previousKind == CharacterKind.Digit;
            var isDigit = kind == CharacterKind.Digit;
            if (isPreviousDigit != isDigit)
            {
                return true;
            }

            var isPreviousUncased = previousKind == CharacterKind.Uncased;
            var isUncased = kind == CharacterKind.Uncased;
            return !isDigit && isPreviousUncased != isUncased;
        }

        /// <summary>
        /// The kind of the next character that is not a combining mark, which decides whether an uppercase letter ends an
        /// acronym: in <c>XRepMax</c> the <c>R</c> starts a word because a lowercase letter follows it.
        /// </summary>
        private static CharacterKind GetNextBaseKind(string identifier, int index)
        {
            while (index < identifier.Length)
            {
                var kind = GetKind(identifier, index);
                if (kind != CharacterKind.Mark)
                {
                    return kind;
                }

                index += char.IsSurrogatePair(identifier, index) ? 2 : 1;
            }

            return CharacterKind.Separator;
        }

        private static CharacterKind GetKind(string identifier, int index)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(identifier, index))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                    return CharacterKind.Upper;
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.ModifierLetter:
                    return CharacterKind.Lower;
                case UnicodeCategory.OtherLetter:
                    return CharacterKind.Uncased;
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                    return CharacterKind.Digit;
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                    return CharacterKind.Mark;
                default:
                    return CharacterKind.Separator;
            }
        }

        private static void AddWord(List<string> words, StringBuilder currentWord)
        {
            if (currentWord.Length == 0)
            {
                return;
            }

            words.Add(currentWord.ToString());
            currentWord.Clear();
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

        /// <summary>
        /// A word of only uppercase letters is an acronym and a word without letters a number; both stay as written.
        /// </summary>
        private static string FormatWord(string word, bool isFirst)
        {
            var letters = word.Where(char.IsLetter).ToList();
            if (letters.Count == 0 || letters.All(char.IsUpper))
            {
                return word;
            }

            var lowercaseWord = word.ToLowerInvariant();
            return isFirst
                ? char.ToUpperInvariant(lowercaseWord[0]) + lowercaseWord.Substring(1)
                : lowercaseWord;
        }

        private enum CharacterKind
        {
            Separator,
            Upper,
            Lower,

            /// <summary>
            /// A letter of a script without case, such as Chinese or Arabic.
            /// </summary>
            Uncased,

            Digit,

            /// <summary>
            /// A combining mark, which belongs to the letter before it.
            /// </summary>
            Mark
        }
    }
}
