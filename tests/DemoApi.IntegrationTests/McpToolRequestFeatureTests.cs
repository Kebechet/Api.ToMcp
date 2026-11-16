using System.Threading.RateLimiting;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// The feature that marks a tool's in-process request, as app code such as a rate limiter's partition reads it. Each app
/// runs on Kestrel, so the MCP request and the tool's request carry a real client address.
/// </summary>
public sealed class McpToolRequestFeatureTests
{
    private const string LoopPreventionHeader = "X-MCP-Internal-Call";
    private const string ProbeRoute = "/api/tool-request";
    private const string TogetherRoute = "/api/tool-request/together";
    private const string ToolRequestPartition = "mcp tool requests";
    private const string UnmarkedProbe = """{"isToolRequest":false,"toolName":null}""";

    private readonly TaskCompletionSource _isEveryRequestIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestsIn;

    [Fact]
    public async Task AToolsRequest_InProcess_IsMarked_WithTheToolsName()
    {
        await using var app = await StartAppAsync();
        await using var client = await ConnectAsync(app);

        var result = await CallToolAsync(client, ToolRequestProbeTools.FirstName, ProbeRoute);

        Assert.Equal($$"""{"isToolRequest":true,"toolName":"{{ToolRequestProbeTools.FirstName}}"}""", result);
    }

    [Fact]
    public async Task AClientsRequest_ThatCarriesTheLoopPreventionHeader_IsNotMarked()
    {
        await using var app = await StartAppAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + ProbeRoute);
        request.Headers.Add(LoopPreventionHeader, "true");

        using var response = await http.SendAsync(request);

        Assert.Equal(UnmarkedProbe, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AToolsRequest_OverHttp_IsNotMarked()
    {
        // The server receives it from the network, as any client's request.
        await using var app = await StartAppAsync(areApiCallsInProcess: false);
        await using var client = await ConnectAsync(app);

        var result = await CallToolAsync(client, ToolRequestProbeTools.FirstName, ProbeRoute);

        Assert.Equal(UnmarkedProbe, result);
    }

    [Fact]
    public async Task ConcurrentCallsOfDifferentTools_EachMarkTheirRequest_WithTheirOwnToolsName()
    {
        // The API answers neither request before both have come in, so the two calls run at once.
        await using var app = await StartAppAsync();
        await using var client = await ConnectAsync(app);

        var results = await Task.WhenAll(
            CallToolAsync(client, ToolRequestProbeTools.FirstName, TogetherRoute),
            CallToolAsync(client, ToolRequestProbeTools.SecondName, TogetherRoute));

        Assert.Equal($$"""{"isToolRequest":true,"toolName":"{{ToolRequestProbeTools.FirstName}}"}""", results[0]);
        Assert.Equal($$"""{"isToolRequest":true,"toolName":"{{ToolRequestProbeTools.SecondName}}"}""", results[1]);
    }

    [Fact]
    public async Task APerAddressConcurrencyLimitOfOne_RefusesEveryToolCall_WhenToolRequestsShareTheClientsPartition()
    {
        // The tool's request carries the MCP request's connection, and the MCP request holds the client's one permit.
        await using var app = await StartAppAsync(rateLimiting: RateLimiting.PerAddress);
        await using var client = await ConnectAsync(app);

        var result = await CallToolAsync(client, ToolRequestProbeTools.FirstName, ProbeRoute);

        Assert.StartsWith("HTTP 429: Too Many Requests", result);
    }

    [Fact]
    public async Task APerAddressConcurrencyLimitOfOne_LetsEveryToolCallThrough_WhenToolRequestsHaveAPartitionOfTheirOwn()
    {
        await using var app = await StartAppAsync(rateLimiting: RateLimiting.PerAddressButNotToolRequests);
        await using var client = await ConnectAsync(app);

        var first = await CallToolAsync(client, ToolRequestProbeTools.FirstName, ProbeRoute);
        var second = await CallToolAsync(client, ToolRequestProbeTools.SecondName, ProbeRoute);

        Assert.Equal($$"""{"isToolRequest":true,"toolName":"{{ToolRequestProbeTools.FirstName}}"}""", first);
        Assert.Equal($$"""{"isToolRequest":true,"toolName":"{{ToolRequestProbeTools.SecondName}}"}""", second);
    }

    private async Task<WebApplication> StartAppAsync(bool areApiCallsInProcess = true, RateLimiting rateLimiting = RateLimiting.None)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpTools(typeof(ToolRequestProbeTools).Assembly, options => options.AreApiCallsInProcess = areApiCallsInProcess);
        if (areApiCallsInProcess)
        {
            // A tool's request over HTTP would come from the same address, so the client refuses it.
            builder.Services.AddHttpClient(nameof(IMcpHttpInvoker)).ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler());
        }

        if (rateLimiting != RateLimiting.None)
        {
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    rateLimiting == RateLimiting.PerAddressButNotToolRequests && context.IsMcpToolRequest()
                        ? RateLimitPartition.GetNoLimiter(ToolRequestPartition)
                        : RateLimitPartition.GetConcurrencyLimiter(
                            context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                            _ => new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));
            });
        }

        var app = builder.Build();
        if (rateLimiting != RateLimiting.None)
        {
            app.UseRateLimiter();
        }

        app.MapGet(ProbeRoute, Describe);
        app.MapGet(TogetherRoute, async (HttpContext context) =>
        {
            if (Interlocked.Increment(ref _requestsIn) == 2)
            {
                _isEveryRequestIn.SetResult();
            }

            await _isEveryRequestIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Describe(context);
        });
        app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await app.StartAsync();

        return app;
    }

    private static ToolRequestProbe Describe(HttpContext context)
    {
        return new ToolRequestProbe(context.IsMcpToolRequest(), context.Features.Get<IMcpToolRequestFeature>()?.ToolName);
    }

    private static async Task<McpClient> ConnectAsync(WebApplication app)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(app.Urls.Single() + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            NullLoggerFactory.Instance);

        return await McpClient.CreateAsync(transport);
    }

    private static async Task<string> CallToolAsync(McpClient client, string toolName, string route)
    {
        var result = await client.CallToolAsync(toolName, new Dictionary<string, object?> { ["route"] = route });

        return ((TextContentBlock)result.Content[0]).Text;
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"The tool sent {request.RequestUri} over HTTP.");
        }
    }

    private enum RateLimiting
    {
        None,
        PerAddress,
        PerAddressButNotToolRequests
    }

    private sealed record ToolRequestProbe(bool IsToolRequest, string? ToolName);
}

[McpServerToolType]
public static class ToolRequestProbeTools
{
    public const string FirstName = "tool_request_first";
    public const string SecondName = "tool_request_second";

    [McpServerTool(Name = FirstName)]
    public static Task<CallToolResult> First(IMcpHttpInvoker invoker, string route, CancellationToken cancellationToken)
    {
        return invoker.GetAsync(route, cancellationToken);
    }

    [McpServerTool(Name = SecondName)]
    public static Task<CallToolResult> Second(IMcpHttpInvoker invoker, string route, CancellationToken cancellationToken)
    {
        return invoker.GetAsync(route, cancellationToken);
    }
}
