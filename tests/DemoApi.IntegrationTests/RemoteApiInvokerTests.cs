using System.Net.Mime;
using System.Text;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// The invoker of the README's "Migrating from 2.x", registered as it says, for tools that called another server through
/// <c>McpTools:BaseUrl</c> with in-process calls turned off: their requests go to that server, with the caller's token.
/// </summary>
public sealed class RemoteApiInvokerTests
{
    private const string CallerToken = "Bearer caller-token";

    [Fact]
    public async Task ATool_CallsTheOtherServer_UnderItsBaseAddress_WithTheCallersToken()
    {
        await using var remote = await StartRemoteAsync();
        await using var app = await StartAppAsync(remote.Urls.Single() + "/v2/");

        var get = await CallToolAsync(app, "parity_get", new Dictionary<string, object?> { ["route"] = "/api/orders/7?x=1" });
        var post = await CallToolAsync(app, "parity_post", new Dictionary<string, object?> { ["route"] = "api/orders", ["body"] = "{\"id\":7}" });
        var form = await CallToolAsync(app, "parity_headers_form", new Dictionary<string, object?>
        {
            ["route"] = "/api/orders/7/notes",
            ["name"] = "Ann",
            ["tags"] = new[] { "a" },
            ["comment"] = "x & y"
        });

        Assert.False(get.IsError, get.Text);
        Assert.Equal("GET /v2/api/orders/7?x=1 Bearer caller-token  ", get.Text);
        Assert.False(post.IsError, post.Text);
        Assert.Equal("POST /v2/api/orders Bearer caller-token application/json; charset=utf-8 {\"id\":7}", post.Text);
        Assert.False(form.IsError, form.Text);
        Assert.Equal("POST /v2/api/orders/7/notes Bearer caller-token application/x-www-form-urlencoded; charset=utf-8 comment=x%20%26%20y Ann", form.Text);
    }

    [Fact]
    public async Task AnErrorStatusOfTheOtherServer_ComesBackAsAnErrorResult()
    {
        await using var remote = await StartRemoteAsync();
        await using var app = await StartAppAsync(remote.Urls.Single() + "/");

        var result = await CallToolAsync(app, "parity_get", new Dictionary<string, object?> { ["route"] = "/missing" });

        Assert.True(result.IsError);
        Assert.Equal("HTTP 404: Not Found - no such route", result.Text);
    }

    /// <summary>
    /// The other server, which answers with the method, path, query, <c>Authorization</c>, content type and body of each
    /// request, and the <c>X-Name</c> header when it has one.
    /// </summary>
    private static async Task<WebApplication> StartRemoteAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var remote = builder.Build();
        remote.Run(async context =>
        {
            if (context.Request.Path == "/missing")
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("no such route");
                return;
            }

            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            var name = context.Request.Headers["X-Name"].ToString();
            await context.Response.WriteAsync(
                $"{context.Request.Method} {context.Request.Path}{context.Request.QueryString} {context.Request.Headers.Authorization} {context.Request.ContentType} {body}" +
                (name.Length > 0 ? " " + name : ""));
        });
        await remote.StartAsync();

        return remote;
    }

    private static async Task<WebApplication> StartAppAsync(string remoteBaseAddress)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpTools(typeof(ParityProbeTools).Assembly, options => options.ArePropertyDescriptionsFromXmlDocs = false);
        builder.Services.AddHttpClient<RemoteApiInvoker>(client => client.BaseAddress = new Uri(remoteBaseAddress));
        builder.Services.AddTransient<IMcpHttpInvoker>(services => services.GetRequiredService<RemoteApiInvoker>());

        var app = builder.Build();
        app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await app.StartAsync();

        return app;
    }

    private static async Task<(bool IsError, string Text)> CallToolAsync(WebApplication app, string toolName, Dictionary<string, object?> arguments)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(app.Urls.Single() + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { [HeaderNames.Authorization] = CallerToken }
            },
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(toolName, arguments);
        return (result.IsError == true, ((TextContentBlock)result.Content[0]).Text);
    }
}

/// <summary>
/// The sample of the README's "Migrating from 2.x", as it stands there, so it is known to compile and work.
/// </summary>
public sealed class RemoteApiInvoker : IMcpHttpRequestInvoker
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RemoteApiInvoker(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _httpContextAccessor = httpContextAccessor;
    }

    public Task<CallToolResult> GetAsync(string route, CancellationToken ct = default) =>
        SendAsync(new McpHttpRequest(HttpMethod.Get, route), ct);

    public Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default) =>
        SendAsync(new McpHttpRequest(HttpMethod.Post, route) { JsonBody = jsonBody }, ct);

    public Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default) =>
        SendAsync(new McpHttpRequest(HttpMethod.Put, route) { JsonBody = jsonBody }, ct);

    public Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default) =>
        SendAsync(new McpHttpRequest(HttpMethod.Patch, route) { JsonBody = jsonBody }, ct);

    public Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default) =>
        SendAsync(new McpHttpRequest(HttpMethod.Delete, route), ct);

    public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<CallToolResult> SendAsync(McpHttpRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(request.Method, request.Route.TrimStart('/'));
        var authorization = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorization))
        {
            message.Headers.TryAddWithoutValidation(HeaderNames.Authorization, authorization);
        }

        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (request.JsonBody is not null)
        {
            message.Content = new StringContent(request.JsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
        }
        else if (request.FormBody is not null)
        {
            message.Content = new StringContent(request.FormBody, Encoding.UTF8, MediaTypeNames.Application.FormUrlEncoded);
        }

        using var response = await _httpClient.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        return new CallToolResult
        {
            IsError = !response.IsSuccessStatusCode,
            Content = { new TextContentBlock { Text = response.IsSuccessStatusCode ? body : $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} - {body}" } }
        };
    }
}
