using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Complex parameters MVC binds property by property from the route, query and form values instead of the body: every
/// one in a controller without <c>[ApiController]</c>, and in an <c>[ApiController]</c> one whose type has a property with
/// a binding source of its own. MVC reads the properties under the parameter's name as a prefix (<c>q.City</c>) when a
/// value carries it, and under their own names otherwise; a property filled from the route binds only without the
/// prefix, since no route value is named <c>q.Id</c>.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string Formatter = "global::Api.ToMcp.Runtime.Services.McpRequestValueFormatter";

    [Theory]
    [InlineData("HttpGet", "await invoker.GetAsync(route, cancellationToken)")]
    [InlineData("HttpPost", "await invoker.PostAsync(route, null, cancellationToken)")]
    [InlineData("HttpPut", "await invoker.PutAsync(route, null, cancellationToken)")]
    [InlineData("HttpPatch", "await invoker.PatchAsync(route, null, cancellationToken)")]
    [InlineData("HttpDelete", "await invoker.DeleteAsync(route, cancellationToken)")]
    public void Generator_FillsTheRouteFromARouteProperty_AndSendsTheQueryPropertiesUnprefixed(string httpAttribute, string invokerCall)
    {
        // MVC answers /attr/7?City=Brno with Id 7, and /attr/7?q.City=Brno with Id 0; a JSON body it ignores.
        var tool = GenerateCompilingSimpleTypeTool($"[{httpAttribute}(\"attr/{{id:int}}\")] public Task<string> Find(AttributedQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Id = System.Uri.EscapeDataString({Formatter}.Format(q.Id));", tool);
        Assert.Contains("var route = $\"/api/orders/attr/{routeq_Id}\";", tool);
        Assert.Contains("AddQueryValue(queryParts, \"City\", q.City);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"Note\", q.Note);", tool);
        // A [FromHeader] property is read from a header, which the tool does not send.
        Assert.DoesNotContain("\"Tenant\"", tool);
        Assert.DoesNotContain("\"Id\"", tool);
        Assert.DoesNotContain("\"q\"", tool);
        Assert.DoesNotContain("bodyJson", tool);
        Assert.Contains(invokerCall, tool);
    }

    [Fact]
    public void Generator_ReadsThePropertiesOfANullableParameter_WithoutDereferencingNull()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"attr/{id:int}\")] public Task<string> Find(AttributedQuery? q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Id = System.Uri.EscapeDataString({Formatter}.Format(q?.Id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"City\", q?.City);", tool);
    }

    [Fact]
    public void Generator_FillsTheRouteFromAnUnattributedPropertyNamedLikeThePlaceholder()
    {
        // Without the prefix MVC finds an unattributed property among the route values too.
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"byname/{id:int}\")] public Task<string> Find(RouteByNameQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Id = System.Uri.EscapeDataString({Formatter}.Format(q.Id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"City\", q.City);", tool);
        Assert.DoesNotContain("\"Id\"", tool);
    }

    [Fact]
    public void Generator_MatchesAPropertyByTheNameItsBindingAttributeGives()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"aliased/{orderId}\")] public Task<string> Find(AliasedRouteQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Order = System.Uri.EscapeDataString({Formatter}.Format(q.Order));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"c\", q.City);", tool);
    }

    [Theory]
    [InlineData("HttpGet", "QueryOnlyFilter")]
    [InlineData("HttpPost", "QueryOnlyFilter")]
    [InlineData("HttpDelete", "QueryOnlyFilter")]
    [InlineData("HttpPost", "DerivedQuery")]
    public void Generator_ExpandsAParameterWithPropertyBindingSourcesButNoRouteProperty_UnderItsName(string httpAttribute, string type)
    {
        // A [FromQuery] property, an inherited one included, keeps MVC off the body; q.City and q.Note both bind.
        var tool = GenerateCompilingSimpleTypeTool($"[{httpAttribute}(\"search\")] public Task<string> Find({type} q) => Task.FromResult(string.Empty);");

        Assert.Contains("AddQueryValue(queryParts, \"q\", q);", tool);
        Assert.DoesNotContain("bodyJson", tool);
    }

    [Theory]
    [InlineData("HttpPost", "await invoker.PostAsync(route, null, cancellationToken)")]
    [InlineData("HttpPut", "await invoker.PutAsync(route, null, cancellationToken)")]
    [InlineData("HttpPatch", "await invoker.PatchAsync(route, null, cancellationToken)")]
    public void Generator_ExpandsAComplexParameter_OnEveryMethod_OfAControllerWithoutApiController(string httpAttribute, string invokerCall)
    {
        // Without [ApiController] MVC ignores a JSON body here and binds ?filter.City=Brno.
        var tool = GenerateCompilingSimpleTypeTool(
            $"[{httpAttribute}(\"search\")] public Task<string> Find(ProductFilter filter) => Task.FromResult(string.Empty);",
            PlainControllerDeclaration);

        Assert.Contains("AddQueryValue(queryParts, \"filter\", filter);", tool);
        Assert.Contains(invokerCall, tool);
        Assert.DoesNotContain("bodyJson", tool);
    }

    [Fact]
    public void Generator_FillsTheRouteFromAPropertyOfAComplexParameter_OfAControllerWithoutApiController()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPut(\"shipments/{id:int}\")] public Task<string> Find(Shipment shipment) => Task.FromResult(string.Empty);",
            PlainControllerDeclaration);

        Assert.Contains($"var routeshipment_Id = System.Uri.EscapeDataString({Formatter}.Format(shipment.Id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"Note\", shipment.Note);", tool);
        Assert.DoesNotContain("bodyJson", tool);
    }

    [Theory]
    [InlineData("[FromBody]")]
    [InlineData("[FromRoute]")]
    public void Generator_FillsAPlaceholderNoParameterNames_FromAPropertyOfABoundSimpleType_SoTheToolCompiles(string bindingAttribute)
    {
        // Left unfilled, {value} names no variable in the emitted string and the tool does not compile. The route then
        // only reaches the action: MVC reads the [FromBody] id from the body, but a [FromRoute] one from a route value
        // named id, which this route has none of, so that id binds as its default.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpPost(\"{{value}}\")] public Task<string> Find({bindingAttribute} OrderId id) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeid_Value = System.Uri.EscapeDataString({Formatter}.Format(id.Value));", tool);
    }

    [Fact]
    public void Generator_FillsTheRouteOfAFromRouteParameter_ByTheNamesItsPropertiesBind_AndSendsItsQueryProperties()
    {
        // MVC reads [FromRoute(Name = "orderId")] Order from {orderId}, and the [FromQuery(Name = "c")] property from ?c=.
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"aliased/{orderId}\")] public Task<string> Find([FromRoute] AliasedRouteQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Order = System.Uri.EscapeDataString({Formatter}.Format(q.Order));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"c\", q.City);", tool);
    }

    [Theory]
    [InlineData("RecordPropertyFilter")]
    [InlineData("RecordParameterFilter")]
    [InlineData("NamedModelBinderFilter")]
    [InlineData("NamedBindPropertyFilter")]
    public void Generator_SendsAComplexParameterAsTheBody_WhenItsAttributesNameNoBindingSourceMvcCounts(string type)
    {
        // MVC skips the properties a record's constructor sets, and [ModelBinder] or [BindProperty] without a binder
        // type names no source; it reads the type from the body, and the query string gets a 415.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpPost(\"search\")] public Task<string> Find({type} q) => Task.FromResult(string.Empty);");

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(q);", tool);
        Assert.DoesNotContain("queryParts", tool);
    }

    [Theory]
    [InlineData("RecordWithQueryProperty")]
    [InlineData("CustomBinderFilter")]
    public void Generator_SendsAComplexParameterInTheQuery_WhenAPropertyMvcCountsNamesABindingSource(string type)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpPost(\"search\")] public Task<string> Find({type} q) => Task.FromResult(string.Empty);");

        Assert.Contains("AddQueryValue(queryParts, \"q\", q);", tool);
        Assert.DoesNotContain("bodyJson", tool);
    }

    [Fact]
    public void Generator_EscapesNamesThatAreCSharpKeywords_WhereverItWritesThemAsIdentifiers()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPost(\"kw/{namespace}/{id}\")] public Task<string> Find(string @namespace, KeywordQuery q, [FromBody] ProductFilter @object, [FromQuery] string? @class, [FromQuery] int? @await) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routenamespace = System.Uri.EscapeDataString({Formatter}.Format(@namespace));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"event\", q.@event);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"class\", @class);", tool);
        // await is a keyword only inside an async method, which the tool is.
        Assert.Contains("AddQueryValue(queryParts, \"await\", @await);", tool);
        Assert.Contains("var bodyJson = JsonSerializer.Serialize(@object);", tool);
    }

    [Fact]
    public void Generator_LeavesOutAPropertyWhoseGetterIsNotPublic()
    {
        // MVC does not bind it either, and the generated tool could not read it.
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"priv/{id}\")] public Task<string> Find(PrivateGetterQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Id = System.Uri.EscapeDataString({Formatter}.Format(q.Id));", tool);
        Assert.DoesNotContain("\"Note\"", tool);
    }

    [Theory]
    [InlineData("AttributedQuery a, NotedShipment b")]
    [InlineData("NotedShipment b, AttributedQuery a")]
    public void Generator_FillsTheRouteFromAFromRouteProperty_AndKeepsAParameterWhoseIdIsUnattributedUnderItsPrefix(string parameters)
    {
        // MVC reads a's [FromRoute] Id only without the a. prefix. b's unattributed Id it reads from the route without the
        // b. prefix too, but from b.Id under it, which keeps b's own value: unprefixed, b.Id and b.Note would get a's.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"two/{{id}}\")] public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routea_Id = System.Uri.EscapeDataString({Formatter}.Format(a.Id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"City\", a.City);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"Note\", a.Note);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"b\", b);", tool);
    }

    [Theory]
    [InlineData("[HttpPut(\"ship/{id}\")] public Task<string> Find(int id, Shipment b)")]
    [InlineData("[HttpGet(\"same/{id}\")] public Task<string> Find(Shipment a, Shipment b)")]
    public void Generator_KeepsAParameterWhoseUnattributedPropertyDoesNotFillTheRoute_UnderItsPrefix(string action)
    {
        // Without [ApiController] MVC reads b.Id from the query under the b. prefix; unprefixed, it would get {id}.
        var tool = GenerateCompilingSimpleTypeTool($"{action} => Task.FromResult(string.Empty);", PlainControllerDeclaration);

        Assert.Contains("AddQueryValue(queryParts, \"b\", b);", tool);
    }

    [Fact]
    public void Generator_SendsAParameterWithAPropertyReadFromTheRoute_UnderItsOwnNames_WhenAParameterFillsTheRoute()
    {
        // The parameter id fills {id}; MVC still reads q.Id from it, but only without the q. prefix.
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"direct/{id}\")] public Task<string> Find(int id, AttributedQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeid = System.Uri.EscapeDataString({Formatter}.Format(id));", tool);
        Assert.Contains("AddQueryValue(queryParts, \"City\", q.City);", tool);
        Assert.DoesNotContain("\"q\"", tool);
    }

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
    [InlineData("string name")]
    [InlineData("int? count")]
    [InlineData("OrderId orderId")]
    public void Generator_SendsAParameterOfASimpleType_OnPost_OfAControllerWithoutApiController_AsAQueryValue(string parameter)
    {
        var tool = GenerateCompilingSimpleTypeTool(
            $"[HttpPost(\"search\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);",
            PlainControllerDeclaration);

        var name = parameter.Split(' ')[1];
        Assert.Contains($"AddQueryValue(queryParts, \"{name}\", {name});", tool);
        Assert.DoesNotContain("bodyJson", tool);
    }
}
