using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// An action MVC serves at several routes, of which its tool calls one, as 2.4.0's did. MCP014 reports another route at
/// which MVC binds a value of the action that the route the tool calls cannot carry. See
/// https://github.com/Kebechet/Api.ToMcp/issues/61.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string UncalledRouteId = "MCP014";

    [Theory]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, string? slug", "GET /api/orders/{id}", "GET /api/orders/{id}/{slug}", "'slug'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-slug/{slug}\")]", "int? id, string? slug", "GET /api/orders/by-id/{id}", "GET /api/orders/by-slug/{slug}", "'slug'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"all\")] [HttpGet(\"one/{id?}\")]", "int? id", "GET /api/orders/all", "GET /api/orders/one/{id?}", "'id'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{a}\")] [HttpGet(\"x/{b}/{c}\")]", "string a, string? b, string? c", "GET /api/orders/{a}", "GET /api/orders/x/{b}/{c}", "'b' and 'c'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-code/{productCode}\")]", "int? id, [FromRoute(Name = \"productCode\")] string? code", "GET /api/orders/by-id/{id}", "GET /api/orders/by-code/{productCode}", "'code'")]
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromRoute] string? slug", "GET /api/orders/{id}", "GET /api/orders/{id}/{slug}", "'slug'")]
    public void Generator_ReportsARouteTheToolDoesNotCall_WhereMvcBindsAValueFromThatRouteAlone(
        string controllerDeclaration, string httpAttributes, string parameters, string calledRoute, string uncalledRoute, string lostValues)
    {
        // [ApiController] binds a simple parameter that any route of the action names from the route alone, as
        // [FromRoute] does in any controller, and the route the tool calls has no such value.
        var source = UncalledRoutesSource(controllerDeclaration, $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);

        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Equal(
            $"The MCP tool of action 'OrdersController.Find' calls it at {calledRoute}, not at {uncalledRoute}, where MVC binds {lostValues}, " +
            "which that route alone carries. Give that route an action of its own for a tool to call it.",
            info.GetMessage());
        Assert.Equal("Find", info.Location.SourceTree!.GetText().ToString(info.Location.SourceSpan));
    }

    [Fact]
    public void Generator_ReportsTheRouteOfAnotherControllerRoute_WhereMvcBindsAValueFromThatRouteAlone()
    {
        // MVC serves the action at each [Route] of its controller; the tool calls it at the first.
        var source = UncalledRoutesSource(
            "[ApiController] [Route(\"api/orders\")] [Route(\"api/tenants/{tenantId}/orders\")] public class OrdersController : ControllerBase",
            "[HttpGet(\"{id}\")] public Task<string> Find(string? tenantId, int id) => Task.FromResult(string.Empty);");

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);

        Assert.Equal(
            "The MCP tool of action 'OrdersController.Find' calls it at GET /api/orders/{id}, not at GET /api/tenants/{tenantId}/orders/{id}, where MVC binds 'tenantId', " +
            "which that route alone carries. Give that route an action of its own for a tool to call it.",
            info.GetMessage());
    }

    [Fact]
    public void Generator_ReportsAPropertyOfAFromRouteParameter_ThatOnlyARouteTheToolDoesNotCallNames()
    {
        const string types = @"
    public sealed class DocKey
    {
        public int Id { get; set; }

        public int Version { get; set; }
    }
";
        var source = UncalledRoutesSource(
            ApiControllerDeclaration,
            "[HttpGet(\"{id}\")] [HttpGet(\"{id}/v/{version:min(1)}\")] public Task<string> Find([FromRoute] DocKey key) => Task.FromResult(string.Empty);",
            types);

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);

        Assert.Equal(
            "The MCP tool of action 'OrdersController.Find' calls it at GET /api/orders/{id}, not at GET /api/orders/{id}/v/{version:min(1)}, where MVC binds the property Version of 'key', " +
            "which that route alone carries. Give that route an action of its own for a tool to call it.",
            info.GetMessage());
    }

    [Theory]
    // In a controller without [ApiController], MVC binds slug from the query string, where the tool sends it.
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, string? slug")]
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-slug/{slug}\")]", "int id = 5, string slug = \"x\"")]
    // A binding attribute of another source makes MVC bind the value from there.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromQuery] string? slug")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromQuery(Name = \"slug\")] string? s")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromHeader(Name = \"slug\")] string? slug")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromServices] System.IServiceProvider slug")]
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{note}\")]", "int id, [FromBody] Shipment note")]
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{note}\")]", "int id, [FromForm] string? note")]
    // [ApiController] reads a complex parameter without a binding attribute from the body, whatever the routes name.
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{shipment}\")]", "int id, Shipment shipment")]
    // The route the tool calls carries the value as well.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}/{slug}\")] [HttpGet(\"{id}\")]", "int id, string? slug")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"x/{ID}\")]", "int id")]
    // No parameter takes {culture}, which middleware such as a culture provider reads.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{culture}/{id}\")]", "int id")]
    // The other route takes no value; MVC serves the other one for POST, which the tool, sending GET, never calls.
    [InlineData(ApiControllerDeclaration, "[HttpGet] [HttpGet(\"{id?}\")]", "int? id")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"a/{x}\")] [HttpPost(\"b/{y}\")]", "string? x, string? y")]
    public void Generator_ReportsNothing_WhereNoValueOfTheActionIsLost(string controllerDeclaration, string httpAttributes, string parameters)
    {
        var source = UncalledRoutesSource(controllerDeclaration, $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Theory]
    // MVC reads the [FromQuery] Version from the query string, where the tool sends it.
    [InlineData("[HttpGet(\"{id}\")] [HttpGet(\"{id}/v/{version}\")]", "[FromRoute] VersionedKey key")]
    // [ApiController] infers no source for a type with a property that names one, so MVC reads the parameter from the
    // query string as well.
    [InlineData("[HttpGet(\"{id}\")] [HttpGet(\"{id}/{key}\")]", "int id, VersionedKey key")]
    public void Generator_ReportsNothing_ForAComplexValue_ThatMvcReadsFromTheQueryString(string httpAttributes, string parameters)
    {
        const string types = @"
    public sealed class VersionedKey
    {
        public int Id { get; set; }

        [FromQuery]
        public int Version { get; set; }
    }
";
        var source = UncalledRoutesSource(
            ApiControllerDeclaration,
            $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);",
            types);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Fact]
    public void Generator_ReportsNothing_ForAControllerRouteWhoseValueTheActionReadsFromTheQueryString()
    {
        // The culture provider reads ?culture= too, and so does the action.
        var source = UncalledRoutesSource(
            "[ApiController] [Route(\"api/orders\")] [Route(\"{culture}/api/orders\")] public class OrdersController : ControllerBase",
            "[HttpGet(\"{id}\")] public Task<string> Find(int id, [FromQuery] string? culture) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Fact]
    public void Generator_EmitsTheToolOfAnActionWithSeveralRoutes_AsMainDoes()
    {
        // The tool calls the first route, as 2.4.0's did: the expected text is what main 0d09708 generates.
        var source = UncalledRoutesSource(
            ApiControllerDeclaration,
            "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-slug/{slug}\")] public Task<string> Find(int? id, string? slug, [FromQuery] int page = 1) => Task.FromResult(string.Empty); " +
            "[HttpPost(\"{id}/reviews\")] [HttpPost(\"by-slug/{slug}/reviews\")] public Task<string> Review(int id, string? slug, [FromForm] Shipment review, [FromHeader(Name = \"Idempotency-Key\")] string? key) => Task.FromResult(string.Empty);");

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        AssertMatchesSnapshot("ToolOfAnActionWithSeveralRoutes.txt", GetToolClass(generated, SimpleTypeToolClassName));
        AssertMatchesSnapshot("FormToolOfAnActionWithSeveralRoutes.txt", GetToolClass(generated, "OrdersController_ReviewTool"));
    }

    /// <param name="types">Types declared beside the controller.</param>
    private static string UncalledRoutesSource(string controllerDeclaration, string actions, string types = "")
    {
        return SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
{types}
    {controllerDeclaration}
    {{
        {actions}
    }}
}}
";
    }
}
