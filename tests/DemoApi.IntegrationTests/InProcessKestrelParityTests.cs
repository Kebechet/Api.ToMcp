using System.Net;
using System.Text;
using System.Text.Json;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// A tool's API request against the same request sent straight to Kestrel, for what Kestrel and the client decide rather
/// than a middleware: the status of an exception the app lets escape, with no exception handler to answer it, a body left
/// in <c>Response.BodyWriter</c>, synchronous body I/O, a header set after the response started, redirects, and a
/// <c>Map</c> branch. The invoker's HTTP client refuses every request, so a tool call that succeeds was served in process.
/// </summary>
public sealed class InProcessKestrelParityTests
{
    private const string Route = "/api/kestrel-parity";
    private const string LateHeaderRoute = Route + "/late-header";
    private const string MapPrefix = "/sub";

    [Fact]
    public async Task AToolsRequest_GetsTheStatusOfAnEscapedRequestSizeLimitRefusal_AsOneSentToTheServer()
    {
        await using var app = await StartAsync();
        const string body = "{\"name\":\"Lamp\",\"count\":3}";

        var direct = await SendDirectlyAsync(app, HttpMethod.Post, Route + "/limited", body);
        var tool = await CallToolAsync(app, "parity_post", Route + "/limited", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, direct.Status);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 413", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_GetsTheStatusOfAnEscapedBadHttpRequestException_AsOneSentToTheServer()
    {
        await using var app = await StartAsync();

        var direct = await SendDirectlyAsync(app, HttpMethod.Get, Route + "/bad-request");
        var tool = await CallToolAsync(app, "parity_get", Route + "/bad-request");

        Assert.Equal(HttpStatusCode.NotFound, direct.Status);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 404: Not Found", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_GetsWhatTheAppLeftInTheBodyWriter_AsOneSentToTheServer()
    {
        await using var app = await StartAsync();

        var direct = await SendDirectlyAsync(app, HttpMethod.Get, Route + "/body-writer");
        var tool = await CallToolAsync(app, "parity_get", Route + "/body-writer");

        Assert.Equal(HttpStatusCode.OK, direct.Status);
        Assert.False(tool.IsError, tool.Text);
        Assert.Equal(direct.Body, tool.Text);
        Assert.Equal("{\"via\":\"body writer\"}", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsRefusedSynchronousBodyIO_AsOneSentToTheServer()
    {
        await using var app = await StartAsync();

        var direct = await SendDirectlyAsync(app, HttpMethod.Post, Route + "/sync-read", "{\"a\":1}");
        var tool = await CallToolAsync(app, "parity_post", Route + "/sync-read", "{\"a\":1}");

        Assert.Equal(HttpStatusCode.InternalServerError, direct.Status);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 500", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_FailsWhenAMiddlewareSetsAHeaderAfterTheResponseStarted_AsOneSentToTheServer()
    {
        var refusals = 0;
        await using var app = await StartAsync(app => app.Use(async (context, next) =>
        {
            await next(context);
            if (context.Request.Path != LateHeaderRoute)
            {
                return;
            }

            try
            {
                context.Response.Headers["X-Late"] = "late";
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref refusals);
                throw;
            }
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => SendDirectlyAsync(app, HttpMethod.Get, LateHeaderRoute));
        var tool = await CallToolAsync(app, "parity_get", LateHeaderRoute);

        Assert.True(tool.IsError);
        Assert.Equal(2, refusals);
    }

    [Theory]
    [InlineData("parity_get", "/redirect", null)]
    [InlineData("parity_post", "/see-other", "{\"a\":1}")]
    [InlineData("parity_post", "/temporary-redirect", "{\"a\":1}")]
    public async Task AToolsRequest_FollowsARedirectIntoTheApp_AsAClientOfTheServerDoes(string toolName, string path, string? body)
    {
        await using var app = await StartAsync();
        var method = body is null ? HttpMethod.Get : HttpMethod.Post;

        var direct = await SendDirectlyAsync(app, method, Route + path, body);
        var tool = await CallToolAsync(app, toolName, Route + path, body);

        Assert.Equal(HttpStatusCode.OK, direct.Status);
        Assert.False(tool.IsError, tool.Text);
        Assert.Equal(direct.Body, tool.Text);
        Assert.Contains("\"path\":\"/api/kestrel-parity/target\"", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_ReturnsARedirectToAnotherOrigin_AsItIs()
    {
        await using var app = await StartAsync();

        var tool = await CallToolAsync(app, "parity_get", Route + "/elsewhere");

        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 302: Found", tool.Text);
    }

    [Theory]
    [InlineData(true, "/headers-form")]
    [InlineData(false, "/headers-form")]
    [InlineData(true, "/form-redirect")]
    [InlineData(false, "/form-redirect")]
    public async Task AToolsHeadersAndForm_ReachTheAction_AsThoseOfARequestSentToTheServer(bool areApiCallsInProcess, string path)
    {
        // Kestrel drops the spaces around a header value and HttpClient sends a header's values as one line, which the
        // in-process request has to match; a 307 keeps the method, the form and the headers.
        await using var app = await StartAsync(areApiCallsInProcess: areApiCallsInProcess);
        using var client = new HttpClient();
        using var direct = new HttpRequestMessage(HttpMethod.Post, app.Urls.Single() + Route + "/headers-form")
        {
            Content = new FormUrlEncodedContent([new("comment", "x & y = 100%")])
        };
        direct.Headers.Add("X-Name", "padded");
        direct.Headers.Add("X-Tags", ["sp", "\"a, b\""]);
        using var directResponse = await client.SendAsync(direct);

        var tool = await CallToolAsync(app.Urls.Single(), "parity_headers_form", new Dictionary<string, object?>
        {
            ["route"] = Route + path,
            ["name"] = "  padded \t",
            ["tags"] = new[] { " sp ", "a, b" },
            ["comment"] = "x & y = 100%"
        });

        var directBody = await directResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, directResponse.StatusCode);
        Assert.False(tool.IsError, tool.Text);
        Assert.Equal("{\"name\":\"padded\",\"tags\":[\"sp\",\"a, b\"],\"comment\":\"x & y = 100%\"}", directBody);
        Assert.Equal(directBody, tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_ReachesTheMapBranchOfItsMcpRequest_AsOneSentToTheServer()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureServices(ConfigureServices);
                web.Configure(app => app.Map(MapPrefix, branch =>
                {
                    branch.UseRouting();
                    branch.UseMcpLoopPrevention();
                    branch.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                        endpoints.MapMcpEndpoint("mcp", allowAnonymous: true);
                    });
                }));
            })
            .Build();
        await host.StartAsync();
        try
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            var direct = await SendDirectlyAsync(address, HttpMethod.Get, MapPrefix + Route + "/target");
            var tool = await CallToolAsync(address + MapPrefix, "parity_get", Route + "/target");

            Assert.Equal(HttpStatusCode.OK, direct.Status);
            Assert.False(tool.IsError, tool.Text);
            Assert.Equal(direct.Body, tool.Text);
            Assert.Contains($"\"pathBase\":\"{MapPrefix}\",\"path\":\"{Route}/target\"", tool.Text);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication>? configure = null, bool areApiCallsInProcess = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        ConfigureServices(builder.Services, areApiCallsInProcess);

        var app = builder.Build();
        configure?.Invoke(app);
        app.UseMcpLoopPrevention();
        app.MapControllers();
        app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await app.StartAsync();

        return app;
    }

    /// <param name="areApiCallsInProcess">
    /// Whether the tools call the API in process, with an HTTP client that refuses every request, or over HTTP to the
    /// server's own address.
    /// </param>
    private static void ConfigureServices(IServiceCollection services, bool areApiCallsInProcess)
    {
        services.AddControllers().AddApplicationPart(typeof(KestrelParityController).Assembly);
        if (areApiCallsInProcess)
        {
            services.AddMcpTools(typeof(ParityProbeTools).Assembly);
            services.AddHttpClient(nameof(IMcpHttpInvoker)).ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler());
            return;
        }

        services.AddMcpTools(typeof(ParityProbeTools).Assembly, options => options.AreApiCallsInProcess = false);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        ConfigureServices(services, areApiCallsInProcess: true);
    }

    private static Task<(HttpStatusCode Status, string Body)> SendDirectlyAsync(WebApplication app, HttpMethod method, string route, string? body = null)
    {
        return SendDirectlyAsync(app.Urls.Single(), method, route, body);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendDirectlyAsync(string address, HttpMethod method, string route, string? body = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(method, address + route);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Task<(bool IsError, string Text)> CallToolAsync(WebApplication app, string toolName, string route, string? body = null)
    {
        return CallToolAsync(app.Urls.Single(), toolName, route, body);
    }

    private static async Task<(bool IsError, string Text)> CallToolAsync(string address, string toolName, string route, string? body = null)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(address + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

        var arguments = new Dictionary<string, object?> { ["route"] = route };
        if (body is not null)
        {
            arguments["body"] = body;
        }

        var result = await client.CallToolAsync(toolName, arguments);
        return (result.IsError == true, ((TextContentBlock)result.Content[0]).Text);
    }

    private static async Task<(bool IsError, string Text)> CallToolAsync(string address, string toolName, Dictionary<string, object?> arguments)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(address + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

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

[ApiController]
[Route("api/kestrel-parity")]
public sealed class KestrelParityController : ControllerBase
{
    private const string TargetPath = "/api/kestrel-parity/target";

    [HttpPost("limited")]
    [RequestSizeLimit(10)]
    public IActionResult Limited(ParityItem item)
    {
        return Ok(item);
    }

    [HttpGet("bad-request")]
    public IActionResult ThrowBadRequest()
    {
        throw new BadHttpRequestException("No such tenant.", StatusCodes.Status404NotFound);
    }

    [HttpGet("body-writer")]
    public async Task WriteThroughTheBodyWriter()
    {
        // Utf8JsonWriter advances the writer and never flushes it, so the server has to.
        Response.ContentType = "application/json";
        await Response.StartAsync();
        using var writer = new Utf8JsonWriter(Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("via", "body writer");
        writer.WriteEndObject();
    }

    [HttpPost("sync-read")]
    public IActionResult ReadSynchronously()
    {
        using var reader = new StreamReader(Request.Body);
        return Ok(new { length = reader.ReadToEnd().Length });
    }

    [HttpGet("late-header")]
    public IActionResult LateHeader()
    {
        return Ok(new { ok = true });
    }

    [HttpGet("redirect")]
    public IActionResult RedirectToTarget()
    {
        return Redirect(TargetPath);
    }

    [HttpPost("see-other")]
    public IActionResult SeeOther()
    {
        Response.Headers.Location = TargetPath;
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    [HttpPost("temporary-redirect")]
    public IActionResult TemporaryRedirect()
    {
        return RedirectPreserveMethod(TargetPath);
    }

    [HttpGet("elsewhere")]
    public IActionResult RedirectElsewhere()
    {
        return Redirect("http://elsewhere.invalid" + TargetPath);
    }

    [HttpPost("headers-form")]
    public IActionResult HeadersAndForm([FromHeader(Name = "X-Name")] string? name, [FromHeader(Name = "X-Tags")] string[]? tags, [FromForm] string? comment)
    {
        return Ok(new { name, tags, comment });
    }

    [HttpPost("form-redirect")]
    public IActionResult FormRedirect()
    {
        return RedirectPreserveMethod("/api/kestrel-parity/headers-form");
    }

    [HttpGet("target")]
    [HttpPost("target")]
    public async Task<IActionResult> Target()
    {
        var body = await new StreamReader(Request.Body).ReadToEndAsync();
        return Ok(new { method = Request.Method, pathBase = Request.PathBase.Value, path = Request.Path.Value, body });
    }
}
