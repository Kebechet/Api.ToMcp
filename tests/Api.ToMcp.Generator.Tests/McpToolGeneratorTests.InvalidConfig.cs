using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// A generator.json the generator cannot use as written falls back to a default, which used to happen silently and then
/// drew MCP006 advice about an include list the file does have. MCP001 names the problem and where it is instead. See
/// https://github.com/Kebechet/Api.ToMcp/issues/17.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string InvalidConfigId = "MCP001";

    private const string DefaultConfigFallbackMessage =
        "generator.json cannot be used as written, so the default config applies, which is SelectedOnly mode with an empty include list: ";

    [Theory]
    [InlineData(@"{
  ""schemaVersion"": 1,
  ""mode"": ""SelectedOnly"",
  ""include"": [""ProductsController""
}", 4)]
    [InlineData(@"{
  ""schemaVersion"": 1,
  ""mode"": ""SelectedOnly"",
  ""include"": [""ProductsController"",]
}", 3)]
    [InlineData(@"{
  ""schemaVersion"": 1,
  ""mode"": ""SelectedOnly"",
  ""include"": ""ProductsController""
}", 3)]
    public void Generator_ReportsTheParseErrorAtItsLine_InsteadOfIncludeAdvice(string config, int expectedLine)
    {
        // The default config selects nothing, but MCP006 would blame an include list the file does have.
        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, config));

        Assert.Equal(InvalidConfigId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.StartsWith(DefaultConfigFallbackMessage, diagnostic.GetMessage());
        Assert.Equal("Mcp/generator.json", diagnostic.Location.GetLineSpan().Path);
        Assert.Equal(expectedLine, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void Generator_ReportsAnUnknownMode_InsteadOfIncludeAdvice()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExclude"", ""exclude"": [""ProductsController.GetAll""] }";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, config));

        Assert.Equal(InvalidConfigId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            "generator.json cannot be used as written, so SelectedOnly mode applies: mode 'AllExceptExclude' must be one of SelectedOnly, AllExceptExcluded",
            diagnostic.GetMessage());
        Assert.Equal("Mcp/generator.json", diagnostic.Location.GetLineSpan().Path);
    }

    [Fact]
    public void Generator_KeepsTheRestOfTheConfig_WhenOnlyTheModeIsUnknown()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""Selected"", ""include"": [""ProductsController.GetAll""] }";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, config));
        var generated = RunGenerator(Usings + ProductsController, config);

        Assert.Equal(InvalidConfigId, diagnostic.Id);
        Assert.Contains("class ProductsController_GetAllTool", generated);
        Assert.DoesNotContain("class ProductsController_GetByIdTool", generated);
    }

    [Fact]
    public void Generator_ReportsAConfigFileItCannotRead()
    {
        var diagnostics = CSharpGeneratorDriver
            .Create(new McpToolGenerator())
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(new UnreadableAdditionalText("Mcp/generator.json")))
            .RunGenerators(CreateCompilation(Usings + ProductsController))
            .GetRunResult()
            .Diagnostics;

        var diagnostic = Assert.Single(diagnostics);

        Assert.Equal(InvalidConfigId, diagnostic.Id);
        Assert.Equal(DefaultConfigFallbackMessage + "the file could not be read", diagnostic.GetMessage());
        Assert.Equal("Mcp/generator.json", diagnostic.Location.GetLineSpan().Path);
    }

    /// <summary>
    /// What Roslyn hands a generator for an additional file it could not read: a null text.
    /// </summary>
    private sealed class UnreadableAdditionalText : AdditionalText
    {
        public UnreadableAdditionalText(string path)
        {
            Path = path;
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => null;
    }
}
