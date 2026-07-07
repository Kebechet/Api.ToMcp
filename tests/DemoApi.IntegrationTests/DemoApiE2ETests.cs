using System.Net;
using System.Reflection;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// End-to-end tests that boot the real DemoApi in-memory via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// and exercise the full package: source-generated tools, the runtime invoker, loop-prevention
/// middleware, and the wired-up MCP endpoint.
/// </summary>
public class DemoApiE2ETests : IClassFixture<WebApplicationFactory<Program>>
{
    // Seeded in ProductsController.
    private static readonly Guid LaptopId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    private readonly WebApplicationFactory<Program> _factory;

    public DemoApiE2ETests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Controller_Endpoint_ReturnsData()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/products");

        response.EnsureSuccessStatusCode();
        Assert.Contains("Laptop", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task McpEndpoint_IsMappedAndBlocksInternalCalls()
    {
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add("X-MCP-Internal-Call", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot be called internally", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void GeneratedTools_HonorConfigSelectionAndMcpIgnore()
    {
        var toolNames = GetGeneratedToolTypeNames();

        // In the demo's generator.json (SelectedOnly): GetAll and GetById are included.
        Assert.Contains("ProductsController_GetAllTool", toolNames);
        Assert.Contains("ProductsController_GetByIdTool", toolNames);

        // Create is not in the include list; Delete carries [McpIgnore].
        Assert.DoesNotContain(toolNames, n => n.StartsWith("ProductsController_Create"));
        Assert.DoesNotContain(toolNames, n => n.StartsWith("ProductsController_Delete"));
    }

    [Fact]
    public async Task GeneratedTool_InvokesApiEndToEnd_ThroughRuntimeInvoker()
    {
        // Wire the runtime invoker to the in-memory test server so a generated tool's call
        // travels the real path: tool -> IMcpHttpInvoker -> HTTP -> ASP.NET pipeline -> controller.
        var client = _factory.CreateClient();
        var invoker = new McpHttpInvoker(
            client,
            new FixedBaseUrlProvider(client.BaseAddress!.ToString().TrimEnd('/')),
            new HttpContextAccessor(),
            NullLogger<McpHttpInvoker>.Instance,
            Options.Create(new McpScopeOptions()));

        var toolType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.ProductsController_GetByIdTool", throwOnError: true)!;
        var invoke = toolType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;

        var task = (Task<string>)invoke.Invoke(null, new object?[] { invoker, LaptopId })!;
        var json = await task;

        Assert.Contains("Laptop", json);
        Assert.Contains(LaptopId.ToString(), json);
    }

    private static string[] GetGeneratedToolTypeNames()
    {
        var infoType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.McpToolsInfo", throwOnError: true)!;
        var toolTypes = (Type[])infoType
            .GetField("ToolTypes", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        return toolTypes.Select(t => t.Name).ToArray();
    }

    private sealed class FixedBaseUrlProvider : ISelfBaseUrlProvider
    {
        private readonly string _baseUrl;
        public FixedBaseUrlProvider(string baseUrl) => _baseUrl = baseUrl;
        public string GetBaseUrl() => _baseUrl;
    }
}
