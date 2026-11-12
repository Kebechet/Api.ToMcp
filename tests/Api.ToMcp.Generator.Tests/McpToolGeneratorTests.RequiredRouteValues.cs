using System;
using System.Linq;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// A route value the template requires has to be in the URL. Without it the tool requests another URL, which MVC can
/// serve with another action of the same HTTP method: <c>DELETE api/orders/</c> instead of <c>DELETE api/orders/{id}</c>.
/// See https://github.com/Kebechet/Api.ToMcp/issues/50.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Theory]
    [InlineData("[HttpGet(\"d/{id}/x\")]", "int? id", "int? id")]
    [InlineData("[HttpGet(\"{id}\")]", "Guid? id", "System.Guid? id")]
    [InlineData("[HttpDelete(\"{id}\")]", "Guid? id", "System.Guid? id")]
    [InlineData("[HttpGet(\"{id:int}\")]", "int? id = null", "int? id")]
    [InlineData("[HttpGet(\"{id}\")]", "int id = 5", "int? id")]
    [InlineData("[HttpGet(\"{name}\")]", "string? name", "string? name")]
    [InlineData("[HttpGet(\"{name}\")]", "string name = \"all\"", "string name")]
    [InlineData("[HttpGet(\"{name}.{ext?}\")]", "string? name, string? ext = null", "string? name")]
    [InlineData("[HttpGet(\"{id}\")]", "AttributedQuery? q", "TestApi.AttributedQuery? q")]
    public void Generator_DeclaresTheParameterThatFillsARequiredRouteValue_WithoutADefault(string httpAttribute, string parameters, string expectedDeclaration)
    {
        // Without a default the MCP SDK lists the parameter in the input schema's required, whatever its type.
        var tool = GenerateCompilingSimpleTypeTool($"{httpAttribute} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.Contains($" {expectedDeclaration},", tool);
    }

    [Theory]
    [InlineData("[HttpGet(\"{id?}\")]", "int? id", "int? id = null")]
    [InlineData("[HttpGet(\"{id:int?}\")]", "int? id", "int? id = null")]
    [InlineData("[HttpGet(\"{id=5}\")]", "int? id", "int? id = null")]
    [InlineData("[HttpGet(\"{*path}\")]", "string? path", "string? path = null")]
    [InlineData("[HttpGet(\"{**path}\")]", "string? path", "string? path = null")]
    [InlineData("[HttpGet]", "int? count", "int? count = null")]
    [InlineData("[HttpGet(\"d/{id}/x\")]", "int id, int? count", "int? count = null")]
    [InlineData("[HttpGet(\"{name}.{ext?}\")]", "string name, string? ext = null", "string? ext = null")]
    public void Generator_KeepsAnOptionalRouteValueACatchAllAndAQueryValue_Optional(string httpAttribute, string parameters, string expectedDeclaration)
    {
        var tool = GenerateCompilingSimpleTypeTool($"{httpAttribute} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.Contains($" {expectedDeclaration},", tool);
    }

    [Fact]
    public void Generator_DeclaresTheParameterThatFillsARequiredRouteValue_BeforeTheOptionalOnes()
    {
        // C# wants every optional parameter last (CS1737), and the action declares the route value after one.
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"d/{id}/x\")] public Task<string> Find(string? filter, int? id) => Task.FromResult(string.Empty);");

        var declaredNames = tool
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("[Description(\"Parameter: ", StringComparison.Ordinal))
            .Select(x => x.Split('"')[1].Substring("Parameter: ".Length));

        Assert.Equal("id, filter", string.Join(", ", declaredNames));
    }

    [Fact]
    public void Generator_RefusesARequiredRouteValueThatChangesThePath_BeforeTheToolCallsTheApi()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpDelete(\"{id}\")] public Task<string> Find(Guid? id) => Task.FromResult(string.Empty);");

        var lines = tool.Split('\n').Select(x => x.Trim()).ToList();
        var routeVariable = lines.FindIndex(x => x.StartsWith("var routeid = ", StringComparison.Ordinal));
        Assert.Equal($"if ({Formatter}.ChangesThePath(routeid, isRequired: true))", lines[routeVariable + 1]);
        Assert.Equal(
            "return new CallToolResult { IsError = true, Content = { new TextContentBlock { Text = \"The argument 'id' fills the route value {id} of the API's URL, which cannot be empty, '.' or '..'.\" } } };",
            lines[routeVariable + 2]);
        Assert.True(routeVariable + 2 < lines.FindIndex(x => x.Contains("invoker.DeleteAsync(")), "The tool checks the route value only after it calls the API.");
    }

    [Fact]
    public void Generator_NamesTheParameterWhosePropertyFillsARequiredRouteValue_AsTheInputSchemaDoes()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"{id}\")] public Task<string> Find(AttributedQuery? q) => Task.FromResult(string.Empty);");

        Assert.Contains($"if ({Formatter}.ChangesThePath(routeq_Id, isRequired: true))", tool);
        Assert.Contains("Text = \"The argument 'q' has a property that fills the route value {id} of the API's URL, which cannot be empty, '.' or '..'.\"", tool);
    }

    [Theory]
    [InlineData("[HttpGet(\"{id?}\")]", "int? id", "routeid", "which cannot be '.' or '..'.")]
    [InlineData("[HttpGet(\"{id=5}\")]", "int? id", "routeid", "which cannot be '.' or '..'.")]
    [InlineData("[HttpGet(\"{name}.{ext?}\")]", "string name, string? ext = null", "routeext", "which cannot be '.' or '..'.")]
    [InlineData("[HttpGet(\"{*path}\")]", "string? path", "routepath", "which cannot have a '.' or '..' segment.")]
    [InlineData("[HttpGet(\"{**path}\")]", "string? path", "routepath", "which cannot have a '.' or '..' segment.")]
    public void Generator_LetsAnOptionalRouteValueBeEmpty_ButNotChangeThePath(string httpAttribute, string parameters, string routeVariable, string expectedRefusal)
    {
        // A dot value is no segment at all: System.Uri removes it, and with .. the segment before it, from the URL.
        var tool = GenerateCompilingSimpleTypeTool($"{httpAttribute} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.Contains($"if ({Formatter}.ChangesThePath({routeVariable}, isRequired: false))", tool);
        Assert.Contains(expectedRefusal + "\" } } };", tool);
    }

    [Fact]
    public void Generator_ChecksNoQueryValue()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet] public Task<string> Find(int? count, string? name) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain("IsError", tool);
    }

    [Fact]
    public void Endpoint_DeclaresTheParameterThatFillsARequiredRouteValue_WithoutADefault_AndRefusesItEmpty()
    {
        // Without its id, the call would send DELETE /items, which the endpoint mapped next to it serves.
        var source = MinimalApi(@"
            app.MapDelete(""/items/{id}"", (Guid? id) => id);
            app.MapDelete(""/items"", () => 0);");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_DeleteItemsByIdTool");

        Assert.Contains(" System.Guid? id,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: true))", tool);
        Assert.Contains("Text = \"The argument 'id' fills the route value {id} of the API's URL, which cannot be empty, '.' or '..'.\"", tool);
    }

    [Fact]
    public void Endpoint_KeepsAnOptionalRouteValue_Optional()
    {
        var source = MinimalApi(@"app.MapGet(""/items/{id?}"", (int? id) => id);");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_GetItemsByIdTool");

        Assert.Contains(" int? id = null,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: false))", tool);
    }
}
