using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class InProcessApiPipelineTests
{
    private const string ItemsUrl = "https://api.example.com/api/items";

    [Fact]
    public void IsCaptured_IsFalse_UntilTheAppsPipelineIsBuilt()
    {
        var services = CreateServices();
        var pipeline = CreatePipeline(services);
        var builder = new PipelineCapturingApplicationBuilderFactory(new ApplicationBuilderFactory(services), pipeline)
            .CreateBuilder(new FeatureCollection());
        builder.Run(context => Task.CompletedTask);

        Assert.False(pipeline.IsCaptured);
        builder.Build();
        Assert.True(pipeline.IsCaptured);
    }

    [Fact]
    public async Task SendAsync_RunsTheRequest_ThroughEveryMiddlewareTheAppAdded()
    {
        var pipeline = Build(app =>
        {
            app.Use((context, next) => Trail(context, "first", next));
            app.Use((context, next) => Trail(context, "second", next));
            app.Run(context => context.Response.WriteAsync(string.Join(",", (List<string>)context.Items["trail"]!)));
        });

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal("first,second", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_HandsTheAppTheRequest_AsTheMessageDescribesIt()
    {
        var pipeline = Build(app => app.Run(async context =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
            {
                ["method"] = context.Request.Method,
                ["scheme"] = context.Request.Scheme,
                ["host"] = context.Request.Host.Value,
                ["pathBase"] = context.Request.PathBase.Value,
                ["path"] = context.Request.Path.Value,
                ["query"] = context.Request.QueryString.Value,
                ["protocol"] = context.Request.Protocol,
                ["authorization"] = context.Request.Headers.Authorization.ToString(),
                ["custom"] = context.Request.Headers["X-Custom"].ToString(),
                ["contentType"] = context.Request.ContentType,
                ["contentLength"] = context.Request.ContentLength,
                ["body"] = body
            });
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl + "?x=1&y=two")
        {
            Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer token-1");
        request.Headers.Add("X-Custom", "custom-1");

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("POST", seen.GetProperty("method").GetString());
        Assert.Equal("https", seen.GetProperty("scheme").GetString());
        Assert.Equal("api.example.com", seen.GetProperty("host").GetString());
        Assert.Equal("", seen.GetProperty("pathBase").GetString());
        Assert.Equal("/api/items", seen.GetProperty("path").GetString());
        Assert.Equal("?x=1&y=two", seen.GetProperty("query").GetString());
        Assert.Equal("HTTP/1.1", seen.GetProperty("protocol").GetString());
        Assert.Equal("Bearer token-1", seen.GetProperty("authorization").GetString());
        Assert.Equal("custom-1", seen.GetProperty("custom").GetString());
        Assert.Equal("application/json; charset=utf-8", seen.GetProperty("contentType").GetString());
        Assert.Equal(7, seen.GetProperty("contentLength").GetInt64());
        Assert.Equal("{\"a\":1}", seen.GetProperty("body").GetString());
    }

    [Fact]
    public async Task SendAsync_ReportsNoBody_ForARequestWithoutContent()
    {
        var pipeline = Build(app => app.Run(context =>
            context.Response.WriteAsync($"{context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody}|{context.Request.ContentLength}")));

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal("False|", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(6, "413")]
    [InlineData(7, "{\"a\":1}")]
    public async Task SendAsync_HoldsTheBody_ToTheLimitTheAppSets_AsAServerDoes(long limit, string expected)
    {
        // [RequestSizeLimit] sets the limit; Kestrel throws a BadHttpRequestException with 413 when a body over it is read.
        var pipeline = Build(app => app.Run(async context =>
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = limit;
            try
            {
                await context.Response.WriteAsync(await new StreamReader(context.Request.Body).ReadToEndAsync());
            }
            catch (BadHttpRequestException exception)
            {
                await context.Response.WriteAsync(exception.StatusCode.ToString());
            }
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl) { Content = new StringContent("{\"a\":1}") };

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_LetsTheAppChangeTheBodyLimit_OnlyBeforeItReadsTheBody()
    {
        var pipeline = Build(app => app.Run(async context =>
        {
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;
            var isReadOnlyBefore = limit.IsReadOnly;
            await context.Request.Body.ReadAsync(new byte[1]);
            await context.Response.WriteAsync($"{isReadOnlyBefore}|{limit.IsReadOnly}");
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl) { Content = new StringContent("{\"a\":1}") };

        using var response = await pipeline.SendAsync(request, caller: null, CancellationToken.None);

        Assert.Equal("False|True", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_GivesTheRequest_TheCallersConnectionAndClientCertificate()
    {
        using var certificate = CreateCertificate();
        var pipeline = Build(app => app.Run(context => context.Response.WriteAsync(
            $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}|" +
            $"{context.Connection.LocalIpAddress}:{context.Connection.LocalPort}|" +
            $"{context.Connection.ClientCertificate?.Thumbprint}")));
        var caller = new DefaultHttpContext();
        caller.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        caller.Connection.RemotePort = 50123;
        caller.Connection.LocalIpAddress = IPAddress.Parse("10.0.0.1");
        caller.Connection.LocalPort = 8080;
        caller.Connection.ClientCertificate = certificate;

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller, CancellationToken.None);

        Assert.Equal($"203.0.113.7:50123|10.0.0.1:8080|{certificate.Thumbprint}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_GivesTheRequest_TheCallersPathBase_AndTheRouteAsItsPath()
    {
        // A path base the app took from X-Forwarded-Prefix is not in the path, so it cannot be handed over in the URL: the
        // app would route /v1/api/items. A UsePathBase middleware passes a path that does not start with it unchanged.
        var pipeline = Build(app => app.Run(context => context.Response.WriteAsync($"{context.Request.PathBase}|{context.Request.Path}")));
        var caller = new DefaultHttpContext();
        caller.Request.PathBase = "/v1";

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller, CancellationToken.None);

        Assert.Equal("/v1|/api/items", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_ReturnsTheStatusHeadersAndBody_TheAppWrote()
    {
        var pipeline = Build(app => app.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers["X-Result"] = "result-1";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("created");
        }));

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Post, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Created", response.ReasonPhrase);
        Assert.Equal(["result-1"], response.Headers.GetValues("X-Result"));
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("created", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendAsync_StartsTheResponse_AtTheFirstWrite_AfterItsOnStartingCallbacks()
    {
        var events = new List<string>();
        var pipeline = Build(app => app.Run(async context =>
        {
            context.Response.OnStarting(() =>
            {
                events.Add("starting");
                context.Response.Headers["X-Started"] = "yes";
                return Task.CompletedTask;
            });
            events.Add($"before write, started={context.Response.HasStarted}");
            await context.Response.WriteAsync("body");
            events.Add($"after write, started={context.Response.HasStarted}");
        }));

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal(["before write, started=False", "starting", "after write, started=True"], events);
        Assert.Equal(["yes"], response.Headers.GetValues("X-Started"));
    }

    [Fact]
    public async Task SendAsync_RunsTheOnStartingCallbacks_OfAResponseWithoutABody()
    {
        var pipeline = Build(app => app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Started"] = "yes";
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        }));

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Delete, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["yes"], response.Headers.GetValues("X-Started"));
    }

    [Fact]
    public async Task SendAsync_RunsTheOnCompletedCallbacks_AndDisposesTheRequestsServices()
    {
        var tracker = new DisposalTracker();
        var completed = false;
        var pipeline = Build(
            app => app.Run(context =>
            {
                context.RequestServices.GetRequiredService<ScopedDisposable>();
                context.Response.OnCompleted(() =>
                {
                    completed = true;
                    return Task.CompletedTask;
                });
                return context.Response.WriteAsync("done");
            }),
            services => services.AddSingleton(tracker).AddScoped<ScopedDisposable>());

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.True(completed);
        Assert.True(tracker.IsDisposed);
    }

    [Fact]
    public async Task SendAsync_AnswersAnUnhandledException_With500AndNothingElse_AndLogsIt()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var pipeline = Build(
            app => app.Run(context =>
            {
                context.Response.Headers["X-Partial"] = "partial";
                throw new InvalidOperationException("boom");
            }),
            loggerProvider: new ListLoggerProvider(logs));

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Partial"));
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Contains(logs, x => x.Level == LogLevel.Error);
    }

    [Fact]
    public async Task SendAsync_Throws_WhenTheAppFailsAfterTheResponseStarted()
    {
        var pipeline = Build(app => app.Run(async context =>
        {
            await context.Response.WriteAsync("partial");
            throw new InvalidOperationException("boom");
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_Throws_WhenTheAppAbortsTheRequest()
    {
        var pipeline = Build(app => app.Run(context =>
        {
            context.Abort();
            return Task.CompletedTask;
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_AbortsTheRequest_WhenTheCallIsCanceled()
    {
        var started = new TaskCompletionSource();
        var pipeline = Build(app => app.Run(async context =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }));
        using var cancellation = new CancellationTokenSource();

        var sending = pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
    }

    [Fact]
    public async Task SendAsync_LeavesTheCallersHttpContext_ToTheCaller()
    {
        // The request's own context is the current one inside the app, but setting it must not clear the caller's: the
        // MCP request still needs its context after the tool's request.
        var accessor = new HttpContextAccessor();
        var isOwnContextInside = false;
        var pipeline = Build(
            app => app.Run(context =>
            {
                isOwnContextInside = ReferenceEquals(accessor.HttpContext, context);
                return Task.CompletedTask;
            }),
            services => services.AddSingleton<IHttpContextAccessor>(accessor));
        var caller = new DefaultHttpContext();
        accessor.HttpContext = caller;

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller, CancellationToken.None);

        Assert.True(isOwnContextInside);
        Assert.Same(caller, accessor.HttpContext);
    }

    [Fact]
    public async Task SendAsync_RunsTheApp_UnderTheCallersActivity()
    {
        string? activityIdInside = null;
        var pipeline = Build(app => app.Run(context =>
        {
            activityIdInside = Activity.Current?.Id;
            return Task.CompletedTask;
        }));
        using var activity = new Activity("tool call").Start();

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Equal(activity.Id, activityIdInside);
    }

    [Fact]
    public async Task SendAsync_LogsInsideTheApp_UnderARequestScope_LikeAServedRequest()
    {
        var scopes = new ScopeRecordingLoggerProvider();
        string? traceIdentifier = null;
        var pipeline = Build(
            app => app.Run(context =>
            {
                traceIdentifier = context.TraceIdentifier;
                context.RequestServices.GetRequiredService<ILogger<InProcessApiPipelineTests>>().LogInformation("inside");
                return Task.CompletedTask;
            }),
            loggerProvider: scopes);

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl + "?page=2"), caller: null, CancellationToken.None);

        var scope = scopes.ScopesByMessage["inside"];
        Assert.Equal("/api/items", scope["RequestPath"]);
        Assert.Equal(traceIdentifier, scope["RequestId"]);
    }

    [Fact]
    public async Task SendAsync_LogsTheRequestsMethodUrlAndStatus()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var pipeline = Build(
            app => app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }),
            loggerProvider: new ListLoggerProvider(logs));

        using var response = await pipeline.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), caller: null, CancellationToken.None);

        Assert.Contains(logs, x => x.Level == LogLevel.Information && x.Message.Contains("GET") && x.Message.Contains(ItemsUrl) && x.Message.Contains("404"));
    }

    private static Task Trail(HttpContext context, string name, RequestDelegate next)
    {
        if (context.Items["trail"] is not List<string> trail)
        {
            context.Items["trail"] = trail = [];
        }

        trail.Add(name);
        return next(context);
    }

    private static InProcessApiPipeline Build(
        Action<IApplicationBuilder> configure,
        Action<IServiceCollection>? configureServices = null,
        ILoggerProvider? loggerProvider = null)
    {
        var services = CreateServices(configureServices, loggerProvider);
        var pipeline = CreatePipeline(services);
        var builder = new PipelineCapturingApplicationBuilderFactory(new ApplicationBuilderFactory(services), pipeline)
            .CreateBuilder(new FeatureCollection());
        configure(builder);
        builder.Build();

        return pipeline;
    }

    private static ServiceProvider CreateServices(Action<IServiceCollection>? configureServices = null, ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider);
            }
        });
        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static InProcessApiPipeline CreatePipeline(IServiceProvider services)
    {
        return new InProcessApiPipeline(new DefaultHttpContextFactory(services), services.GetRequiredService<ILogger<InProcessApiPipeline>>());
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=tool-caller", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class DisposalTracker
    {
        public bool IsDisposed { get; set; }
    }

    private sealed class ScopedDisposable : IDisposable
    {
        private readonly DisposalTracker _tracker;

        public ScopedDisposable(DisposalTracker tracker)
        {
            _tracker = tracker;
        }

        public void Dispose()
        {
            _tracker.IsDisposed = true;
        }
    }

    /// <summary>
    /// Records, for each message, the scope values that were active when it was logged.
    /// </summary>
    private sealed class ScopeRecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopeProvider;

        public Dictionary<string, Dictionary<string, object?>> ScopesByMessage { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            return new ScopeRecordingLogger(this);
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        public void Dispose()
        {
        }

        private sealed class ScopeRecordingLogger : ILogger
        {
            private readonly ScopeRecordingLoggerProvider _provider;

            public ScopeRecordingLogger(ScopeRecordingLoggerProvider provider)
            {
                _provider = provider;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return _provider._scopeProvider?.Push(state);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = new Dictionary<string, object?>();
                _provider._scopeProvider?.ForEachScope((scope, collected) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var (key, value) in pairs)
                        {
                            collected[key] = value;
                        }
                    }
                }, values);

                lock (_provider.ScopesByMessage)
                {
                    _provider.ScopesByMessage[formatter(state, exception)] = values;
                }
            }
        }
    }
}
