using System.Net;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// Loop prevention in an app that maps the MCP endpoint somewhere other than the demo's <c>mcp</c>.
/// </summary>
public class McpLoopPreventionTests
{
    private const string LoopPreventionHeader = "X-MCP-Internal-Call";
    private const string RefusalText = "MCP endpoints cannot be called internally to prevent loops.";

    [Theory]
    [InlineData("ai", "/ai")]
    [InlineData("tenants/{tenant}/assistant", "/tenants/acme/assistant")]
    public async Task RefusesAnInternalCall_ToTheMcpEndpoint_WhereverMapMcpEndpointMapsIt(string pattern, string path)
    {
        await using var app = await StartAppAsync(pattern);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(LoopPreventionHeader, "true");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RefusalText, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RefusesAnInternalCall_ToTheMcpEndpoint_WhenRoutingRunsAfterTheMiddleware()
    {
        await using var app = await StartAppAsync("ai", routeAfterLoopPrevention: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai");
        request.Headers.Add(LoopPreventionHeader, "true");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RefusalText, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LeavesAnInternalCall_ToTheMcpEndpoint_ToTheSdk_WithoutTheMiddleware()
    {
        await using var app = await StartAppAsync("ai", useLoopPrevention: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai");
        request.Headers.Add(LoopPreventionHeader, "true");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.NotEqual(RefusalText, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("ai", "/ai")]
    [InlineData("tenants/{tenant}/assistant", "/tenants/acme/assistant")]
    public async Task RefusesAToolsInProcessRequest_ToTheMcpEndpoint_WithoutTheMiddleware(string pattern, string path)
    {
        // https://github.com/Kebechet/Api.ToMcp/issues/100
        await using var app = await StartAppAsync(pattern, useLoopPrevention: false);
        await using var client = await ConnectAsync(app, path);

        var result = await client.CallToolAsync("parity_post", new Dictionary<string, object?> { ["route"] = path, ["body"] = "{}" });

        Assert.True(result.IsError);
        Assert.Equal($"HTTP 400: Bad Request - {RefusalText}", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task ServesAClientsRequest_ThatCarriesTheHeader_WithoutTheMiddleware()
    {
        // https://github.com/Kebechet/Api.ToMcp/issues/100: any client can send the header, which marks no request a tool's
        // without the middleware.
        await using var app = await StartAppAsync("ai", useLoopPrevention: false);
        await using var client = await ConnectAsync(app, "/ai", new Dictionary<string, string> { [LoopPreventionHeader] = "true" });

        var result = await client.CallToolAsync("parity_get", new Dictionary<string, object?> { ["route"] = "/api/status" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("up", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task ServesTheMcpEndpoint_ToAClient_AtThePatternItIsMappedAt()
    {
        await using var app = await StartAppAsync("tenants/{tenant}/assistant");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/tenants/acme/assistant"), TransportMode = HttpTransportMode.StreamableHttp },
            app.GetTestClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        // An initialize handshake and a ping: the 2026-07-28 protocol has neither.
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = "2025-06-18" });

        var ping = await client.PingAsync();

        Assert.NotNull(ping);
    }

    [Fact]
    public async Task ServesAnApiRoute_ToAnInternalCall()
    {
        // A tool's own request carries the header, and the API serves it.
        await using var app = await StartAppAsync("ai");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add(LoopPreventionHeader, "true");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("up", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/status")]
    public async Task RefusesAnInternalCall_ToAnEndpointTheSdksMapMcpMapsAtMcp_AndToAnyPathUnderIt(string path)
    {
        // An app that maps the endpoint with the SDK's MapMcp directly kept this protection from before MapMcpEndpoint
        // refused internal calls itself.
        await using var app = await StartAppAsync("mcp", mapWithTheSdk: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(LoopPreventionHeader, "true");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RefusalText, await response.Content.ReadAsStringAsync());
    }

    private static async Task<McpClient> ConnectAsync(WebApplication app, string path, Dictionary<string, string>? headers = null)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost" + path),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers
            },
            app.GetTestClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport);
    }

    private static async Task<WebApplication> StartAppAsync(string pattern, bool useLoopPrevention = true, bool routeAfterLoopPrevention = false, bool mapWithTheSdk = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpTools(typeof(McpLoopPreventionTests).Assembly);

        var app = builder.Build();
        if (useLoopPrevention)
        {
            app.UseMcpLoopPrevention();
        }

        if (routeAfterLoopPrevention)
        {
            app.UseRouting();
        }

        app.MapGet("/api/status", () => "up");
        app.MapPost("/mcp/status", () => "up");
        if (mapWithTheSdk)
        {
            app.MapMcp(pattern);
        }
        else
        {
            app.MapMcpEndpoint(pattern);
        }
        await app.StartAsync();

        return app;
    }
}
