using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// <c>[McpRequiredScope]</c> tells the runtime, before a tool runs, the scope the tool passes to <c>BeforeInvokeAsync</c>.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string RequiredScopeMarker = "[global::Api.ToMcp.Runtime.McpRequiredScope(";

    [Theory]
    [InlineData("[HttpGet(\"items\")]", "", "McpScope.Read")]
    [InlineData("[HttpPost(\"items\")]", "", "McpScope.Write")]
    [InlineData("[HttpDelete(\"items\")]", "", "McpScope.Delete")]
    [InlineData("[HttpPost(\"items\")]", "[McpExpose(Scope = McpScope.Read)]", "McpScope.Read")]
    [InlineData("[HttpPost(\"items\")]", "[McpExpose(Scope = McpScope.Read | McpScope.Write)]", "McpScope.Read | McpScope.Write")]
    [InlineData("[HttpPost(\"items\")]", "[McpExpose(Scope = (McpScope)8)]", "(McpScope)8")]
    public void Generator_NamesTheScopeAToolChecks_InTheRequiredScopeAttribute(string httpAttribute, string exposeAttribute, string expectedScopeExpression)
    {
        var controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        " + exposeAttribute + @"
        " + httpAttribute + @"
        public Task<string> Run(string name) => Task.FromResult(string.Empty);
    }
}
";
        var errors = CompileWithGeneratedTools(Usings + controller, AllExceptExcludedConfig);
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
        Assert.Equal($"{RequiredScopeMarker}{expectedScopeExpression})]", GetToolLine(generated, "ReportsController_RunTool", RequiredScopeMarker));
        Assert.Equal($"await invoker.BeforeInvokeAsync({expectedScopeExpression}, cancellationToken);", GetScopeCheck(generated, "ReportsController_RunTool"));
    }

    [Fact]
    public void Generator_NamesTheRequiredScope_OnAToolWithAnOutputSchema()
    {
        var tool = GenerateCompilingToolClass(StructuredOutputSource("public ActionResult<Product> Get() => new Product();"), AllExceptExcludedConfig);

        Assert.Contains($"{RequiredScopeMarker}McpScope.Read)]", tool);
    }

    [Fact]
    public void Generator_QualifiesTheRequiredScope_WhenAParameterHidesMcpScope()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpPut(\"clash/{id}\")] public Task<string> Find(int id, string McpScope) => Task.FromResult(string.Empty);");

        Assert.Contains($"{RequiredScopeMarker}global::Api.ToMcp.Abstractions.Scopes.McpScope.Write)]", tool);
    }
}
