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

    [Theory]
    [InlineData(PlainControllerDeclaration, "[FromQuery(Name = \"id\")] int x, int id", "AddQueryValue(queryParts, \"id\", x);")]
    [InlineData(PlainControllerDeclaration, "[FromHeader(Name = \"id\")] string? h, int id", "")]
    [InlineData(ApiControllerDeclaration, "[ModelBinder(Name = \"id\")] string? value, int id", "AddQueryValue(queryParts, \"id\", value);")]
    public void Generator_FillsARouteValue_FromTheParameterMvcReadsFromIt_NotOneItBindsUnderThatNameElsewhere(string controllerDeclaration, string parameters, string expectedQueryValue)
    {
        // MVC binds id from {id}, and the parameter before it under the name id from the query string or a header. Filled
        // from that one, the route would give id its value, and a DELETE would remove the record it names.
        var tool = GenerateCompilingSimpleTypeTool(
            $"[HttpGet(\"x/{{id}}\")] public Task<string> Find({parameters}) => Task.FromResult(string.Empty);",
            controllerDeclaration);

        Assert.Contains($"var routeid = System.Uri.EscapeDataString({Formatter}.Format(id));", tool);
        Assert.Contains("var route = $\"/api/orders/x/{routeid}\";", tool);
        if (expectedQueryValue.Length == 0)
        {
            Assert.DoesNotContain("queryParts", tool);
        }
        else
        {
            Assert.Contains(expectedQueryValue, tool);
        }
    }

    [Fact]
    public void Generator_FillsARouteValue_FromAPropertyMvcReadsFromIt_NotFromAParameterItBindsUnderThatNameFromTheQuery()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"x/{id}\")] public Task<string> Find([FromQuery(Name = \"id\")] int q, [FromRoute] Shipment s) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routes_Id = System.Uri.EscapeDataString({Formatter}.Format(s.Id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"id\", q);", tool);
    }

    [Fact]
    public void Generator_FillsARouteValue_FromAPropertyOfAnObjectThePlaceholderNamesAsItsPrefix()
    {
        // A prefix names the object's properties, id.Id and id.Note, not a value the route can carry.
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"x/{id:int}\")] public Task<string> Find([FromQuery(Name = \"id\")] Shipment shipment) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeshipment_Id = System.Uri.EscapeDataString({Formatter}.Format(shipment.Id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"id\", shipment);", tool);
    }

    [Fact]
    public void Generator_FillsAPlaceholder_FromABodyPropertyOfItsName_NotFromAParameterThatFillsAnotherOne()
    {
        // id fills {productId}; filled from id too, {id:guid} would get a number, and every call would get 404.
        var tool = GenerateCompilingSimpleTypeTool("[HttpPut(\"t/{id:guid}/items/{productId:int}\")] public Task<string> Find([FromRoute(Name = \"productId\")] int id, [FromBody] GuidDto body) => Task.FromResult(string.Empty);");

        Assert.Contains("var route = $\"/api/orders/t/{routebody_Id}/items/{routeid}\";", tool);
    }

    [Fact]
    public void Generator_SkipsAnAction_WhoseParameterFillsAnotherRouteValueThanThePlaceholderOfItsName()
    {
        const string action = "[HttpGet(\"t/{id}/items/{productId}\")] public Task<string> Find([FromRoute(Name = \"productId\")] int id) => Task.FromResult(string.Empty);";
        var source = SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
    {ApiControllerDeclaration}
    {{
        {action}
    }}
}}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);

        Assert.Equal(
            "No MCP tool is generated for action 'OrdersController.Find': no parameter or property fills the route value '{id}' of GET /api/orders/t/{id}/items/{productId}",
            warning.GetMessage());
    }

    [Theory]
    [InlineData(PlainControllerDeclaration, "[Bind(Prefix = \"\")] ProductFilter filter")]
    [InlineData(PlainControllerDeclaration, "[Bind(Prefix = \"\")] PrefixedFilter filter")]
    [InlineData(ApiControllerDeclaration, "[FromQuery(Name = \"\")] ProductFilter filter")]
    public void Generator_SendsThePropertiesOfAParameterAnEmptyNameNames_UnderTheirOwnNames(string controllerDeclaration, string parameter)
    {
        // MVC takes the first name that is not null, an empty one included, and binds the properties without a prefix:
        // City, never filter.City nor the type's pf.City.
        var tool = GenerateCompilingSimpleTypeTool(
            $"[HttpGet(\"search\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);",
            controllerDeclaration);

        Assert.Contains("AddQueryValue(queryParts, \"City\", filter.City);", tool);
        Assert.DoesNotContain("\"filter\"", tool);
        Assert.DoesNotContain("\"pf\"", tool);
    }

    [Theory]
    [InlineData(@"([FromQuery(Name = ""orderId"")] int q, int orderId) => 1")]
    [InlineData(@"([AsParameters] RouteAndQuery input) => 1")]
    public void EndpointParameter_FillsTheRouteValue_ThatItReadsFromTheRoute_NotOneThatTheQueryAliasNames(string handler)
    {
        var source = MinimalApi($@"app.MapGet(""/orders/{{orderId}}"", {handler}).WithName(""Tool"");") + @"
namespace TestApi
{
    public sealed record RouteAndQuery([FromQuery(Name = ""orderId"")] int Q, int OrderId);
}
";
        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_ToolTool");

        Assert.Matches(@"var route = \$""/orders/\{routeorderId\}"";", tool.Replace("routeOrderId", "routeorderId"));
        Assert.Matches(@"AddQueryValue\(queryParts, ""orderId"", (q|Q)\);", tool);
    }

    [Theory]
    [InlineData(@"([FromRoute(Name = ""id"")] int a, [FromRoute(Name = ""id"")] int b) => 1", "its inputs 'a' and 'b' are read from the same route value 'id', which a tool cannot give each a value of its own")]
    [InlineData(@"([FromQuery(Name = ""q"")] int a, string q) => 1", "its inputs 'a' and 'q' are read from the same query value 'q', which a tool cannot give each a value of its own")]
    public void Endpoint_WithTwoInputsReadUnderOneName_GetsNoToolAndWarning(string handler, string reason)
    {
        var source = MinimalApi($@"app.MapGet(""/orders/{{id}}"", {handler}).WithName(""Tool"");");

        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == UnbuildableEndpointId);

        Assert.EndsWith(reason, warning.GetMessage());
        Assert.DoesNotContain("Endpoints_ToolTool", RunGenerator(source, MinimalApisConfig));
    }

    [Fact]
    public void EndpointInputs_ReadUnderOneNameFromTheRouteAndTheQuery_EachGetAnArgument()
    {
        var source = MinimalApi(@"app.MapGet(""/orders/{id}"", ([FromRoute(Name = ""id"")] int a, [FromQuery(Name = ""id"")] int b) => 1).WithName(""Tool"");");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_ToolTool");

        Assert.Contains("var route = $\"/orders/{routea}\";", tool);
        Assert.Contains("AddQueryValue(queryParts, \"id\", b);", tool);
    }
}
