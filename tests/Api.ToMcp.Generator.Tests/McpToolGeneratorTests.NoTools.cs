using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// A config that selects no action builds an app whose MCP server lists no tools, with nothing pointing at the cause, so
/// the generator warns (MCP006) whenever the project has actions it could have exposed. See
/// https://github.com/Kebechet/Api.ToMcp/issues/17.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string NoToolsGeneratedId = "MCP006";

    private const string ProductsController = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        public Task<string> GetById(Guid id) => Task.FromResult(string.Empty);

        [HttpGet]
        public Task<string> GetAll() => Task.FromResult(string.Empty);
    }
}
";

    /// <summary>
    /// What the build's <c>CreateMcpGeneratorConfig</c> target writes when the project has no generator.json.
    /// </summary>
    private const string StarterConfig = @"{
  ""schemaVersion"": 1,
  ""mode"": ""SelectedOnly"",
  ""include"": [],
  ""exclude"": [],
  ""naming"": {
    ""toolNameFormat"": ""{Controller}_{Action}"",
    ""removeControllerSuffix"": true
  }
}";

    [Fact]
    public void Generator_WarnsThatNoToolsAreGenerated_ForTheStarterConfigsEmptyInclude()
    {
        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, StarterConfig));

        Assert.Equal(NoToolsGeneratedId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            "No MCP tools are generated: SelectedOnly mode selects none of the project's HTTP actions. The include list in generator.json is empty: add controllers such as 'ProductsController' or actions such as 'ProductsController.GetById' to it, or mark actions with [McpExpose].",
            diagnostic.GetMessage());
        Assert.Equal("Mcp/generator.json", diagnostic.Location.GetLineSpan().Path);
    }

    [Fact]
    public void Generator_WarnsThatNoToolsAreGenerated_WhenNoIncludeEntryMatches()
    {
        // Matching is case-sensitive, so a wrongly cased action name selects nothing, just like a misspelled controller.
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Prodcts"", ""ProductsController.getById""] }";

        var diagnostics = GetGeneratorDiagnostics(Usings + ProductsController, config);
        var diagnostic = Assert.Single(diagnostics, x => x.Id == NoToolsGeneratedId);

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        // MCP008 says at each entry what is wrong with it, so this does not repeat that entries are case-sensitive.
        Assert.Equal(
            "No MCP tools are generated: SelectedOnly mode selects none of the project's HTTP actions. No include entry in generator.json selects an action: 'Prodcts' and 'ProductsController.getById' name no controller or action of the project, as MCP008 reports. List controllers such as 'ProductsController' or actions such as 'ProductsController.GetById' in it, or mark actions with [McpExpose].",
            diagnostic.GetMessage());
        Assert.Equal(2, diagnostics.Count(x => x.Id == UnmatchedConfigEntryId));
    }

    [Theory]
    [InlineData(@"""Admin""", "'Admin' names no [Http*] action without [McpIgnore] or '/mcp' in its route")]
    [InlineData(@"""Admin"", ""Health"", ""Products.Delete""", "'Admin', 'Health' and 'Products.Delete' name no [Http*] action without [McpIgnore] or '/mcp' in its route")]
    [InlineData(@"""Admin"", ""Prodcts""", "'Admin' names no [Http*] action without [McpIgnore] or '/mcp' in its route, and 'Prodcts' names no controller or action of the project, as MCP008 reports")]
    public void Generator_WarnsThatNoToolsAreGenerated_WhenTheIncludeEntriesNameOnlyActionsKeptFromMcp(string entries, string reason)
    {
        // These entries name a controller or action, so they get no MCP008, and MCP006 says why they still select nothing.
        var config = $@"{{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [{entries}] }}";

        var diagnostic = Assert.Single(
            GetGeneratorDiagnostics(Usings + ControllersKeptFromMcp, config),
            x => x.Id == NoToolsGeneratedId);

        Assert.Equal(
            $"No MCP tools are generated: SelectedOnly mode selects none of the project's HTTP actions. No include entry in generator.json selects an action: {reason}. List controllers such as 'ProductsController' or actions such as 'ProductsController.GetById' in it, or mark actions with [McpExpose].",
            diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_WarnsThatNoToolsAreGenerated_WhenTheExcludeListCoversEveryAction()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [""Products""] }";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, config));

        Assert.Equal(NoToolsGeneratedId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            "No MCP tools are generated: AllExceptExcluded mode selects none of the project's HTTP actions. The exclude list in generator.json leaves no action to expose: remove entries from it, or mark actions with [McpExpose].",
            diagnostic.GetMessage());
    }

    [Fact]
    public void Generator_WarnsThatNoToolsAreGenerated_WhenNoConfigFileReachesTheGenerator()
    {
        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, generatorJson: null));

        Assert.Equal(NoToolsGeneratedId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            "No MCP tools are generated: SelectedOnly mode selects none of the project's HTTP actions. No generator.json is among the project's AdditionalFiles, so the default config applies, whose include list is empty: create Mcp/generator.json and list controllers such as 'ProductsController' or actions such as 'ProductsController.GetById' in its include.",
            diagnostic.GetMessage());
        Assert.Equal(Location.None, diagnostic.Location);
    }

    [Theory]
    [InlineData(@"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""ProductsController.GetAll""] }")]
    [InlineData(@"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Products""] }")]
    [InlineData(@"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [""Products.GetById""] }")]
    public void Generator_DoesNotWarn_WhenTheConfigGeneratesTools(string config)
    {
        Assert.Empty(GetGeneratorDiagnostics(Usings + ProductsController, config));
    }

    [Fact]
    public void Generator_DoesNotWarn_WhenMcpExposeGeneratesToolsDespiteAnEmptyInclude()
    {
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [McpExpose]
        [HttpGet]
        public Task<string> GetAll() => Task.FromResult(string.Empty);

        [HttpDelete(""{id}"")]
        public Task<string> Delete(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        Assert.Empty(GetGeneratorDiagnostics(Usings + controller, StarterConfig));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class HealthController : ControllerBase
    {
        public string Describe() => string.Empty;
    }
}
")]
    public void Generator_DoesNotWarn_WhenTheProjectHasNoHttpActions(string source)
    {
        Assert.Empty(GetGeneratorDiagnostics(Usings + source, StarterConfig));
        Assert.Empty(GetGeneratorDiagnostics(Usings + source, generatorJson: null));
    }

    [Fact]
    public void Generator_DoesNotWarn_WhenEveryActionIsKeptFromMcpByItsAttributeOrRoute()
    {
        // No config could expose these, so an empty server is what the code asks for, not a config mistake.
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [McpIgnore]
    [Route(""api/[controller]"")]
    public class AdminController : ControllerBase
    {
        [HttpPost(""reset"")]
        public Task<string> Reset() => Task.FromResult(string.Empty);
    }

    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [McpIgnore]
        [HttpDelete(""{id}"")]
        public Task<string> Delete(Guid id) => Task.FromResult(string.Empty);

        [HttpGet(""/mcp/products"")]
        public Task<string> ListForMcp() => Task.FromResult(string.Empty);
    }
}
";
        Assert.Empty(GetGeneratorDiagnostics(Usings + controller, StarterConfig));
    }

    private static ImmutableArray<Diagnostic> GetGeneratorDiagnostics(string source, string? generatorJson)
    {
        return CreateDriver(generatorJson)
            .RunGenerators(CreateCompilation(source))
            .GetRunResult()
            .Diagnostics;
    }
}
