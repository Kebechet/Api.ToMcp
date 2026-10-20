using System;
using System.Linq;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Where a generated tool sends a parameter that names no binding source. MVC binds a simple type, one it reads from a
/// single string through a type converter or a static <c>TryParse</c>, from the route or the query string on every HTTP
/// method. A complex type it binds from the body in an <c>[ApiController]</c>, and property by property from the query
/// string, among the other value providers, in a controller without one. A parameter sent anywhere else reaches the
/// action as its default value, and the call runs anyway.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string SimpleTypeToolClassName = "OrdersController_FindTool";
    private const string ApiControllerDeclaration = "[ApiController] [Route(\"api/[controller]\")] public class OrdersController : ControllerBase";
    private const string PlainControllerDeclaration = "[Route(\"api/[controller]\")] public class OrdersController : ControllerBase";

    [Theory]
    [InlineData("OrderId orderId")]
    [InlineData("OrderId? orderId")]
    [InlineData("CustomerId customerId")]
    [InlineData("CustomerId? customerId")]
    [InlineData("InvoiceId invoiceId")]
    [InlineData("ProjectId projectId")]
    [InlineData("Sku sku")]
    [InlineData("Sku? sku")]
    [InlineData("Slug slug")]
    [InlineData("TicketId ticketId")]
    [InlineData("IfaceId ifaceId")]
    [InlineData("OrderStatus status")]
    [InlineData("Version version")]
    [InlineData("Uri link")]
    public void Generator_SendsAParameterOfASimpleType_OnGet_AsAQueryValue(string parameter)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        var name = parameter.Split(' ')[1];
        Assert.Contains($"AddQueryValue(queryParts, \"{name}\", {name});", tool);
    }

    [Theory]
    [InlineData("HttpPost", "await invoker.PostAsync(route, null, cancellationToken)")]
    [InlineData("HttpPut", "await invoker.PutAsync(route, null, cancellationToken)")]
    [InlineData("HttpPatch", "await invoker.PatchAsync(route, null, cancellationToken)")]
    [InlineData("HttpDelete", "await invoker.DeleteAsync(route, cancellationToken)")]
    public void Generator_SendsAStronglyTypedId_OnEveryOtherHttpMethod_AsAQueryValue(string httpAttribute, string invokerCall)
    {
        // Sent as the JSON body, {"Value":"..."} binds nothing: MVC reads the id from the query string.
        var tool = GenerateCompilingSimpleTypeTool($"[{httpAttribute}(\"cancel\")] public Task<string> Find(OrderId orderId) => Task.FromResult(string.Empty);");

        Assert.Contains("AddQueryValue(queryParts, \"orderId\", orderId);", tool);
        Assert.Contains(invokerCall, tool);
        Assert.DoesNotContain("bodyJson", tool);
    }

    [Theory]
    [InlineData("OrderId? orderId")]
    [InlineData("CustomerId customerId")]
    [InlineData("Sku sku")]
    [InlineData("TicketId ticketId")]
    [InlineData("OrderStatus status")]
    [InlineData("string name")]
    [InlineData("int? count")]
    [InlineData("Guid? id")]
    [InlineData("DateOnly day")]
    [InlineData("TimeOnly at")]
    public void Generator_SendsAParameterOfASimpleType_OnPost_AsAQueryValue(string parameter)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpPost(\"cancel\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        var name = parameter.Split(' ')[1];
        Assert.Contains($"AddQueryValue(queryParts, \"{name}\", {name});", tool);
        Assert.DoesNotContain("bodyJson", tool);
    }

    [Fact]
    public void Generator_SendsAStronglyTypedIdNamedInTheRoute_AsTheRouteValue_NextToTheBody()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpPut(\"{orderId}\")] public Task<string> Find(OrderId orderId, [FromBody] ProductFilter filter) => Task.FromResult(string.Empty);");

        Assert.Contains("var routeorderId = System.Uri.EscapeDataString(global::Api.ToMcp.Runtime.Services.McpRequestValueFormatter.Format(orderId));", tool);
        Assert.Contains("var bodyJson = JsonSerializer.Serialize(filter);", tool);
        Assert.DoesNotContain("queryParts", tool);
    }

    [Theory]
    [InlineData("ProductFilter filter")]
    [InlineData("ProductFilter? filter")]
    [InlineData("ExpandableFilter filter")]
    [InlineData("Rating filter")]
    public void Generator_SendsAParameterOfAComplexType_OnPost_AsTheBody(string parameter)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpPost(\"search\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(filter);", tool);
        Assert.DoesNotContain("queryParts", tool);
    }

    [Theory]
    [InlineData("HttpGet", "ProductFilter filter")]
    [InlineData("HttpGet", "ProductFilter? filter")]
    [InlineData("HttpGet", "ExpandableFilter filter")]
    [InlineData("HttpDelete", "ProductFilter filter")]
    [InlineData("HttpDelete", "ProductFilter? filter")]
    public void Generator_DoesNotSendAParameterOfAComplexType_OnGetOrDelete_OfAnApiController(string httpAttribute, string parameter)
    {
        // [ApiController] binds it from the body, which the tool cannot send on GET or DELETE; MVC answers 415 either way.
        var tool = GenerateCompilingSimpleTypeTool($"[{httpAttribute}(\"search\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain("queryParts", tool);
    }

    [Theory]
    [InlineData("HttpGet", "ProductFilter filter")]
    [InlineData("HttpGet", "ProductFilter? filter")]
    [InlineData("HttpGet", "ExpandableFilter filter")]
    [InlineData("HttpDelete", "ProductFilter filter")]
    [InlineData("HttpDelete", "ProductFilter? filter")]
    public void Generator_ExpandsAParameterOfAComplexType_OnGetOrDelete_OfAControllerWithoutApiController_IntoTheQueryString(string httpAttribute, string parameter)
    {
        // Without [ApiController] MVC binds it property by property from every value provider, the query string included.
        var tool = GenerateCompilingSimpleTypeTool(
            $"[{httpAttribute}(\"search\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);",
            PlainControllerDeclaration);

        Assert.Contains("AddQueryValue(queryParts, \"filter\", filter);", tool);
    }

    [Theory]
    [InlineData("", "[ApiController] public abstract class ApiControllerBase : ControllerBase { } [Route(\"api/[controller]\")] public class OrdersController : ApiControllerBase")]
    [InlineData("[assembly: ApiController]", PlainControllerDeclaration)]
    public void Generator_TreatsAControllerAsAnApiController_WhenABaseClassOrItsAssemblyHasTheAttribute(string assemblyAttributes, string controllerDeclaration)
    {
        // MVC reads [ApiController] from base classes, since it is inherited, and from the assembly.
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpGet(\"search\")] public Task<string> Find(ProductFilter filter) => Task.FromResult(string.Empty);",
            controllerDeclaration,
            assemblyAttributes);

        Assert.DoesNotContain("queryParts", tool);
    }

    private static string GenerateCompilingSimpleTypeTool(
        string action,
        string controllerDeclaration = ApiControllerDeclaration,
        string assemblyAttributes = "")
    {
        var source = SimpleTypeSources.Usings + assemblyAttributes + SimpleTypeSources.Types + $@"
namespace TestApi
{{
    {controllerDeclaration}
    {{
        {action}
    }}
}}
";
        var errors = CompileWithGeneratedTools(source, AllExceptExcludedConfig);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));

        return GetToolClass(RunGenerator(source, AllExceptExcludedConfig), SimpleTypeToolClassName);
    }
}
