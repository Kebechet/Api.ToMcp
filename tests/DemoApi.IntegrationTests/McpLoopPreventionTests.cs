using System.Net;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
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
    public async Task ServesAnApiRoute_ToAnInternalCall_WhenTheMcpEndpointIsElsewhere()
    {
        // A tool's own request carries the header; only the MCP endpoint refuses it, whatever the path looks like.
        await using var app = await StartAppAsync("ai");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp/status");
        request.Headers.Add(LoopPreventionHeader, "true");

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("up", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> StartAppAsync(string pattern, bool useLoopPrevention = true, bool routeAfterLoopPrevention = false)
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

        app.MapGet("/mcp/status", () => "up");
        app.MapMcpEndpoint(pattern);
        await app.StartAsync();

        return app;
    }
}
