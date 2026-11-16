using System.Net;
using System.Text;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace Api.ToMcp.Runtime.Tests.Services;

/// <summary>
/// The tool's in-process request under the limits of the server the host runs, here a Kestrel the test gives the host.
/// </summary>
public class InProcessApiPipelineServerLimitsTests
{
    private const string Origin = "https://api.example.com";
    private const string ItemsUrl = Origin + "/api/items";
    private const string Seen = "seen";

    [Theory]
    [InlineData(0, HttpStatusCode.OK, Seen)]
    [InlineData(1, HttpStatusCode.RequestUriTooLong, "")]
    public async Task SendAsync_RefusesARequestLineOverKestrelsLimit_BeforeTheAppSeesIt(int over, HttpStatusCode expectedStatus, string expectedBody)
    {
        // "GET /api/items?q=" + 72 characters + " HTTP/1.1\r\n" is 100 bytes.
        var seen = 0;
        var pipeline = Build(app => app.Run(context => Serve(context, ref seen)), kestrel => kestrel.Limits.MaxRequestLineSize = 100);

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl + "?q=" + new string('q', 72 + over)), caller: null, CancellationToken.None);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
        Assert.Equal(over == 0 ? 1 : 0, seen);
    }

    [Theory]
    [InlineData(0, HttpStatusCode.OK)]
    [InlineData(1, HttpStatusCode.RequestHeaderFieldsTooLarge)]
    public async Task SendAsync_RefusesHeadersOverKestrelsTotalSize_BeforeTheAppSeesThem(int over, HttpStatusCode expectedStatus)
    {
        // "Host: api.example.com\r\n" is 23 bytes, and "X-Pad: " + 68 characters + "\r\n" is 77.
        var seen = 0;
        var pipeline = Build(app => app.Run(context => Serve(context, ref seen)), kestrel => kestrel.Limits.MaxRequestHeadersTotalSize = 100);
        using var request = new HttpRequestMessage(HttpMethod.Get, ItemsUrl);
        request.Headers.Add("X-Pad", new string('p', 68 + over));

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(over == 0 ? 1 : 0, seen);
    }

    [Theory]
    [InlineData(0, HttpStatusCode.OK)]
    [InlineData(1, HttpStatusCode.RequestHeaderFieldsTooLarge)]
    public async Task SendAsync_RefusesMoreHeadersThanKestrelAllows_BeforeTheAppSeesThem(int over, HttpStatusCode expectedStatus)
    {
        // Host, X-A, X-B-0 and the body's Content-Type and Content-Length make Kestrel's 5.
        var seen = 0;
        var pipeline = Build(app => app.Run(context => Serve(context, ref seen)), kestrel => kestrel.Limits.MaxRequestHeaderCount = 5);
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Add("X-A", "1");
        for (var i = 0; i < 1 + over; i++)
        {
            request.Headers.Add($"X-B-{i}", "1");
        }

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(over == 0 ? 1 : 0, seen);
    }

    [Theory]
    [InlineData(100, 1, HttpStatusCode.RequestUriTooLong, "URI Too Long")]
    [InlineData(1, 1000, HttpStatusCode.RequestHeaderFieldsTooLarge, "Request Header Fields Too Large")]
    public async Task SendAsync_AnswersARefusal_WithKestrelsReasonPhraseAndNoBody(int queryLength, int headerLength, HttpStatusCode expectedStatus, string expectedReason)
    {
        var pipeline = Build(app => app.Run(context => context.Response.WriteAsync(Seen)), kestrel =>
        {
            kestrel.Limits.MaxRequestLineSize = 100;
            kestrel.Limits.MaxRequestHeadersTotalSize = 1000;
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, ItemsUrl + "?q=" + new string('q', queryLength));
        request.Headers.Add("X-Pad", new string('p', headerLength));

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedReason, response.ReasonPhrase);
        Assert.Equal(HttpVersion.Version11, response.Version);
        Assert.Same(request, response.RequestMessage);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SendAsync_LogsARefusal_OnlyAtDebug_AndNotAsServed()
    {
        // Kestrel logs its own refusal at Debug, and hosting never sees the request.
        var logs = new List<(LogLevel Level, string Message)>();
        var pipeline = Build(app => app.Run(context => context.Response.WriteAsync(Seen)), kestrel => kestrel.Limits.MaxRequestLineSize = 50, new ListLoggerProvider(logs));

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl + "?q=" + new string('q', 50)), caller: null, CancellationToken.None);

        var log = Assert.Single(logs);
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.StartsWith("Refused the tool's API request GET https://api.example.com/api/items?q=", log.Message);
        Assert.EndsWith(" in process with 414, as the server refuses a request over its limits: Request line too long.", log.Message);
    }

    [Theory]
    [InlineData(31, HttpStatusCode.OK)]
    [InlineData(30, HttpStatusCode.RequestUriTooLong)]
    public async Task SendAsync_CountsTheTarget_PercentEncodedAsSent_WithoutThePathBaseAProxyGaveTheMcpRequest(int limit, HttpStatusCode expectedStatus)
    {
        // "GET /api/a%40b?q=%40 HTTP/1.1\r\n" is 31 bytes; the app decodes the path to "/api/a@b", 2 bytes shorter.
        var pipeline = Build(app => app.Run(context => context.Response.WriteAsync(context.Features.Get<IHttpRequestFeature>()!.RawTarget)), kestrel => kestrel.Limits.MaxRequestLineSize = limit);
        var caller = new DefaultHttpContext();
        caller.Request.PathBase = "/proxy";

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, Origin + "/proxy/api/a%40b?q=%40"), caller, CancellationToken.None);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_GivesTheAppTheTarget_PercentEncodedAsAClientSendsIt()
    {
        var pipeline = Build(app => app.Run(context => context.Response.WriteAsync(
            $"{context.Features.Get<IHttpRequestFeature>()!.RawTarget}|{context.Request.PathBase}|{context.Request.Path}|{context.Request.QueryString}")));
        var caller = new DefaultHttpContext();
        caller.Request.PathBase = "/proxy";

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, Origin + "/proxy/api/a%40b%2Fc?q=%40"), caller, CancellationToken.None);

        Assert.Equal("/api/a%40b%2Fc?q=%40|/proxy|/api/a@b%2Fc|?q=%40", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_RefusesARedirectedRequest_OverKestrelsLimit()
    {
        var pipeline = Build(
            app => app.Run(context =>
            {
                if (context.Request.Path == "/api/items")
                {
                    context.Response.Redirect("/api/target?q=" + new string('q', 100));
                    return Task.CompletedTask;
                }

                return context.Response.WriteAsync(Seen);
            }),
            kestrel => kestrel.Limits.MaxRequestLineSize = 100);

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestUriTooLong, response.StatusCode);
    }

    [Theory]
    [InlineData(6, "413")]
    [InlineData(7, "{\"a\":1}")]
    public async Task SendAsync_HoldsTheBody_ToKestrelsLimit_UnlessTheAppSetsAnother(long limit, string expected)
    {
        var pipeline = Build(
            app => app.Run(async context =>
            {
                try
                {
                    await context.Response.WriteAsync(await new StreamReader(context.Request.Body).ReadToEndAsync());
                }
                catch (BadHttpRequestException exception)
                {
                    await context.Response.WriteAsync(exception.StatusCode.ToString());
                }
            }),
            kestrel => kestrel.Limits.MaxRequestBodySize = limit);
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl) { Content = new StringContent("{\"a\":1}") };

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_StartsTheBodyLimitAtKestrels_WhichTheAppMayLift()
    {
        var pipeline = Build(
            app => app.Run(async context =>
            {
                var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;
                var startingLimit = limit.MaxRequestBodySize;
                limit.MaxRequestBodySize = null;
                await context.Response.WriteAsync($"{startingLimit}|{await new StreamReader(context.Request.Body).ReadToEndAsync()}");
            }),
            kestrel => kestrel.Limits.MaxRequestBodySize = 3);
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl) { Content = new StringContent("{\"a\":1}") };

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal("3|{\"a\":1}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_HoldsTheRequestToNoLimit_UnderAServerItDoesNotKnow()
    {
        var pipeline = Build(app => app.Run(async context =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            await context.Response.WriteAsync($"{context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize}|{context.Request.Headers.Count}|{body.Length}");
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl + "?q=" + new string('q', 20_000)) { Content = new StringContent(new string('b', 40_000)) };
        for (var i = 0; i < 200; i++)
        {
            request.Headers.Add($"X-Pad-{i}", new string('p', 200));
        }

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("|203|40000", await response.Content.ReadAsStringAsync());
    }

    private static Task Serve(HttpContext context, ref int seen)
    {
        Interlocked.Increment(ref seen);
        return context.Response.WriteAsync(Seen);
    }

    /// <summary>
    /// The pipeline the host builds, for a host that runs a Kestrel of the options <paramref name="configureKestrel"/>
    /// sets, or no server it knows without it.
    /// </summary>
    private static InProcessApiPipeline Build(
        Action<IApplicationBuilder> configure,
        Action<KestrelServerOptions>? configureKestrel = null,
        ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider);
            }
        });
        services.AddSingleton(CreateServer(configureKestrel));
        var serviceProvider = services.BuildServiceProvider();

        var pipeline = new InProcessApiPipeline(new DefaultHttpContextFactory(serviceProvider), serviceProvider.GetRequiredService<ILogger<InProcessApiPipeline>>(), InProcessRequestDiagnostics.Create(serviceProvider));
        var builder = new PipelineCapturingApplicationBuilderFactory(new ApplicationBuilderFactory(serviceProvider), pipeline)
            .CreateBuilder(new FeatureCollection());
        configure(builder);
        builder.Build();

        return pipeline;
    }

    private static IServer CreateServer(Action<KestrelServerOptions>? configureKestrel)
    {
        if (configureKestrel is null)
        {
            return Mock.Of<IServer>();
        }

        var options = new KestrelServerOptions();
        configureKestrel(options);

        return new KestrelServer(Microsoft.Extensions.Options.Options.Create(options), Mock.Of<IConnectionListenerFactory>(), NullLoggerFactory.Instance);
    }
}
