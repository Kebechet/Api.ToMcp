using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Route values: MVC matches a route value to a parameter or property ignoring case.
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
}
