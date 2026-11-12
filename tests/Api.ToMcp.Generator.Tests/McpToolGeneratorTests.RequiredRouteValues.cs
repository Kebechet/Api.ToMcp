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
}
