using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis.Text;

namespace Api.ToMcp.Generator.Parsing
{
    internal static class ConfigParser
    {
        private const string DefaultConfigFallback = "the default config applies, which is SelectedOnly mode with an empty include list";
        private const string UnknownModeFallback = "SelectedOnly mode applies";
        private const string UnreadableFileReason = "the file could not be read";
        private const string EmptyFileReason = "the file is empty";
        private const string NullFileReason = "the file holds null instead of a JSON object";

        /// <summary>
        /// Starts what System.Text.Json appends to a <see cref="JsonException"/>'s message: the JSON path and a line and
        /// byte position counted from zero, which would contradict the diagnostic's location, counted from one.
        /// </summary>
        private const string JsonExceptionPathMarker = " Path: ";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        /// <param name="json">The file's text, or null when it could not be read.</param>
        public static GeneratorConfigModel Parse(string? json)
        {
            if (json == null)
                return CreateDefaultConfig(UnreadableFileReason, default);

            if (string.IsNullOrWhiteSpace(json))
                return CreateDefaultConfig(EmptyFileReason, default);

            try
            {
                var dto = JsonSerializer.Deserialize<ConfigDto>(json, Options);
                if (dto == null)
                    return CreateDefaultConfig(NullFileReason, default);

                var mode = ParseMode(dto.Mode);

                return new GeneratorConfigModel
                {
                    SchemaVersion = dto.SchemaVersion,
                    Mode = mode ?? SelectionModeModel.SelectedOnly,
                    Include = dto.Include != null ? dto.Include.ToImmutableArray() : ImmutableArray<string>.Empty,
                    Exclude = dto.Exclude != null ? dto.Exclude.ToImmutableArray() : ImmutableArray<string>.Empty,
                    Naming = new NamingConfigModel
                    {
                        ToolNameFormat = dto.Naming?.ToolNameFormat ?? "{Controller}_{Action}",
                        RemoveControllerSuffix = dto.Naming?.RemoveControllerSuffix ?? true
                    },
                    StructuredOutput = dto.StructuredOutput ?? true,
                    Error = mode == null
                        ? new ConfigErrorModel
                        {
                            Fallback = UnknownModeFallback,
                            Reason = $"mode '{dto.Mode}' must be one of {string.Join(", ", Enum.GetNames(typeof(SelectionModeModel)))}",
                            Position = FindModeValue(json)
                        }
                        : null
                };
            }
            catch (JsonException ex)
            {
                return CreateDefaultConfig(GetReason(ex), GetPosition(json, ex.LineNumber ?? 0, ex.BytePositionInLine ?? 0));
            }
            catch (Exception ex)
            {
                return CreateDefaultConfig(ex.Message, default);
            }
        }

        private static string GetReason(JsonException exception)
        {
            var pathStart = exception.Message.LastIndexOf(JsonExceptionPathMarker, StringComparison.Ordinal);

            return pathStart < 0 ? exception.Message : exception.Message.Substring(0, pathStart);
        }

        /// <summary>
        /// Converts the parser's position, which counts from zero in lines and in the line's UTF-8 bytes, to the character
        /// column that the build output and editors show.
        /// </summary>
        private static LinePosition GetPosition(string json, long lineNumber, long bytePositionInLine)
        {
            var lines = json.Split('\n');
            if (lineNumber >= lines.Length)
                return new LinePosition((int)lineNumber, 0);

            var lineBytes = Encoding.UTF8.GetBytes(lines[lineNumber]);
            var byteCount = (int)Math.Min(bytePositionInLine, lineBytes.Length);

            return new LinePosition((int)lineNumber, Encoding.UTF8.GetCharCount(lineBytes, 0, byteCount));
        }

        /// <summary>
        /// Where the value of the top-level mode property starts, or the file's start when there is none to find.
        /// </summary>
        private static LinePosition FindModeValue(string json)
        {
            var utf8 = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName &&
                        reader.CurrentDepth == 1 &&
                        string.Equals(reader.GetString(), nameof(ConfigDto.Mode), StringComparison.OrdinalIgnoreCase) &&
                        reader.Read())
                    {
                        var textBefore = Encoding.UTF8.GetString(utf8, 0, (int)reader.TokenStartIndex);
                        var lineStart = textBefore.LastIndexOf('\n') + 1;

                        return new LinePosition(textBefore.Count(x => x == '\n'), textBefore.Length - lineStart);
                    }
                }
            }
            catch (JsonException)
            {
            }

            return default;
        }

        private static GeneratorConfigModel CreateDefaultConfig(string reason, LinePosition position)
        {
            return new GeneratorConfigModel
            {
                Error = new ConfigErrorModel
                {
                    Fallback = DefaultConfigFallback,
                    Reason = reason,
                    Position = position
                }
            };
        }

        /// <returns>The mode, <see cref="SelectionModeModel.SelectedOnly"/> when none is set, or null for an unknown one.</returns>
        private static SelectionModeModel? ParseMode(string? mode)
        {
            if (mode == null)
                return SelectionModeModel.SelectedOnly;

            foreach (SelectionModeModel candidate in Enum.GetValues(typeof(SelectionModeModel)))
            {
                if (string.Equals(mode, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        private sealed class ConfigDto
        {
            public int SchemaVersion { get; set; } = 1;
            public string? Mode { get; set; }
            public List<string>? Include { get; set; }
            public List<string>? Exclude { get; set; }
            public NamingDto? Naming { get; set; }
            public bool? StructuredOutput { get; set; }
        }

        private sealed class NamingDto
        {
            public string? ToolNameFormat { get; set; }
            public bool? RemoveControllerSuffix { get; set; }
        }
    }
}
