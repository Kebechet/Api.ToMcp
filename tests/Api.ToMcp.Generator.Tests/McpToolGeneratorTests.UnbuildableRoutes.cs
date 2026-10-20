using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Actions whose route a tool cannot build. Their tool would not compile, or would call an endpoint MVC does not serve them
/// at, so the generator leaves it out and warns with MCP009 at the action. See https://github.com/Kebechet/Api.ToMcp/issues/46.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string UnbuildableRouteId = "MCP009";

    [Fact]
    public void Generator_SkipsAnActionWithARouteValueNothingFills_AndWarnsAtTheAction()
    {
        // The action reads {id} itself, from RouteData; the tool has no argument for it, and the placeholder used to stay
        // in the URL as C# that does not compile (CS0103).
        const string source = SimpleTypeSources.Usings + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet(""z/{id}"")]
        public IActionResult Z() => Ok(RouteData.Values[""id""]);

        [HttpGet(""all"")]
        public IActionResult All() => Ok();
    }
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);
        var generated = RunGenerator(source, AllExceptExcludedConfig);
        var errors = CompileWithGeneratedTools(source, AllExceptExcludedConfig);

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(
            "No MCP tool is generated for action 'OrdersController.Z': no parameter or property fills the route value '{id}' of GET /api/orders/z/{id}",
            warning.GetMessage());
        Assert.Equal("Z", GetLocatedText(warning.Location, source));
        Assert.DoesNotContain("OrdersController_ZTool", generated);
        Assert.Contains("typeof(OrdersController_AllTool)", generated);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
    }

    private const string UnroutableController = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ComputedVerbsController : ControllerBase
    {
        [HttpGet(""z/{id}"")]
        public Task<string> Z() => Task.FromResult(string.Empty);
    }
}
";

    [Fact]
    public void Generator_WarnsThatNoToolsAreGenerated_WhenTheIncludeSelectsOnlyActionsWithoutARoute()
    {
        // MCP008 and MCP009 say what is wrong at each entry and action, so MCP006 only points at them.
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Prodcuts"", ""ComputedVerbsController""] }";

        var diagnostics = GetGeneratorDiagnostics(Usings + ProductsController + UnroutableController, config);
        var diagnostic = Assert.Single(diagnostics, x => x.Id == NoToolsGeneratedId);

        Assert.Equal(
            "No MCP tools are generated: SelectedOnly mode selects only HTTP actions whose route the generator cannot build. " +
            "No include entry in generator.json selects an action that becomes a tool: 'Prodcuts' names no controller or action of the project, as MCP008 reports, " +
            "and 'ComputedVerbsController' selects only actions whose route the generator cannot build, as MCP009 reports. " +
            "List controllers such as 'ProductsController' or actions such as 'ProductsController.GetById' in it, or mark actions with [McpExpose].",
            diagnostic.GetMessage());
        Assert.Single(diagnostics, x => x.Id == UnmatchedConfigEntryId);
        Assert.Single(diagnostics, x => x.Id == UnbuildableRouteId);
    }

    [Fact]
    public void Generator_WarnsThatNoToolsAreGenerated_WhenTheExcludeListLeavesOnlyActionsWithoutARoute()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [""Products""] }";

        var diagnostics = GetGeneratorDiagnostics(Usings + ProductsController + UnroutableController, config);
        var diagnostic = Assert.Single(diagnostics, x => x.Id == NoToolsGeneratedId);

        Assert.Equal(
            "No MCP tools are generated: AllExceptExcluded mode selects only HTTP actions whose route the generator cannot build. " +
            "MCP009 says at each of them why. Remove entries from the exclude list in generator.json, or mark actions with [McpExpose].",
            diagnostic.GetMessage());
        Assert.Single(diagnostics, x => x.Id == UnbuildableRouteId);
    }

    [Fact]
    public void Generator_DoesNotWarnThatNoToolsAreGenerated_WhenAToolIsGeneratedBesideAnActionWithoutARoute()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Products"", ""ComputedVerbsController""] }";

        var diagnostics = GetGeneratorDiagnostics(Usings + ProductsController + UnroutableController, config);

        Assert.DoesNotContain(diagnostics, x => x.Id == NoToolsGeneratedId);
        Assert.Single(diagnostics, x => x.Id == UnbuildableRouteId);
    }

    [Theory]
    [InlineData("{lang=en}/items", "lang")]
    [InlineData("{lang?}/items", "lang")]
    [InlineData("z/{id?}/{part}", "id")]
    public void Generator_SkipsAnActionWithAnOptionalRouteValueNothingFills_BeforeTheEndOfTheRoute(string template, string unfilledName)
    {
        // An optional route value can only be left out at the end of the URL.
        var source = SimpleTypeSources.Usings + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet(""" + template + @""")]
        public IActionResult Z(string? part) => Ok();
    }
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);

        Assert.Contains($": no parameter or property fills the route value '{{{unfilledName}}}' of GET /api/orders/", warning.GetMessage());
        Assert.DoesNotContain("OrdersController_ZTool", RunGenerator(source, AllExceptExcludedConfig));
    }

    [Theory]
    [InlineData("z/{id?}", "/api/orders/z")]
    [InlineData("z/{id=5}", "/api/orders/z")]
    [InlineData("z/{id:int=5}", "/api/orders/z")]
    [InlineData("z/{id:int?}", "/api/orders/z")]
    [InlineData("z/{*rest}", "/api/orders/z")]
    [InlineData("z/{**rest}", "/api/orders/z")]
    [InlineData("z/{a?}/{b?}", "/api/orders/z")]
    [InlineData("/{id?}", "/")]
    public void Generator_LeavesOutAnOptionalRouteValueNothingFills_AtTheEndOfTheRoute(string template, string expectedRoute)
    {
        // MVC serves the action without the value, so the tool calls the URL that leaves it out.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"{template}\")] public Task<string> Find() => Task.FromResult(string.Empty);");

        Assert.Equal($"var route = $\"{expectedRoute}\";", GetRouteLine(tool, SimpleTypeToolClassName));
    }

    [Theory]
    [InlineData("files/{*path}")]
    [InlineData("files/{**path}")]
    public void Generator_KeepsTheSlashesOfACatchAllRouteValue(string template)
    {
        // A catch-all value spans segments, which MVC reads back from the path with its slashes.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"{template}\")] public Task<string> Find(string path) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routepath = System.Uri.EscapeDataString({Formatter}.Format(path)).Replace(\"%2F\", \"/\");", tool);
        Assert.Equal("var route = $\"/api/orders/files/{routepath}\";", GetRouteLine(tool, SimpleTypeToolClassName));
    }

    [Theory]
    [InlineData("page/{page:int=1}", "int page", "/api/orders/page/{routepage}")]
    [InlineData("{id:regex(^\\\\d{{3}}$)}", "int id", "/api/orders/{routeid}")]
    [InlineData("say\\\"hi", "", "/api/orders/say\\\"hi")]
    [InlineData("back\\\\slash", "", "/api/orders/back\\\\slash")]
    [InlineData("{{literal}}", "", "/api/orders/{{literal}}")]
    public void Generator_WritesTheRouteTemplate_AsAStringThatCompiles(string template, string parameter, string expectedRoute)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"{template}\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Equal($"var route = $\"{expectedRoute}\";", GetRouteLine(tool, SimpleTypeToolClassName));
    }
}
