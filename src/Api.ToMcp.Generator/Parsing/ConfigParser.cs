using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
                return new GeneratorConfigModel();

            try
            {
                var dto = JsonSerializer.Deserialize<ConfigDto>(json, Options);
                if (dto == null)
                    return new GeneratorConfigModel();

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
                            Reason = $"mode '{dto.Mode}' must be one of {string.Join(", ", Enum.GetNames(typeof(SelectionModeModel)))}"
                        }
                        : null
                };
            }
            catch (JsonException ex)
            {
                // Both count from zero, as LinePosition does.
                return CreateDefaultConfig(ex.Message, new LinePosition((int)(ex.LineNumber ?? 0), (int)(ex.BytePositionInLine ?? 0)));
            }
            catch (Exception ex)
            {
                return CreateDefaultConfig(ex.Message, default);
            }
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
