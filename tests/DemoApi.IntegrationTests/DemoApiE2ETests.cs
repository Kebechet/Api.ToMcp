using System.ComponentModel;
using System.Net;
using System.Reflection;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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

        var task = (Task<CallToolResult>)invoke.Invoke(null, new object?[] { invoker, LaptopId, CancellationToken.None })!;
        var result = await task;

        Assert.NotEqual(true, result.IsError);
        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.Contains("Laptop", text);
        Assert.Contains(LaptopId.ToString(), text);
    }

    [Fact]
    public async Task GeneratedTool_PropagatesCancellation_ToTheHttpCall()
    {
        // A cancelled token must abort the call. If the generated tool dropped the token
        // (passing default), this request would instead succeed and return data.
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

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = (Task<CallToolResult>)invoke.Invoke(null, new object?[] { invoker, LaptopId, cts.Token })!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void GeneratedTool_TakesDescriptions_FromXmlDocs_InRealBuild()
    {
        // Proves the real demo build (GenerateDocumentationFile=true) flows <summary>/<param>
        // docs into the generated [Description] attributes, not the generic fallbacks.
        var toolType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.ProductsController_GetByIdTool", throwOnError: true)!;
        var method = toolType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;

        var toolDescription = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.Equal("Gets a product by its unique identifier.", toolDescription);

        var idParam = method.GetParameters().Single(p => p.Name == "id");
        var paramDescription = idParam.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.Equal("The unique identifier of the product to retrieve.", paramDescription);
    }

    [Fact]
    public async Task GeneratedTool_ReturnsIsError_WhenApiReturnsErrorStatus()
    {
        // A non-2xx downstream response must surface as isError = true on the tool result
        // (issue #7) - conditioned, not thrown - so the model sees a deliberate failure.
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

        var unknownId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var task = (Task<CallToolResult>)invoke.Invoke(null, new object?[] { invoker, unknownId, CancellationToken.None })!;
        var result = await task;

        Assert.True(result.IsError);
        Assert.Contains("404", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_SurfacesIsError_OverTheWire()
    {
        // The definitive #7 test: drive the real /mcp endpoint with an MCP client and assert
        // that a failed downstream call comes back as isError:true in the protocol response
        // (verifies the SDK maps the returned CallToolResult.IsError onto the wire).
        await using var client = await ConnectMcpClientAsync();

        var ok = await client.CallToolAsync(
            "Products_GetById",
            new Dictionary<string, object?> { ["id"] = LaptopId.ToString() });
        Assert.NotEqual(true, ok.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)ok.Content[0]).Text);

        var failed = await client.CallToolAsync(
            "Products_GetById",
            new Dictionary<string, object?> { ["id"] = "ffffffff-ffff-ffff-ffff-ffffffffffff" });
        Assert.True(failed.IsError);
        Assert.Contains("404", ((TextContentBlock)failed.Content[0]).Text);
    }

    private async Task<McpClient> ConnectMcpClientAsync()
    {
        // Route the invoker's internal self-call back into the in-memory server (otherwise it
        // uses a real HttpClient that can't reach the TestServer, and every call "fails").
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("McpTools:BaseUrl", "http://localhost");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<IMcpHttpInvoker, McpHttpInvoker>()
                    .ConfigurePrimaryHttpMessageHandler(sp =>
                        ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler());
            });
        });

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            factory.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport);
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
