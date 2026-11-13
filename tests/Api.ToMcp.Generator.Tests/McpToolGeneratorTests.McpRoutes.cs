using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// No action at the MCP endpoint's default route, <c>/mcp</c>, or under it gets a tool, compared by whole path segments
/// in any letter case. An action whose route only holds the letters, such as <c>api/mcp-status</c> or <c>api/mcp</c>, gets
/// its tool: the MCP endpoint refuses a tool's request wherever it is mapped. See
/// https://github.com/Kebechet/Api.ToMcp/issues/53.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Theory]
    [InlineData("[controller]", "")]
    [InlineData("mcp", "")]
    [InlineData("MCP", "items")]
    [InlineData("Mcp/", "items")]
    [InlineData("api/[controller]", "/mcp/items")]
    [InlineData("api/[controller]", "~/Mcp/items")]
    [InlineData("api/[controller]", "/mcp/")]
    [InlineData("api/[controller]", "/mcp/{id}")]
    [InlineData("/", "mcp/status")]
    [InlineData("~/", "mcp/status")]
    [InlineData("~/mcp", "status")]
    [InlineData("~/Mcp", "")]
    [InlineData("/mcp/", "status")]
    public void Action_AtTheMcpRouteOrUnderIt_GetsNoToolAndNoWarning_EvenWithMcpExpose(string controllerRoute, string actionRoute)
    {
        var generated = RunGenerator(Usings + McpRouteController(controllerRoute, actionRoute), AllExceptExcludedConfig);

        Assert.DoesNotContain("[McpServerTool(", generated);
        Assert.Empty(GetGeneratorDiagnostics(Usings + McpRouteController(controllerRoute, actionRoute), AllExceptExcludedConfig));
    }

    [Theory]
    [InlineData("api/[controller]", "/api/mcp", "/api/mcp")]
    [InlineData("api/[controller]", "/api/MCP/items", "/api/MCP/items")]
    [InlineData("api/[controller]", "/api/mcpservers", "/api/mcpservers")]
    [InlineData("api/[controller]", "/api/mcp-settings", "/api/mcp-settings")]
    [InlineData("api/[controller]", "/api/settings/mcp-status", "/api/settings/mcp-status")]
    [InlineData("api/[controller]", "/v1/mcpartners/{id}", "/v1/mcpartners/{routeid}")]
    [InlineData("api/[controller]", "/mcpservers", "/mcpservers")]
    [InlineData("api/[controller]", "/mcp-status", "/mcp-status")]
    [InlineData("api/[controller]", "/mcp.json", "/mcp.json")]
    [InlineData("v1/[controller]", "", "/v1/mcp")]
    public void Action_WhoseRouteOnlyHoldsMcp_GetsItsTool(string controllerRoute, string actionRoute, string route)
    {
        var generated = RunGenerator(Usings + McpRouteController(controllerRoute, actionRoute), AllExceptExcludedConfig);

        Assert.Contains("[McpServerTool(", generated);
        Assert.Contains($"var route = $\"{route}", generated);
    }

    [Theory]
    [InlineData("/", "probe-root", "/probe-root")]
    [InlineData("~/", "probe-root", "/probe-root")]
    [InlineData("/", "", "/\"")]
    [InlineData("~/api", "items", "/api/items")]
    [InlineData("~/api", "", "/api\"")]
    [InlineData("/api/", "items", "/api/items")]
    [InlineData("api/", "/items", "/items")]
    public void Action_UnderARootOrAppRelativeControllerRoute_IsCalledWhereMvcServesIt(string controllerRoute, string actionRoute, string route)
    {
        // MVC combines the templates as AttributeRouteModel.CombineTemplates does: a prefix of "/" or "~/" counts as
        // none, and one leading "/" or "~/" is cleaned from the result. "//probe-root" and "/~/api/items" answer 404.
        var generated = RunGenerator(Usings + McpRouteController(controllerRoute, actionRoute), AllExceptExcludedConfig);

        Assert.Contains($"var route = $\"{route}", generated);
    }

    /// <summary>
    /// <c>McpController</c>, whose <c>[controller]</c> token is <c>mcp</c>, with one <c>[McpExpose]</c> action.
    /// </summary>
    private static string McpRouteController(string controllerRoute, string actionRoute)
    {
        return @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""" + controllerRoute + @""")]
    public class McpController : ControllerBase
    {
        [McpExpose]
        [HttpGet(""" + actionRoute + @""")]
        public Task<int> Get(int id) => Task.FromResult(id);
    }
}
";
    }
}
