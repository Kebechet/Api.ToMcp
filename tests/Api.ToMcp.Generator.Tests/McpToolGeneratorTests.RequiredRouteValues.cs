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

    [Theory]
    [InlineData("[HttpGet(\"{id}\")] [HttpGet]")]
    [InlineData("[HttpGet(\"{id}\")] [HttpGet(\"\")]")]
    [InlineData("[HttpGet(\"multi/{id:int}\")] [HttpGet(\"MULTI\")]")]
    public void Generator_KeepsARouteValueOptional_ThatAnotherRouteOfTheActionLeavesOut(string httpAttributes)
    {
        // The URL without the value reaches the same action, at its other route.
        var tool = GenerateCompilingSimpleTypeTool($"{httpAttributes} public Task<string> Find(int? id) => Task.FromResult(string.Empty);");

        Assert.Contains(" int? id = null,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: false))", tool);
    }

    [Theory]
    [InlineData("[HttpGet(\"{id}\")] [HttpGet(\"other\")]", "int? id")]
    [InlineData("[HttpGet(\"{id}\")] [HttpPost]", "int? id")]
    [InlineData("[HttpGet(\"x/{id}/y\")] [HttpGet(\"x/y\")]", "int? id")]
    [InlineData("[HttpGet(\"{tenant}/x/{id}\")] [HttpGet(\"{other}/x\")]", "string? tenant, string? other, int? id")]
    public void Generator_KeepsARouteValueRequired_ThatNoOtherRouteOfTheActionLeavesOutAtTheEnd(string httpAttributes, string parameters)
    {
        // Of x/{id}/y the tool would send x//y, which is not x/y; a route value of another name binds another parameter.
        var tool = GenerateCompilingSimpleTypeTool($"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.Contains(" int? id,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: true))", tool);
    }

    [Theory]
    [InlineData("[HttpDelete(\"opt\")]")]
    [InlineData("[HttpDelete(\"OPT\")]")]
    [InlineData("[HttpDelete(\"/api/orders/opt\")]")]
    [InlineData("[HttpDelete(\"opt/{page?}\")]")]
    [InlineData("[HttpDelete(\"opt\")] [Api.ToMcp.Abstractions.McpIgnore]")]
    [InlineData("[HttpGet(\"/elsewhere\")] [HttpDelete(\"opt\")]")]
    [InlineData("[AcceptVerbs(\"DELETE\", Route = \"opt\")]")]
    public void Generator_RequiresAnOptionalRouteValue_WhenAnotherActionMayBeServedWithoutIt(string siblingAttributes)
    {
        // DELETE api/orders/opt/ is the other action's URL, which MVC serves ahead of opt/{id?}.
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpDelete(\"opt/{id?}\")] public Task<string> Find(Guid? id) => Task.FromResult(string.Empty); " +
            $"{siblingAttributes} public Task<string> All() => Task.FromResult(string.Empty);");

        Assert.Contains(" System.Guid? id,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: true))", tool);
        Assert.Contains("which cannot be empty, '.' or '..'.", tool);
    }

    [Theory]
    [InlineData("[HttpGet(\"opt\")]")]
    [InlineData("[HttpDelete(\"opt/{page}\")]")]
    [InlineData("[HttpDelete(\"other\")]")]
    [InlineData("[HttpDelete(\"{slug}\")]")]
    [InlineData("[HttpDelete(\"{slug?}\")]")]
    [InlineData("[HttpDelete(\"{**rest}\")]")]
    [InlineData("[HttpDelete(\"/opt\")]")]
    public void Generator_KeepsAnOptionalRouteValueOptional_WhenNoOtherActionIsServedWithoutIt(string siblingAttributes)
    {
        // A route value or catch-all where the URL has the literal opt comes after it, as MVC orders them.
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpDelete(\"opt/{id?}\")] public Task<string> Find(Guid? id) => Task.FromResult(string.Empty); " +
            $"{siblingAttributes} public Task<string> All() => Task.FromResult(string.Empty);");

        Assert.Contains(" System.Guid? id = null,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: false))", tool);
    }

    [Theory]
    [InlineData("[HttpDelete(\"{kind}/{id?}\")]", "string kind, Guid? id", "[HttpDelete(\"books\")]")]
    [InlineData("[HttpDelete(\"{kind}/{id?}\")]", "string kind, Guid? id", "[HttpDelete(\"{other}\")]")]
    [InlineData("[HttpDelete(\"{kind}/x/{id?}\")]", "string kind, Guid? id", "[HttpDelete(\"{other:int}/x\")]")]
    [InlineData("[HttpDelete(\"opt/{id=5}\")]", "int? id", "[HttpDelete(\"opt\")]")]
    public void Generator_RequiresAnOptionalRouteValue_WhenTheStructureCannotTellWhichActionIsServed(string httpAttribute, string parameters, string siblingAttributes)
    {
        // A literal of the other route where the URL has a route value matches the value equal to it, ahead of it; and
        // after a route value in both, their constraints, which are not compared, may tell the order.
        var tool = GenerateCompilingSimpleTypeTool(
            $"{httpAttribute} public Task<string> Find({parameters}) => Task.FromResult(string.Empty); " +
            $"{siblingAttributes} public Task<string> All() => Task.FromResult(string.Empty);");

        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: true))", tool);
    }

    [Fact]
    public void Generator_RequiresACatchAllValue_WhenAnotherActionIsServedWithoutIt()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpDelete(\"files/{**path}\")] public Task<string> Find(string? path) => Task.FromResult(string.Empty); " +
            "[HttpDelete(\"files\")] public Task<string> All() => Task.FromResult(string.Empty);");

        Assert.Contains(" string? path,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routepath, isRequired: true))", tool);
        Assert.Contains("which cannot be empty or have a '.' or '..' segment.", tool);
    }

    [Fact]
    public void Generator_SendsARequiredOptionalRouteValue_EvenWhenItEqualsTheActionsDefault()
    {
        // The tool leaves an optional route value out when it equals the action's default, which the URL now needs.
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpDelete(\"opt/{status?}\")] public Task<string> Find(OrderStatus status = OrderStatus.Closed) => Task.FromResult(string.Empty); " +
            "[HttpDelete(\"opt\")] public Task<string> All() => Task.FromResult(string.Empty);");

        Assert.Contains($"var routestatus = System.Uri.EscapeDataString({Formatter}.Format(status));", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routestatus, isRequired: true))", tool);
    }

    [Fact]
    public void Generator_SkipsAnActionWhoseOptionalRouteValueNothingFills_WhenAnotherActionIsServedWithoutIt()
    {
        // The URL would always be the other action's, as nothing gives the route value.
        const string source = SimpleTypeSources.Usings + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpDelete(""opt/{id?}"")]
        public IActionResult Find() => Ok(RouteData.Values[""id""]);

        [HttpDelete(""opt"")]
        public IActionResult All() => Ok();
    }
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);

        Assert.Equal(
            "No MCP tool is generated for action 'OrdersController.Find': no parameter or property fills the route value '{id}' of DELETE /api/orders/opt/{id?}, " +
            "which the URL needs, as another action may be served at the URL without it",
            warning.GetMessage());
        Assert.DoesNotContain("OrdersController_FindTool", RunGenerator(source, AllExceptExcludedConfig));
    }

    [Theory]
    [InlineData("app.MapDelete(\"/opt\", [McpIgnore] () => 0);")]
    [InlineData("app.MapMethods(\"/opt\", new[] { \"GET\", \"DELETE\" }, () => 0);")]
    [InlineData("app.MapGroup(\"/OPT\").MapDelete(\"\", () => 0);")]
    public void Endpoint_RequiresAnOptionalRouteValue_WhenAnotherEndpointIsServedWithoutIt(string sibling)
    {
        var source = MinimalApi("app.MapDelete(\"/opt/{id?}\", (Guid? id) => id); " + sibling);

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_DeleteOptByIdTool");

        Assert.Contains(" System.Guid? id,", tool);
        Assert.Contains($"if ({Formatter}.ChangesThePath(routeid, isRequired: true))", tool);
    }

    [Fact]
    public void Endpoint_KeepsAnOptionalRouteValueOptional_WhenTheOtherEndpointHasAnotherHttpMethod()
    {
        var source = MinimalApi("app.MapDelete(\"/opt/{id?}\", (Guid? id) => id); app.MapGet(\"/opt\", () => 0);");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_DeleteOptByIdTool");

        Assert.Contains(" System.Guid? id = null,", tool);
    }

    [Fact]
    public void Generator_ComparesTheRoutesOfControllersAndEndpoints_WithEachOther()
    {
        const string controllers = @"
namespace TestApi
{
    [ApiController]
    [Route(""opt"")]
    public class OptController : ControllerBase
    {
        [HttpDelete(""{id?}"")]
        public string Find(Guid? id) => """";
    }

    [ApiController]
    public class OtherController : ControllerBase
    {
        [HttpDelete(""/other"")]
        [McpIgnore]
        public string All() => """";
    }
}
";
        var source = MinimalApi("app.MapDelete(\"/opt\", [McpIgnore] () => 0); app.MapDelete(\"/other/{id?}\", (Guid? id) => id);") + controllers;

        var controllerTool = GenerateCompilingToolClass(source, MinimalApisConfig, "OptController_FindTool");
        var endpointTool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_DeleteOtherByIdTool");

        Assert.Contains(" System.Guid? id,", controllerTool);
        Assert.Contains(" System.Guid? id,", endpointTool);
    }
}
