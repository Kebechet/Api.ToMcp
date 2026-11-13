using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// Tools that call the API over HTTP, in an app on Kestrel's <c>http://</c> binding behind a reverse proxy that terminates
/// TLS: the app trusts the proxy's forwarded headers and redirects HTTP to HTTPS. The MCP client names another server in
/// <c>Host</c> and in the forwarded headers, none of which may reach the tool's request
/// (https://github.com/Kebechet/Api.ToMcp/issues/48). The invoker's client follows no redirect, so a redirect comes back
/// as an error result, and records every URL it is handed.
/// </summary>
public sealed class HttpApiCallsBehindAProxyTests
{
    private const string ForgedHostName = "internal.invalid";
    private const string ForgedHost = ForgedHostName + ":9443";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";
    private const string CustomForwardedProtoHeader = "X-Forwarded-Scheme";
    private const string CallerToken = "Bearer caller-token";
    private const string Route = "/api/proxy-probe";

    /// <summary>
    /// Marks a request as coming from the proxy at <see cref="ProxyAddress"/> rather than loopback, where the test runs.
    /// </summary>
    private const string ViaProxyHeader = "X-Test-Via-Proxy";

    private static readonly IPAddress ProxyAddress = IPAddress.Parse("10.0.0.1");

    public enum KnownProxies
    {
        /// <summary>The defaults, which trust loopback: a proxy on the same machine.</summary>
        Default,

        /// <summary>Every address trusted, as <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> sets it up.</summary>
        Cleared,

        /// <summary>The proxy's address added to the defaults.</summary>
        ProxyAdded,

        /// <summary>The defaults replaced with the proxy's address and loopback.</summary>
        ProxyAndLoopback,

        /// <summary>The defaults replaced with the proxy's address alone.</summary>
        ProxyOnly
    }

    [Theory]
    [InlineData(KnownProxies.Default)]
    [InlineData(KnownProxies.Cleared)]
    [InlineData(KnownProxies.ProxyAdded)]
    [InlineData(KnownProxies.ProxyAndLoopback)]
    public async Task AToolsRequest_GoesToTheServersOwnAddress_AsHttps_NotToTheHostTheMcpRequestNames(KnownProxies knownProxies)
    {
        var requests = new List<(string Url, string? ForwardedProto)>();
        await using var app = await StartBehindProxyAsync(knownProxies, requests);

        var tool = await CallToolAsync(app, "parity_get", Route, viaProxy: knownProxies != KnownProxies.Default);

        Assert.DoesNotContain(requests, x => x.Url.Contains(ForgedHostName));
        Assert.Equal([(app.Urls.Single() + Route, "https")], requests);
        Assert.False(tool.IsError, tool.Text);
        AssertSeenAsTheServersOwnHttpsRequest(app, tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsRedirected_WhenTheAppTrustsNoForwardedHeadersFromLoopback()
    {
        // The tool's request comes from loopback, so the app takes it for HTTP and redirects it. Add loopback to the
        // known proxies, or set McpTools:BaseUrl.
        var requests = new List<(string Url, string? ForwardedProto)>();
        await using var app = await StartBehindProxyAsync(KnownProxies.ProxyOnly, requests);

        var tool = await CallToolAsync(app, "parity_get", Route, viaProxy: true);

        Assert.Equal([(app.Urls.Single() + Route, "https")], requests);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 307", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_GoesAsHttps_ThroughAProviderThatWrapsTheDefaultOne()
    {
        var requests = new List<(string Url, string? ForwardedProto)>();
        await using var app = await StartBehindProxyAsync(KnownProxies.Default, requests, configureServices: services =>
            services.AddSingleton<ISelfBaseUrlProvider>(serviceProvider => new WrappingBaseUrlProvider(new DefaultBaseUrlProvider(
                serviceProvider.GetRequiredService<IServer>(),
                serviceProvider.GetRequiredService<IConfiguration>(),
                serviceProvider.GetRequiredService<IHttpContextAccessor>()))));

        var tool = await CallToolAsync(app, "parity_get", Route, viaProxy: false);

        Assert.Equal([(app.Urls.Single() + Route, "https")], requests);
        Assert.False(tool.IsError, tool.Text);
        AssertSeenAsTheServersOwnHttpsRequest(app, tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_GoesAsHttps_UnderTheForwardedProtoHeaderNameTheAppReads()
    {
        var requests = new List<(string Url, string? ForwardedProto)>();
        await using var app = await StartBehindProxyAsync(KnownProxies.Default, requests, CustomForwardedProtoHeader);

        var tool = await CallToolAsync(app, "parity_get", Route, viaProxy: false, CustomForwardedProtoHeader);

        Assert.Equal([(app.Urls.Single() + Route, "https")], requests);
        Assert.False(tool.IsError, tool.Text);
        AssertSeenAsTheServersOwnHttpsRequest(app, tool.Text);
    }

    private static void AssertSeenAsTheServersOwnHttpsRequest(WebApplication app, string echoed)
    {
        // The client's X-Forwarded-For, X-Forwarded-Host and X-Forwarded-Prefix, which the app reads from loopback, would
        // set these if the tool's request passed them on.
        using var seen = JsonDocument.Parse(echoed);
        Assert.Equal("https", seen.RootElement.GetProperty("scheme").GetString());
        Assert.Equal(new Uri(app.Urls.Single()).Authority, seen.RootElement.GetProperty("host").GetString());
        Assert.Equal("", seen.RootElement.GetProperty("pathBase").GetString());
        Assert.Equal(Route, seen.RootElement.GetProperty("path").GetString());
        Assert.Equal(IPAddress.Loopback.ToString(), seen.RootElement.GetProperty("remoteIpAddress").GetString());
        Assert.Equal(CallerToken, seen.RootElement.GetProperty("authorization").GetString());
    }

    private static Task<WebApplication> StartBehindProxyAsync(
        KnownProxies knownProxies,
        List<(string Url, string? ForwardedProto)> requests,
        string forwardedProtoHeader = ForwardedProtoHeader,
        Action<IServiceCollection>? configureServices = null)
    {
        return StartAsync(requests, forwardedProtoHeader, behindProxy: true, configureServices: services =>
        {
            services.Configure<McpScopeOptions>(options => options.AreApiCallsInProcess = false);
            services.AddHttpsRedirection(options => options.HttpsPort = 443);
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.All;
                options.ForwardedProtoHeaderName = forwardedProtoHeader;
                ConfigureKnownProxies(options, knownProxies);
            });
            configureServices?.Invoke(services);
        });
    }

    private static async Task<WebApplication> StartAsync(
        List<(string Url, string? ForwardedProto)> requests,
        string forwardedProtoHeader = ForwardedProtoHeader,
        bool behindProxy = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ProxyProbeController).Assembly);
        builder.Services.AddMcpTools(typeof(ParityProbeTools).Assembly);
        builder.Services.AddHttpClient(nameof(IMcpHttpInvoker))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler(() => new RecordingHandler(requests, forwardedProtoHeader));
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        if (behindProxy)
        {
            app.Use((context, next) =>
            {
                if (context.Request.Headers.ContainsKey(ViaProxyHeader))
                {
                    context.Connection.RemoteIpAddress = ProxyAddress;
                }

                return next(context);
            });
            app.UseForwardedHeaders();
            app.UseHttpsRedirection();
        }

        app.UseMcpLoopPrevention();
        app.MapControllers();
        app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await app.StartAsync();

        return app;
    }

    private static void ConfigureKnownProxies(ForwardedHeadersOptions options, KnownProxies knownProxies)
    {
        if (knownProxies == KnownProxies.Default)
        {
            return;
        }

        if (knownProxies != KnownProxies.ProxyAdded)
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }

        if (knownProxies == KnownProxies.Cleared)
        {
            return;
        }

        options.KnownProxies.Add(ProxyAddress);
        if (knownProxies == KnownProxies.ProxyAndLoopback)
        {
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        }
    }

    private static async Task<(bool IsError, string Text)> CallToolAsync(
        WebApplication app,
        string toolName,
        string route,
        bool viaProxy,
        string forwardedProtoHeader = ForwardedProtoHeader,
        string? body = null)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(app.Urls.Single() + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            new HttpClient(new ProxiedRequestHandler(viaProxy, forwardedProtoHeader) { InnerHandler = new HttpClientHandler() }),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
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
    /// What the proxy passes on: the client's <c>Host</c> and forwarded headers, here naming another server, address and
    /// path base, the scheme the client used, and its token.
    /// </summary>
    private sealed class ProxiedRequestHandler : DelegatingHandler
    {
        private readonly bool _viaProxy;
        private readonly string _forwardedProtoHeader;

        public ProxiedRequestHandler(bool viaProxy, string forwardedProtoHeader)
        {
            _viaProxy = viaProxy;
            _forwardedProtoHeader = forwardedProtoHeader;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = ForgedHost;
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(CallerToken);
            request.Headers.Add(_forwardedProtoHeader, "https");
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
            request.Headers.Add("X-Forwarded-Host", ForgedHostName);
            request.Headers.Add("X-Forwarded-Prefix", "/evil");
            if (_viaProxy)
            {
                request.Headers.Add(ViaProxyHeader, "true");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingHandler : DelegatingHandler
    {
        private readonly List<(string Url, string? ForwardedProto)> _requests;
        private readonly string _forwardedProtoHeader;

        public RecordingHandler(List<(string Url, string? ForwardedProto)> requests, string forwardedProtoHeader)
        {
            _requests = requests;
            _forwardedProtoHeader = forwardedProtoHeader;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwardedProto = request.Headers.TryGetValues(_forwardedProtoHeader, out var values) ? string.Join(",", values) : null;
            lock (_requests)
            {
                _requests.Add((request.RequestUri!.ToString(), forwardedProto));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class WrappingBaseUrlProvider : ISelfBaseUrlProvider
    {
        private readonly DefaultBaseUrlProvider _inner;

        public WrappingBaseUrlProvider(DefaultBaseUrlProvider inner)
        {
            _inner = inner;
        }

        public string GetBaseUrl()
        {
            return _inner.GetBaseUrl();
        }
    }
}

[ApiController]
[Route("api/proxy-probe")]
public sealed class ProxyProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            scheme = Request.Scheme,
            host = Request.Host.Value,
            pathBase = Request.PathBase.Value,
            path = Request.Path.Value,
            remoteIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            authorization = Request.Headers.Authorization.ToString()
        });
    }
}
