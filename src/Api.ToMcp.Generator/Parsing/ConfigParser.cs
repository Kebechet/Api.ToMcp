using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Parsing
{
    internal static class ConfigParser
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public static GeneratorConfigModel Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new GeneratorConfigModel();

            try
            {
                var dto = JsonSerializer.Deserialize<ConfigDto>(json, Options);
                if (dto == null)
                    return new GeneratorConfigModel();

                return new GeneratorConfigModel
                {
                    SchemaVersion = dto.SchemaVersion,
                    Mode = ParseMode(dto.Mode),
                    Include = dto.Include != null ? dto.Include.ToImmutableArray() : ImmutableArray<string>.Empty,
                    Exclude = dto.Exclude != null ? dto.Exclude.ToImmutableArray() : ImmutableArray<string>.Empty,
                    Naming = new NamingConfigModel
                    {
                        ToolNameFormat = dto.Naming?.ToolNameFormat ?? "{Controller}_{Action}",
                        RemoveControllerSuffix = dto.Naming?.RemoveControllerSuffix ?? true
                    }
                };
            }
            catch
            {
                return new GeneratorConfigModel();
            }
        }

        private static SelectionModeModel ParseMode(string? mode)
        {
            if (mode == null)
                return SelectionModeModel.SelectedOnly;

            switch (mode.ToLowerInvariant())
            {
                case "allexceptexcluded":
                    return SelectionModeModel.AllExceptExcluded;
                default:
                    return SelectionModeModel.SelectedOnly;
            }
        }

        private sealed class ConfigDto
        {
            public int SchemaVersion { get; set; } = 1;
            public string? Mode { get; set; }
            public List<string>? Include { get; set; }
            public List<string>? Exclude { get; set; }
            public NamingDto? Naming { get; set; }
        }

        private sealed class NamingDto
        {
            public string? ToolNameFormat { get; set; }
            public bool? RemoveControllerSuffix { get; set; }
        }
    }
}
