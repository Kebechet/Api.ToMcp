using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// A tool's API request at the limits Kestrel holds a request to, against the same request sent straight to Kestrel over a
/// socket, byte for byte as the tool's request would be written in HTTP/1.1; and the same request under a <c>TestServer</c>,
/// which holds it to none. The invoker's HTTP client refuses every request, so a tool call that succeeds was served in
/// process.
/// </summary>
public sealed class InProcessServerLimitsTests
{
    private const string Route = "/api/server-limits";
    private const string Served = "served";
    private const string PadHeader = "X-Pad";
    private const string McpPath = "/mcp";

    /// <summary>
    /// The header the invoker adds to every tool's request, besides <c>Host</c>.
    /// </summary>
    private const string LoopPreventionLine = "X-MCP-Internal-Call: true\r\n";

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 1)]
    [InlineData(1024, 0)]
    [InlineData(1024, 1)]
    public async Task AToolsRequestLine_IsHeldToKestrelsLimit_AsOneSentToTheServer(int? limit, int over)
    {
        await using var app = await StartAsync(kestrel => kestrel.Limits.MaxRequestLineSize = limit ?? kestrel.Limits.MaxRequestLineSize);
        var maxRequestLineSize = limit ?? new KestrelServerLimits().MaxRequestLineSize;

        // A generated tool escapes a route value, as "@" to "%40", and Kestrel counts the target as it arrives.
        var prefix = Route + "/%40%40/";
        var target = prefix + new string('a', maxRequestLineSize + over - "GET  HTTP/1.1\r\n".Length - prefix.Length);
        var direct = await SendDirectlyAsync(app, $"GET {target} HTTP/1.1\r\nHost: {GetHost(app)}\r\n{LoopPreventionLine}\r\n");
        var tool = await CallToolAsync(app, "parity_get", new() { ["route"] = target });

        Assert.Equal(over == 0 ? StatusCodes.Status200OK : StatusCodes.Status414UriTooLong, direct.Status);
        AssertAnsweredAlike(direct, tool);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 1)]
    [InlineData(4096, 0)]
    [InlineData(4096, 1)]
    public async Task AToolsHeaders_AreHeldToKestrelsTotalSize_AsThoseOfOneSentToTheServer(int? limit, int over)
    {
        await using var app = await StartAsync(kestrel => kestrel.Limits.MaxRequestHeadersTotalSize = limit ?? kestrel.Limits.MaxRequestHeadersTotalSize);
        var maxRequestHeadersTotalSize = limit ?? new KestrelServerLimits().MaxRequestHeadersTotalSize;

        var otherLines = $"Host: {GetHost(app)}\r\n{LoopPreventionLine}";
        var value = new string('p', maxRequestHeadersTotalSize + over - otherLines.Length - $"{PadHeader}: \r\n".Length);
        var direct = await SendDirectlyAsync(app, $"GET {Route}/target HTTP/1.1\r\n{otherLines}{PadHeader}: {value}\r\n\r\n");
        var tool = await CallToolAsync(app, "parity_header", new() { ["route"] = Route + "/target", ["header"] = PadHeader, ["value"] = value });

        Assert.Equal(over == 0 ? StatusCodes.Status200OK : StatusCodes.Status431RequestHeaderFieldsTooLarge, direct.Status);
        AssertAnsweredAlike(direct, tool);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 1)]
    [InlineData(20, 0)]
    [InlineData(20, 1)]
    public async Task AToolsHeaders_AreHeldToKestrelsNumber_AsThoseOfOneSentToTheServer(int? limit, int over)
    {
        await using var app = await StartAsync(kestrel => kestrel.Limits.MaxRequestHeaderCount = limit ?? kestrel.Limits.MaxRequestHeaderCount);
        var maxRequestHeaderCount = limit ?? new KestrelServerLimits().MaxRequestHeaderCount;

        // Host and X-MCP-Internal-Call are two of them.
        var count = maxRequestHeaderCount - 2 + over;
        var padLines = string.Concat(Enumerable.Range(1, count).Select(x => $"{PadHeader}-{x}: 1\r\n"));
        var direct = await SendDirectlyAsync(app, $"GET {Route}/target HTTP/1.1\r\nHost: {GetHost(app)}\r\n{LoopPreventionLine}{padLines}\r\n");
        var tool = await CallToolAsync(app, "limits_headers", new() { ["route"] = Route + "/target", ["count"] = count });

        Assert.Equal(over == 0 ? StatusCodes.Status200OK : StatusCodes.Status431RequestHeaderFieldsTooLarge, direct.Status);
        AssertAnsweredAlike(direct, tool);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AToolsBody_IsHeldToKestrelsMaxRequestBodySize_AsOneSentToTheServer(int over)
    {
        await using var app = await StartAsync(kestrel => kestrel.Limits.MaxRequestBodySize = 64);

        var body = "{\"pad\":\"" + new string('p', 64 + over - "{\"pad\":\"\"}".Length) + "\"}";
        var direct = await SendDirectlyAsync(
            app,
            $"POST {Route}/body HTTP/1.1\r\nHost: {GetHost(app)}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        var tool = await CallToolAsync(app, "parity_post", new() { ["route"] = Route + "/body", ["body"] = body });

        Assert.Equal(over == 0 ? StatusCodes.Status200OK : StatusCodes.Status413PayloadTooLarge, direct.Status);
        Assert.Equal(over == 1, tool.IsError);
        Assert.StartsWith(over == 0 ? Served : "HTTP 413", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsHeldToNoLimit_UnderTheTestServer_ThoughKestrelsOptionsAreConfigured()
    {
        // The web host configures Kestrel's options, and UseTestServer replaces the server that would read them.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.WebHost.ConfigureKestrel(SetTinyLimits);
        builder.Logging.ClearProviders();
        ConfigureServices(builder.Services);
        await using var app = builder.Build();
        app.UseMcpLoopPrevention();
        app.MapControllers();
        app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await app.StartAsync();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            app.GetTestClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);

        var line = await CallToolAsync(client, "parity_get", new() { ["route"] = Route + "/" + new string('a', 10_000) });
        var headersSize = await CallToolAsync(client, "parity_header", new() { ["route"] = Route + "/target", ["header"] = PadHeader, ["value"] = new string('p', 40_000) });
        var headerCount = await CallToolAsync(client, "limits_headers", new() { ["route"] = Route + "/target", ["count"] = 120 });
        var body = await CallToolAsync(client, "parity_post", new() { ["route"] = Route + "/body", ["body"] = new string('b', 1000) });

        Assert.Equal(10, app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        Assert.Equal((false, Served), line);
        Assert.Equal((false, Served), headersSize);
        Assert.Equal((false, Served), headerCount);
        Assert.Equal((false, Served + " 1000"), body);
    }

    [Fact]
    public async Task AGeneratedToolsRequest_IsHeldToNoLimit_UnderWebApplicationFactory()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.ConfigureKestrel(SetTinyLimits);
            web.ConfigureTestServices(services =>
                services.AddHttpClient(nameof(IMcpHttpInvoker)).ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler()));
        });
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            factory.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);
        var idempotencyKey = new string('k', 40_000);

        var review = await client.CallToolAsync("Products_AddReview", new Dictionary<string, object?>
        {
            ["id"] = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            ["review"] = new JsonObject { ["rating"] = 5, ["comment"] = new string('c', 1000) },
            ["idempotencyKey"] = idempotencyKey
        });
        var products = await client.CallToolAsync("Products_GetAll", new Dictionary<string, object?> { ["category"] = new string('c', 10_000) });

        Assert.NotEqual(true, review.IsError);
        Assert.Contains(idempotencyKey, ((TextContentBlock)review.Content[0]).Text);
        Assert.NotEqual(true, products.IsError);
    }

    private static void SetTinyLimits(KestrelServerOptions kestrel)
    {
        kestrel.Limits.MaxRequestLineSize = 100;
        kestrel.Limits.MaxRequestHeadersTotalSize = 100;
        kestrel.Limits.MaxRequestHeaderCount = 3;
        kestrel.Limits.MaxRequestBodySize = 10;
    }

    /// <summary>
    /// A refusal comes back as the error result <c>HTTP status: reason</c> of the status line Kestrel writes, without a
    /// body; a request Kestrel lets through reaches the action, in process too.
    /// </summary>
    private static void AssertAnsweredAlike((int Status, string StatusLine) direct, (bool IsError, string Text) tool)
    {
        if (direct.Status == StatusCodes.Status200OK)
        {
            Assert.Equal((false, Served), tool);
            return;
        }

        var reason = direct.StatusLine.Split(' ', 3)[2];
        Assert.Equal((true, $"HTTP {direct.Status}: {reason}"), tool);
    }

    private static async Task<WebApplication> StartAsync(Action<KestrelServerOptions> configureKestrel)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(configureKestrel);
        builder.Logging.ClearProviders();
        ConfigureServices(builder.Services);

        var app = builder.Build();

        // The MCP request carries the tool's body and more, so the server's body limit would refuse it before the tool's.
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(McpPath))
            {
                context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
            }

            return next(context);
        });
        app.UseMcpLoopPrevention();
        app.MapControllers();
        app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await app.StartAsync();

        return app;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(ServerLimitsController).Assembly);
        services.AddMcpTools(typeof(ServerLimitProbeTools).Assembly);
        services.AddHttpClient(nameof(IMcpHttpInvoker)).ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler());
    }

    private static string GetHost(WebApplication app)
    {
        return new Uri(app.Urls.Single()).Authority;
    }

    /// <summary>
    /// Sends the request as it is written, and returns the status line Kestrel answers with.
    /// </summary>
    private static async Task<(int Status, string StatusLine)> SendDirectlyAsync(WebApplication app, string request)
    {
        var address = new Uri(app.Urls.Single());
        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync() ?? throw new InvalidOperationException("Kestrel closed the connection without an answer.");
        return (int.Parse(statusLine.Split(' ', 3)[1]), statusLine);
    }

    private static async Task<(bool IsError, string Text)> CallToolAsync(WebApplication app, string toolName, Dictionary<string, object?> arguments)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(app.Urls.Single() + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

        return await CallToolAsync(client, toolName, arguments);
    }

    private static async Task<(bool IsError, string Text)> CallToolAsync(McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(toolName, arguments);
        return (result.IsError == true, ((TextContentBlock)result.Content[0]).Text);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"The tool sent {request.RequestUri} over HTTP.");
        }
    }
}

[McpServerToolType]
public static class ServerLimitProbeTools
{
    /// <summary>
    /// Sends the headers <c>X-Pad-1</c> to <c>X-Pad-{count}</c>, as a generated tool of as many <c>[FromHeader]</c>
    /// parameters does.
    /// </summary>
    [McpServerTool(Name = "limits_headers")]
    public static Task<CallToolResult> SendHeaders(IMcpHttpInvoker invoker, string route, int count, CancellationToken cancellationToken)
    {
        var headers = Enumerable.Range(1, count).Select(x => new KeyValuePair<string, string>($"X-Pad-{x}", "1")).ToList();

        return new McpHttpRequest(HttpMethod.Get, route) { Headers = headers }.SendAsync(invoker, cancellationToken);
    }
}

[ApiController]
[Route("api/server-limits")]
public sealed class ServerLimitsController : ControllerBase
{
    private const string Served = "served";

    [HttpGet("{**rest}")]
    public IActionResult Serve()
    {
        return Ok(Served);
    }

    [HttpPost("body")]
    public async Task<IActionResult> ReadBody()
    {
        var body = await new StreamReader(Request.Body).ReadToEndAsync();
        return Ok($"{Served} {body.Length}");
    }
}
