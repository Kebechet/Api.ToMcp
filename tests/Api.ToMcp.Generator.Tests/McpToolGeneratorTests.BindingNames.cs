using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// The name MVC binds a parameter under: the <c>Name</c> of the first attribute implementing <c>IModelNameProvider</c>
/// that gives one, of the parameter's own attributes and then its type's, such as <c>[FromRoute(Name)]</c>,
/// <c>[FromQuery(Name)]</c>, <c>[ModelBinder(Name)]</c> or <c>[Bind(Prefix)]</c>, else the parameter's own name. A tool
/// sends the parameter's value under that name, as the prefix of its properties and as the route value it fills, while
/// its argument keeps the parameter's own name. See https://github.com/Kebechet/Api.ToMcp/issues/51 and
/// https://github.com/Kebechet/Api.ToMcp/issues/52.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Theory]
    [InlineData(ApiControllerDeclaration, "[FromRoute(Name = \"productId\")] int id", "")]
    [InlineData(ApiControllerDeclaration, "[ModelBinder(typeof(PlainCodeBinder), Name = \"productId\")] PlainCode id", "")]
    [InlineData(PlainControllerDeclaration, "[ModelBinder(Name = \"productId\")] int id", "")]
    // [ApiController] infers [FromQuery] for id, as no route value is named id, so MVC reads it from ?productId=.
    [InlineData(ApiControllerDeclaration, "[ModelBinder(Name = \"productId\")] int id", "AddQueryValue(queryParts, \"productId\", id);")]
    public void Generator_FillsTheRouteValue_ThatTheNameMvcBindsTheParameterUnderNames(string controllerDeclaration, string parameter, string expectedQueryValue)
    {
        var action = $"[HttpGet(\"p/{{productId}}\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);";
        var source = SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
    {controllerDeclaration}
    {{
        {action}
    }}
}}
";
        var diagnostics = GetGeneratorDiagnostics(source, AllExceptExcludedConfig);

        var tool = GenerateCompilingSimpleTypeTool(action, controllerDeclaration);

        Assert.DoesNotContain(diagnostics, x => x.Id == UnbuildableRouteId);
        Assert.Contains("var route = $\"/api/orders/p/{routeid}\";", tool);
        Assert.Contains("The argument 'id' fills the route value {productId} of the API's URL", tool);
        if (expectedQueryValue.Length == 0)
        {
            Assert.DoesNotContain("queryParts", tool);
        }
        else
        {
            Assert.Contains(expectedQueryValue, tool);
        }
    }

    [Theory]
    [InlineData(ApiControllerDeclaration, "[FromQuery(Name = \"q\")] int id")]
    [InlineData(PlainControllerDeclaration, "[ModelBinder(Name = \"q\")] int id")]
    public void Generator_FillsARouteValueNamedLikeAParameterMvcBindsUnderAnotherName_AndSendsItUnderThatName(string controllerDeclaration, string parameter)
    {
        // MVC reads id from q, which no route value carries; the URL needs a segment for {id} all the same.
        var tool = GenerateCompilingSimpleTypeTool(
            $"[HttpGet(\"x/{{id}}\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);",
            controllerDeclaration);

        Assert.Contains("var route = $\"/api/orders/x/{routeid}\";", tool);
        Assert.Contains("AddQueryValue(queryParts, \"q\", id);", tool);
    }

    [Theory]
    [InlineData(ApiControllerDeclaration, "[ModelBinder(Name = \"q\")] string? search", "\"q\", search")]
    [InlineData(ApiControllerDeclaration, "[FromQuery, ModelBinder(Name = \"q\")] string? search", "\"q\", search")]
    [InlineData(ApiControllerDeclaration, "[FromQuery, Bind(Prefix = \"f\")] ProductFilter filter", "\"f\", filter")]
    [InlineData(ApiControllerDeclaration, "[FromQuery] PrefixedFilter filter", "\"pf\", filter")]
    [InlineData(PlainControllerDeclaration, "[Bind(Prefix = \"f\")] ProductFilter filter", "\"f\", filter")]
    [InlineData(PlainControllerDeclaration, "PrefixedFilter filter", "\"pf\", filter")]
    public void Generator_SendsAQueryValue_UnderTheNameMvcBindsTheParameterUnder(string controllerDeclaration, string parameter, string expectedArguments)
    {
        var tool = GenerateCompilingSimpleTypeTool(
            $"[HttpGet(\"search\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);",
            controllerDeclaration);

        Assert.Contains($"AddQueryValue(queryParts, {expectedArguments});", tool);
    }

    [Fact]
    public void Generator_KeepsThePrefixABindAttributeGives_ThoughAPropertyNamesTheRouteValue()
    {
        // MVC binds every property under s, s.Id included, so never from {id}: unprefixed, Id and Note would bind nothing.
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpGet(\"shipments/{id}\")] public Task<string> Find([Bind(Prefix = \"s\")] Shipment shipment) => Task.FromResult(string.Empty);",
            PlainControllerDeclaration);

        Assert.Contains($"var routeshipment_Id = System.Uri.EscapeDataString({Formatter}.Format(shipment.Id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"s\", shipment);", tool);
        Assert.DoesNotContain("\"Note\"", tool);
    }

    [Fact]
    public void Generator_SendsTheQueryPropertiesOfANamedFromRouteParameter_UnderItsName()
    {
        // MVC binds the properties of [FromRoute(Name = "o")] under o, so City from ?o.c=.
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"aliased/{orderId}\")] public Task<string> Find([FromRoute(Name = \"o\")] AliasedRouteQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Order = System.Uri.EscapeDataString({Formatter}.Format(q.Order));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"o.c\", q.City);", tool);
    }

    [Theory]
    [InlineData(@"([FromRoute(Name = ""orderId"")] int id) => id", "id")]
    [InlineData(@"([AsParameters] OrderRoute route) => route.Id", "Id")]
    public void EndpointParameter_FillsTheRouteValue_ItsFromRouteNameNames(string handler, string argumentName)
    {
        var source = MinimalApi($@"app.MapGet(""/orders/{{orderId}}"", {handler}).WithName(""Tool"");") + @"
namespace TestApi
{
    public sealed record OrderRoute([FromRoute(Name = ""orderId"")] int Id);
}
";
        var diagnostics = GetGeneratorDiagnostics(source, MinimalApisConfig);

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_ToolTool");

        Assert.DoesNotContain(diagnostics, x => x.Id == UnbuildableEndpointId);
        Assert.Contains($"var route = $\"/orders/{{route{argumentName}}}\";", tool);
        Assert.Contains($"The argument '{argumentName}' fills the route value {{orderId}} of the API's URL", tool);
    }
}
