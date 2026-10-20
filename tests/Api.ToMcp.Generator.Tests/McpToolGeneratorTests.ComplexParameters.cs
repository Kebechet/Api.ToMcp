using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Route values: MVC matches a route value to a parameter or property ignoring case, and a placeholder no
/// parameter names is filled from a property of a <c>[FromRoute]</c> or <c>[FromBody]</c> parameter.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string Formatter = "global::Api.ToMcp.Runtime.Services.McpRequestValueFormatter";

    [Theory]
    [InlineData("HttpGet")]
    [InlineData("HttpPost")]
    public void Generator_SendsAParameterTheRouteNamesInAnotherCase_OnlyAsTheRouteValue(string httpAttribute)
    {
        // MVC matches route values ignoring case, so {Id} binds the parameter id.
        var tool = GenerateCompilingSimpleTypeTool($"[{httpAttribute}(\"{{Id}}\")] public Task<string> Find(int id) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeid = System.Uri.EscapeDataString({Formatter}.Format(id));", tool);
        Assert.DoesNotContain("queryParts", tool);
    }

    [Theory]
    [InlineData("[FromBody]")]
    [InlineData("[FromRoute]")]
    public void Generator_FillsARoutePlaceholderFromAPropertyOfASimpleTypeBoundByAnAttribute(string bindingAttribute)
    {
        // Left unfilled, {value} names no variable in the emitted string and the tool does not compile.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpPost(\"{{value}}\")] public Task<string> Find({bindingAttribute} OrderId id) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeid_Value = System.Uri.EscapeDataString({Formatter}.Format(id.Value));", tool);
    }
}
