using System;
using System.Linq;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Where a generated tool sends a parameter that names no binding source. <c>[ApiController]</c> binds a simple type,
/// one MVC reads from a single string through a type converter or a static <c>TryParse</c>, from the route or the query
/// string on every HTTP method, and a complex type from the body. A parameter sent anywhere else reaches the action as
/// its default value, and the call runs anyway.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string SimpleTypeToolClassName = "OrdersController_FindTool";

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
    [InlineData("ProductFilter filter")]
    [InlineData("ProductFilter? filter")]
    [InlineData("ExpandableFilter filter")]
    public void Generator_DoesNotExpandAParameterOfAComplexType_OnGet_IntoTheQueryString(string parameter)
    {
        // [ApiController] binds it from the body, which a GET does not carry; a filter read from the query string says [FromQuery].
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain("queryParts", tool);
    }

    private static string GenerateCompilingSimpleTypeTool(string action)
    {
        var source = SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
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
