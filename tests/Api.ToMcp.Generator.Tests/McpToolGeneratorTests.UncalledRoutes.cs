using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// An action MVC serves at several routes, of which its tool calls one, as 2.4.0's did. See
/// https://github.com/Kebechet/Api.ToMcp/issues/61.
/// </summary>
public partial class McpToolGeneratorTests
{
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
