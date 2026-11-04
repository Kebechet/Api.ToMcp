using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// A tool's API request against the same request sent straight to Kestrel, in an app behind a TLS-terminating proxy
/// whose pipeline has a middleware of every kind: one a startup filter adds, forwarded headers, an exception handler,
/// HTTPS redirection, a branch, authentication, authorization and an MVC filter. Every middleware records itself, and the
/// API answers with what it saw. The invoker's HTTP client refuses every request, so a tool call that succeeds was served
/// in process.
/// </summary>
public sealed class InProcessPipelineParityTests : IAsyncLifetime
{
    private const string Token = "Bearer alice-token";
    private const string LoopPreventionRefusal = "MCP endpoints cannot be called internally to prevent loops.";
    private const string UsePathBasePrefix = "/v2";

    private WebApplication _app = null!;
    private string _address = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration["AllowedHosts"] = "127.0.0.1";
        builder.Services.AddControllers(options => options.Filters.Add<RecordingActionFilter>())
            .AddApplicationPart(typeof(ParityProbeController).Assembly);
        builder.Services.AddProblemDetails();
        builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedPrefix);
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
        builder.Services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddTransient<IStartupFilter, RecordingStartupFilter>();
        builder.Services.AddMcpTools(typeof(ParityProbeTools).Assembly);
        builder.Services.AddHttpClient(nameof(IMcpHttpInvoker)).ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler());

        _app = builder.Build();
        _app.UsePathBase(UsePathBasePrefix);
        _app.Use(Record("first"));
        _app.UseForwardedHeaders();
        _app.Use(Record("after forwarded headers"));
        _app.UseExceptionHandler();
        _app.UseHttpsRedirection();
        _app.Use(Record("after https redirection"));
        _app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), branch => branch.Use(Record("api branch")));
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.Use(Record("after authorization"));
        _app.UseMcpLoopPrevention();
        _app.MapControllers();
        _app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await _app.StartAsync();

        _address = _app.Urls.Single();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/api/parity/echo/plain")]
    [InlineData("/api/parity/echo/a%20b?q=1&q=two")]
    [InlineData("/api/parity/echo/caf%C3%A9?q=x%2By%20z&other=%26")]
    [InlineData("/api/parity/echo/a%2Fb")]
    public async Task AToolsRequest_PassesTheSameMiddleware_AndReachesTheApiAsTheSameRequest_AsOneSentToTheServer(string route)
    {
        var direct = await SendDirectlyAsync(HttpMethod.Get, route);
        var tool = await CallToolAsync("parity_get", route);

        Assert.Equal(HttpStatusCode.OK, direct.Status);
        Assert.False(tool.IsError, tool.Text);
        Assert.Equal(direct.Body, tool.Text);
        Assert.Contains("\"trail\":[\"startup filter\",\"first\",\"after forwarded headers\",\"after https redirection\",\"api branch\",\"after authorization\",\"mvc filter\"]", tool.Text);
    }

    [Theory]
    [InlineData("/v1", "")]
    [InlineData(null, UsePathBasePrefix)]
    public async Task AToolsRequest_GetsThePathBase_OfTheMcpRequest_AsOneSentToTheServer(string? forwardedPrefix, string pathPrefix)
    {
        // A proxy that strips /v1 sends it as X-Forwarded-Prefix; UsePathBase takes /v2 from the path. The route a tool
        // sends holds neither, as the generator builds it from the controller's templates.
        const string route = "/api/parity/echo/plain";

        var direct = await SendDirectlyAsync(HttpMethod.Get, pathPrefix + route, forwardedPrefix: forwardedPrefix);
        var tool = await CallToolAsync("parity_get", route, mcpPathPrefix: pathPrefix, forwardedPrefix: forwardedPrefix);

        Assert.Equal(HttpStatusCode.OK, direct.Status);
        Assert.False(tool.IsError, tool.Text);
        Assert.Equal(direct.Body, tool.Text);
        Assert.Contains($"\"pathBase\":\"{forwardedPrefix ?? pathPrefix}\",\"path\":\"{route}\"", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_CarriesItsBody_AsOneSentToTheServer()
    {
        const string route = "/api/parity/echo";
        const string body = "{\"name\":\"Lamp\",\"count\":3}";

        var direct = await SendDirectlyAsync(HttpMethod.Post, route, body);
        var tool = await CallToolAsync("parity_post", route, body);

        Assert.Equal(HttpStatusCode.OK, direct.Status);
        Assert.False(tool.IsError, tool.Text);
        Assert.Equal(direct.Body, tool.Text);
        Assert.Contains("\"name\":\"Lamp\"", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsRefusedByAuthorization_AsOneSentToTheServer_WithoutTheCallersToken()
    {
        const string route = "/api/parity/echo/plain";

        var direct = await SendDirectlyAsync(HttpMethod.Get, route, authorization: null);
        var tool = await CallToolAsync("parity_get", route, authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, direct.Status);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 401: Unauthorized", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsRefusedByModelValidation_AsOneSentToTheServer()
    {
        const string route = "/api/parity/echo";
        const string body = "{\"name\":\"far too long a name\",\"count\":3}";

        var direct = await SendDirectlyAsync(HttpMethod.Post, route, body);
        var tool = await CallToolAsync("parity_post", route, body);

        Assert.Equal(HttpStatusCode.BadRequest, direct.Status);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 400: Bad Request", tool.Text);
        Assert.Contains("The field Name must be a string with a maximum length of 10.", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsHeldToTheActionsRequestSizeLimit_AsOneSentToTheServer()
    {
        const string route = "/api/parity/limited";
        const string body = "{\"name\":\"Lamp\",\"count\":3}";

        var direct = await SendDirectlyAsync(HttpMethod.Post, route, body);
        var tool = await CallToolAsync("parity_post", route, body);

        // Kestrel refuses the body with a 413 BadHttpRequestException as it is read, which this app's exception handler
        // answers with a 500.
        Assert.Equal(HttpStatusCode.InternalServerError, direct.Status);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 500: Internal Server Error", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsAnsweredByTheExceptionHandler_AsOneSentToTheServer()
    {
        const string route = "/api/parity/boom";

        var direct = await SendDirectlyAsync(HttpMethod.Get, route);
        var tool = await CallToolAsync("parity_get", route);

        Assert.Equal(HttpStatusCode.InternalServerError, direct.Status);
        Assert.Contains("An error occurred while processing your request.", direct.Body);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 500: Internal Server Error", tool.Text);
        Assert.Contains("An error occurred while processing your request.", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_ToTheMcpEndpoint_IsRefusedByLoopPrevention()
    {
        var tool = await CallToolAsync("parity_post", "/mcp", "{}");

        Assert.True(tool.IsError);
        Assert.Contains(LoopPreventionRefusal, tool.Text);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendDirectlyAsync(
        HttpMethod method,
        string route,
        string? body = null,
        string? authorization = Token,
        string? forwardedPrefix = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(method, _address + route);
        SetProxyHeaders(request.Headers, authorization);
        if (forwardedPrefix is not null)
        {
            request.Headers.Add("X-Forwarded-Prefix", forwardedPrefix);
        }
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(bool IsError, string Text)> CallToolAsync(
        string toolName,
        string route,
        string? body = null,
        string? authorization = Token,
        string mcpPathPrefix = "",
        string? forwardedPrefix = null)
    {
        var headers = new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" };
        if (authorization is not null)
        {
            headers["Authorization"] = authorization;
        }

        if (forwardedPrefix is not null)
        {
            headers["X-Forwarded-Prefix"] = forwardedPrefix;
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_address + mcpPathPrefix + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers
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

    /// <summary>
    /// What a TLS-terminating proxy sends along with the client's request.
    /// </summary>
    private static void SetProxyHeaders(System.Net.Http.Headers.HttpRequestHeaders headers, string? authorization)
    {
        headers.Add("X-Forwarded-Proto", "https");
        if (authorization is not null)
        {
            headers.TryAddWithoutValidation("Authorization", authorization);
        }
    }

    private static Func<HttpContext, RequestDelegate, Task> Record(string name)
    {
        return (context, next) =>
        {
            ParityTrail.Add(context, name);
            return next(context);
        };
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"The tool sent {request.RequestUri} over HTTP.");
        }
    }

    private sealed class RecordingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(Record("startup filter"));
                next(app);
            };
        }
    }

    private sealed class RecordingActionFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            ParityTrail.Add(context.HttpContext, "mvc filter");
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }

    private sealed class TokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Token";

        public TokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.ToString() != Token)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

internal static class ParityTrail
{
    private const string ItemKey = "parity trail";

    public static void Add(HttpContext context, string name)
    {
        if (context.Items[ItemKey] is not List<string> trail)
        {
            context.Items[ItemKey] = trail = [];
        }

        trail.Add(name);
    }

    public static IReadOnlyList<string> Get(HttpContext context)
    {
        return context.Items[ItemKey] as List<string> ?? [];
    }
}

[McpServerToolType]
public static class ParityProbeTools
{
    [McpServerTool(Name = "parity_get")]
    public static Task<CallToolResult> Get(IMcpHttpInvoker invoker, string route, CancellationToken cancellationToken)
    {
        return invoker.GetAsync(route, cancellationToken);
    }

    [McpServerTool(Name = "parity_post")]
    public static Task<CallToolResult> Post(IMcpHttpInvoker invoker, string route, string body, CancellationToken cancellationToken)
    {
        return invoker.PostAsync(route, body, cancellationToken);
    }
}

public sealed record ParityItem([System.ComponentModel.DataAnnotations.StringLength(10)] string Name, int Count);

[ApiController]
[Route("api/parity")]
public sealed class ParityProbeController : ControllerBase
{
    [HttpGet("echo/{value}")]
    [Authorize]
    public IActionResult Echo(string value)
    {
        return Ok(Describe(value, item: null));
    }

    [HttpPost("echo")]
    [Authorize]
    public IActionResult Echo(ParityItem item)
    {
        return Ok(Describe(value: null, item));
    }

    [HttpPost("limited")]
    [Authorize]
    [RequestSizeLimit(10)]
    public IActionResult Limited(ParityItem item)
    {
        return Ok(Describe(value: null, item));
    }

    [HttpGet("boom")]
    [AllowAnonymous]
    public IActionResult Boom()
    {
        throw new InvalidOperationException("boom");
    }

    private object Describe(string? value, ParityItem? item)
    {
        return new
        {
            trail = ParityTrail.Get(HttpContext),
            scheme = Request.Scheme,
            isHttps = Request.IsHttps,
            host = Request.Host.Value,
            pathBase = Request.PathBase.Value,
            path = Request.Path.Value,
            query = Request.Query.ToDictionary(x => x.Key, x => x.Value.ToArray()),
            value,
            item,
            user = User.Identity?.Name,
            remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            contentType = Request.ContentType
        };
    }
}
