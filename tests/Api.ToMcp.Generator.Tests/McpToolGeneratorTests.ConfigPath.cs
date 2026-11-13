using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// The build passes the full path of ApiToMcpConfigPath to the generator, which reads the config at that path whatever
/// the file is named. It used to read the first additional file whose name ends in generator.json, which ignored a config
/// named otherwise and read an unrelated file such as openapi-generator.json in its place. See
/// https://github.com/Kebechet/Api.ToMcp/issues/55.
/// </summary>
public partial class McpToolGeneratorTests
{
    /// <summary>
    /// The key under which the build's CompilerVisibleProperty hands the generator the configured path.
    /// </summary>
    private const string ConfigPathBuildProperty = "build_property._McpConfigFullPath";

    private const string PassedOverConfigId = "MCP015";

    private const string GetAllConfig = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""ProductsController.GetAll""] }";
    private const string GetByIdConfig = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""ProductsController.GetById""] }";
    private const string OpenApiGeneratorConfig = @"{ ""generatorName"": ""csharp"", ""outputDir"": ""client"" }";

    private static readonly string ProjectDirectory = Path.Combine(Path.GetTempPath(), "ApiToMcp.ConfigPathTests", "App");

    public static TheoryData<string> ConfigPathsWrittenAnotherWay => new()
    {
        // A relative item that csc resolves against the project, with a segment the build's full path has not.
        Path.Combine(ProjectDirectory, "Other", "..", "Config", "mcp-tools.json"),
        // The case variant the build hands the generator in place of a missing configured file (MCP101).
        Path.Combine(ProjectDirectory, "config", "MCP-tools.json"),
    };

    [Fact]
    public void Generator_ReadsTheConfigAtTheConfiguredPath_WhateverItsName()
    {
        var configPath = Path.Combine(ProjectDirectory, "Config", "mcp-tools.json");

        var generated = RunGeneratorWithConfigPath(configPath, new InMemoryAdditionalText(configPath, GetAllConfig));

        Assert.Contains("class ProductsController_GetAllTool", generated);
        Assert.DoesNotContain("class ProductsController_GetByIdTool", generated);
    }

    [Fact]
    public void Generator_ReadsTheConfigAtTheConfiguredPath_BeforeAnotherFileWhoseNameEndsInGeneratorJson()
    {
        var configPath = Path.Combine(ProjectDirectory, "Mcp", "generator.json");

        var generated = RunGeneratorWithConfigPath(
            configPath,
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "openapi-generator.json"), OpenApiGeneratorConfig),
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "Legacy", "generator.json"), GetAllConfig),
            new InMemoryAdditionalText(configPath, GetByIdConfig));

        Assert.Contains("class ProductsController_GetByIdTool", generated);
        Assert.DoesNotContain("class ProductsController_GetAllTool", generated);
    }

    [Theory]
    [MemberData(nameof(ConfigPathsWrittenAnotherWay))]
    public void Generator_ReadsTheConfigAtTheConfiguredPath_WrittenAnotherWay(string additionalFilePath)
    {
        var configPath = Path.Combine(ProjectDirectory, "Config", "mcp-tools.json");

        var generated = RunGeneratorWithConfigPath(configPath, new InMemoryAdditionalText(additionalFilePath, GetAllConfig));

        Assert.Contains("class ProductsController_GetAllTool", generated);
    }

    [Fact]
    public void Generator_ReadsAGeneratorJsonListedByHand_WhenNoFileIsAtTheConfiguredPath()
    {
        // A project that lists its config by hand and turns ApiToMcpAutoCreateConfig off has nothing at the configured
        // path, as before the build passed the path.
        var configPath = Path.Combine(ProjectDirectory, "Mcp", "generator.json");

        var generated = RunGeneratorWithConfigPath(
            configPath,
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "Legacy", "generator.json"), GetAllConfig));

        Assert.Contains("class ProductsController_GetAllTool", generated);
    }

    [Fact]
    public void Generator_PointsMcp006AtTheConfigAtTheConfiguredPath()
    {
        var configPath = Path.Combine(ProjectDirectory, "Config", "mcp-tools.json");

        var diagnostic = Assert.Single(RunGeneratorWithConfigPath(configPath, out _, new InMemoryAdditionalText(configPath, StarterConfig)));

        Assert.Equal(NoToolsGeneratedId, diagnostic.Id);
        Assert.Equal(configPath, diagnostic.Location.GetLineSpan().Path);
    }

    [Fact]
    public void Generator_ReadsTheConfigAtExactlyTheConfiguredPath_BeforeOneWhosePathDiffersOnlyInCase()
    {
        // On a case-sensitive file system both are files of their own. The case variant stands in for the configured file
        // only while that one is missing (MCP101).
        var configPath = Path.Combine(ProjectDirectory, "Config", "tools.json");

        var generated = RunGeneratorWithConfigPath(
            configPath,
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "Config", "TOOLS.json"), GetByIdConfig),
            new InMemoryAdditionalText(configPath, GetAllConfig));

        Assert.Contains("class ProductsController_GetAllTool", generated);
        Assert.DoesNotContain("class ProductsController_GetByIdTool", generated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Generator_ReadsOnlyAFileNamedGeneratorJson_WhenNoFileIsAtTheConfiguredPath(bool hasConfigPath)
    {
        // openapi-generator.json, listed first, used to be read as the config.
        var configPath = hasConfigPath ? Path.Combine(ProjectDirectory, "Mcp", "generator.json") : null;

        var generated = RunGeneratorWithConfigPath(
            configPath,
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "openapi-generator.json"), OpenApiGeneratorConfig),
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "Legacy", "GENERATOR.json"), GetAllConfig));

        Assert.Contains("class ProductsController_GetAllTool", generated);
    }

    [Fact]
    public void Generator_TellsToCreateTheConfigAtTheConfiguredPath_WhenNoFileIsThere()
    {
        // Mcp/generator.json, which MCP006 named before, is not read when the build configures another path.
        var configPath = Path.Combine(ProjectDirectory, "Config", "mcp-tools.json");

        var diagnostic = Assert.Single(RunGeneratorWithConfigPath(configPath, out _));

        Assert.Equal(NoToolsGeneratedId, diagnostic.Id);
        Assert.Equal(
            $"No MCP tools are generated: SelectedOnly mode selects none of the project's HTTP actions. No config is at ApiToMcpConfigPath, so the default config applies, whose include list is empty: create {configPath} and list controllers such as 'ProductsController' or actions such as 'ProductsController.GetById' in its include.",
            diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_WarnsThatItPassesOverAGeneratorJsonListedByHand_ForTheConfigAtTheConfiguredPath()
    {
        // Such a file was read before the build passed the configured path, and the configured file can be the starter
        // the build wrote beside it, which selects nothing.
        var configPath = Path.Combine(ProjectDirectory, "Mcp", "generator.json");
        var legacyPath = Path.Combine(ProjectDirectory, "Legacy", "generator.json");

        var diagnostics = RunGeneratorWithConfigPath(
            configPath,
            out var generated,
            new InMemoryAdditionalText(legacyPath, GetAllConfig),
            new InMemoryAdditionalText(configPath, StarterConfig));

        var diagnostic = Assert.Single(diagnostics, x => x.Id == PassedOverConfigId);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            $"The generator reads its config from '{configPath}', the file at ApiToMcpConfigPath, and not from '{legacyPath}', which the project also lists among its AdditionalFiles: to read that file, set ApiToMcpConfigPath to it, or else remove it from the AdditionalFiles",
            diagnostic.GetMessage());
        Assert.Equal(legacyPath, diagnostic.Location.GetLineSpan().Path);
        Assert.Contains(diagnostics, x => x.Id == NoToolsGeneratedId);
        Assert.DoesNotContain("class ProductsController_GetAllTool", generated);
    }

    [Fact]
    public void Generator_DoesNotWarnOfTheConfiguredFileInAnotherCase_NorOfAFileNamedOtherwise()
    {
        // On a case-insensitive file system an item listed by hand in another case is the configured file itself, which
        // the build adds once more under its own path.
        var configPath = Path.Combine(ProjectDirectory, "Mcp", "generator.json");

        var diagnostics = RunGeneratorWithConfigPath(
            configPath,
            out _,
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "mcp", "GENERATOR.json"), GetAllConfig),
            new InMemoryAdditionalText(Path.Combine(ProjectDirectory, "openapi-generator.json"), OpenApiGeneratorConfig),
            new InMemoryAdditionalText(configPath, GetAllConfig));

        Assert.Empty(diagnostics);
    }

    private static string RunGeneratorWithConfigPath(string? configPath, params AdditionalText[] additionalFiles)
    {
        RunGeneratorWithConfigPath(configPath, out var generated, additionalFiles);

        return generated;
    }

    /// <param name="configPath">The path the build passes, or null for a project without the package's build targets.</param>
    private static ImmutableArray<Diagnostic> RunGeneratorWithConfigPath(string? configPath, out string generated, params AdditionalText[] additionalFiles)
    {
        var globalOptions = new Dictionary<string, string>();
        if (configPath is not null)
        {
            globalOptions[ConfigPathBuildProperty] = configPath;
        }

        var runResult = CSharpGeneratorDriver
            .Create(
                new[] { new McpToolGenerator().AsSourceGenerator() },
                additionalFiles,
                optionsProvider: new GlobalOptionsProvider(globalOptions))
            .RunGenerators(CreateCompilation(Usings + ProductsController))
            .GetRunResult();

        generated = string.Join("\n", runResult.GeneratedTrees.Select(x => x.GetText().ToString()));

        return runResult.Diagnostics;
    }

    /// <summary>
    /// The options a build hands analyzers: those of the generated global .editorconfig, which holds the compiler-visible
    /// properties.
    /// </summary>
    private sealed class GlobalOptionsProvider : AnalyzerConfigOptionsProvider
    {
        public GlobalOptionsProvider(IReadOnlyDictionary<string, string> globalOptions)
        {
            GlobalOptions = new DictionaryOptions(globalOptions);
        }

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => DictionaryOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => DictionaryOptions.Empty;
    }

    private sealed class DictionaryOptions : AnalyzerConfigOptions
    {
        public static readonly DictionaryOptions Empty = new(new Dictionary<string, string>());

        private readonly IReadOnlyDictionary<string, string> _options;

        public DictionaryOptions(IReadOnlyDictionary<string, string> options)
        {
            _options = options;
        }

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        {
            return _options.TryGetValue(key, out value);
        }
    }
}
