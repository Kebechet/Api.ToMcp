using System.Net;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
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
/// Tools that call the API over HTTP, in an app on Kestrel's <c>http://</c> binding behind a reverse proxy that terminates
/// TLS: the app trusts the proxy's <c>X-Forwarded-Proto</c> and redirects HTTP to HTTPS. The MCP client names another
/// server in <c>Host</c>, which the tool's request must not reach
/// (https://github.com/Kebechet/Api.ToMcp/issues/48). The invoker's client follows no redirect, so a redirect comes back
/// as an error result, and records every URL it is handed.
/// </summary>
public sealed class HttpApiCallsBehindAProxyTests
{
    private const string ForgedHostName = "internal.invalid";
    private const string ForgedHost = ForgedHostName + ":9443";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";
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
        await using var app = await StartAsync(knownProxies, requests);

        var tool = await CallToolAsync(app, viaProxy: knownProxies != KnownProxies.Default);

        Assert.DoesNotContain(requests, x => x.Url.Contains(ForgedHostName));
        Assert.Equal([(app.Urls.Single() + Route, "https")], requests);
        Assert.False(tool.IsError, tool.Text);
        Assert.Equal("{\"scheme\":\"https\",\"path\":\"/api/proxy-probe\"}", tool.Text);
    }

    [Fact]
    public async Task AToolsRequest_IsRedirected_WhenTheAppTrustsNoForwardedHeadersFromLoopback()
    {
        // The tool's request comes from loopback, so the app takes it for HTTP and redirects it. Add loopback to the
        // known proxies, or set McpTools:BaseUrl.
        var requests = new List<(string Url, string? ForwardedProto)>();
        await using var app = await StartAsync(KnownProxies.ProxyOnly, requests);

        var tool = await CallToolAsync(app, viaProxy: true);

        Assert.Equal([(app.Urls.Single() + Route, "https")], requests);
        Assert.True(tool.IsError);
        Assert.StartsWith("HTTP 307", tool.Text);
    }

    private static async Task<WebApplication> StartAsync(KnownProxies knownProxies, List<(string Url, string? ForwardedProto)> requests)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ProxyProbeController).Assembly);
        builder.Services.AddMcpTools(typeof(ParityProbeTools).Assembly, options => options.AreApiCallsInProcess = false);
        builder.Services.AddHttpClient(nameof(IMcpHttpInvoker))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler(() => new RecordingHandler(requests));
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            ConfigureKnownProxies(options, knownProxies);
        });

        var app = builder.Build();
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

    private static async Task<(bool IsError, string Text)> CallToolAsync(WebApplication app, bool viaProxy)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(app.Urls.Single() + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            new HttpClient(new ProxiedRequestHandler(viaProxy) { InnerHandler = new HttpClientHandler() }),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("parity_get", new Dictionary<string, object?> { ["route"] = Route });
        return (result.IsError == true, ((TextContentBlock)result.Content[0]).Text);
    }

    /// <summary>
    /// What the proxy passes on: the client's <c>Host</c>, here one that names another server, and the scheme the client
    /// used.
    /// </summary>
    private sealed class ProxiedRequestHandler : DelegatingHandler
    {
        private readonly bool _viaProxy;

        public ProxiedRequestHandler(bool viaProxy)
        {
            _viaProxy = viaProxy;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = ForgedHost;
            request.Headers.Add(ForwardedProtoHeader, "https");
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

        public RecordingHandler(List<(string Url, string? ForwardedProto)> requests)
        {
            _requests = requests;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwardedProto = request.Headers.TryGetValues(ForwardedProtoHeader, out var values) ? string.Join(",", values) : null;
            lock (_requests)
            {
                _requests.Add((request.RequestUri!.ToString(), forwardedProto));
            }

            return base.SendAsync(request, cancellationToken);
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
        return Ok(new { scheme = Request.Scheme, path = Request.Path.Value });
    }
}
