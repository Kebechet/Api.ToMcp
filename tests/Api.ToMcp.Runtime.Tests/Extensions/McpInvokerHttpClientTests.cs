using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
using Api.ToMcp.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Moq;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Extensions;

/// <summary>
/// The invoker's named <see cref="HttpClient"/>, as <c>AddMcpTools</c> registers it and an app configures it: every call
/// goes through it, and is served in process after every handler the app gives it. The app sets a primary handler that
/// records each request it gets, which <c>AddMcpTools</c> replaces with one that refuses every request.
/// </summary>
public class McpInvokerHttpClientTests
{
    private const string ClientName = nameof(IMcpHttpInvoker);
    private const string CallerToken = "Bearer caller-token";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string TenantHeader = "X-Tenant";
    private const string ClientDefaultsHeader = "X-Client-Defaults";
    private const string LoopPreventionHeader = McpLoopPreventionMiddleware.LoopPreventionHeader;

    private static readonly Assembly ToolAssembly = typeof(StaticParityTools).Assembly;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly List<string> _primaryRequests = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAppsHandlers_AndTheClientsDefaultHeaders_ReachTheApiInProcess_WhereverTheAppConfiguresTheClient(bool isConfiguredBeforeAddMcpTools)
    {
        using var provider = BuildProvider(
            services =>
            {
                services.ConfigureHttpClientDefaults(defaults => defaults.AddHttpMessageHandler(() => new HeaderSettingHandler(ClientDefaultsHeader, "every client")));
                services.AddHttpClient(ClientName)
                    .AddHttpMessageHandler(() => new HeaderSettingHandler(ApiKeyHeader, "secret"))
                    .ConfigureHttpClient(client => client.DefaultRequestHeaders.Add(TenantHeader, "t1"));
            },
            isConfiguredBeforeAddMcpTools);
        BuildEchoPipeline(provider);

        var result = await CallAsync(provider);

        var seen = Echoed(result);
        Assert.Equal("secret", seen.GetProperty("apiKey").GetString());
        Assert.Equal("every client", seen.GetProperty("clientDefaults").GetString());
        Assert.Equal("t1", seen.GetProperty("tenant").GetString());
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public async Task ADefaultHeader_DoesNotReplaceAHeaderTheInvokerSets()
    {
        using var provider = BuildProvider(services => services.AddHttpClient(ClientName).ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "default-token");
            client.DefaultRequestHeaders.Add(LoopPreventionHeader, "false");
        }));
        BuildEchoPipeline(provider);

        var result = await CallAsync(provider);

        var seen = Echoed(result);
        Assert.Equal(CallerToken, seen.GetProperty("authorization").GetString());
        Assert.Equal("true", seen.GetProperty("internalCall").GetString());
    }

    [Fact]
    public async Task ADefaultAuthorization_IsSent_WhenTheMcpRequestHasNone()
    {
        using var provider = BuildProvider(services => services.AddHttpClient(ClientName)
            .ConfigureHttpClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "default-token")));
        BuildEchoPipeline(provider);
        var caller = CreateCaller();
        caller.Request.Headers.Authorization = default;

        var result = await CallAsync(provider, caller);

        Assert.Equal("Bearer default-token", Echoed(result).GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task ARetryingHandlerTheAppAdds_SendsAnInProcess503Again_AndTheSecondAttemptsResponseComesBack()
    {
        var attempts = 0;
        using var provider = BuildProvider(services => services.AddHttpClient(ClientName).AddHttpMessageHandler(() => new RetryOn503Handler()));
        BuildPipeline(provider, async context =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            if (attempt == 1)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await context.Response.WriteAsync($"attempt {attempt}: {body}");
        });

        var result = await CallAsync(provider, invoker => invoker.PostAsync("/api/orders", "{\"id\":7}"));

        Assert.False(result.IsError, Text(result));
        Assert.Equal("attempt 2: {\"id\":7}", Text(result));
        Assert.Equal(2, attempts);
        Assert.Empty(_primaryRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheInProcessHandler_SitsBelowEveryOtherHandler_RightAboveAPrimaryHandlerThatRefusesEveryRequest(bool isConfiguredBeforeAddMcpTools)
    {
        using var provider = BuildProvider(
            services =>
            {
                services.ConfigureHttpClientDefaults(defaults => defaults.AddHttpMessageHandler(() => new HeaderSettingHandler(ClientDefaultsHeader, "every client")));
                services.AddHttpClient(ClientName)
                    .AddHttpMessageHandler(() => new HeaderSettingHandler(ApiKeyHeader, "secret"))
                    .AddLogger(_ => new RecordingClientLogger([]));
            },
            isConfiguredBeforeAddMcpTools);

        var (handlers, primaryHandler) = WalkTheChain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName));

        Assert.IsType<InProcessApiHandler>(handlers[^1]);
        Assert.Single(handlers.OfType<InProcessApiHandler>());
        Assert.Equal(2, handlers.OfType<HeaderSettingHandler>().Count());
        Assert.Contains(handlers, x => x is LoggingHttpMessageHandler);
        Assert.IsType<RefusingPrimaryHandler>(primaryHandler);
    }

    [Fact]
    public async Task ThePrimaryHandler_RefusesEveryRequest_SoNoRequestLeavesTheProcess()
    {
        // Without the in-process handler, the request would go over the network to the host the MCP request names, which
        // is the client's to set: https://github.com/Kebechet/Api.ToMcp/issues/48
        using var provider = BuildProvider(_ => { });
        var (_, primaryHandler) = WalkTheChain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName));
        using var client = new HttpClient(primaryHandler, disposeHandler: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://internal.example/api/status"));

        Assert.Equal(
            "The tool's API request GET https://internal.example/api/status reached the primary handler of the invoker's HttpClient, which never sends a request " +
            "over the network: a tool's request runs in process only, through InProcessApiHandler, which is missing from the client's handlers.",
            exception.Message);
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public void ThePrimaryHandler_IsReplacedAfterTheFactorysMetricsFilterConfiguredTheAppsOwn()
    {
        // The metrics filter gives a SocketsHttpHandler the app's meter factory once the client's configuration has run. The
        // in-process filter runs after it, so the metrics filter never sees the refusing handler, which stays.
        var appsPrimaryHandler = new SocketsHttpHandler();
        using var provider = BuildProvider(_ => { }, primaryHandler: () => appsPrimaryHandler);

        var (_, primaryHandler) = WalkTheChain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName));

        Assert.IsType<RefusingPrimaryHandler>(primaryHandler);
        Assert.NotNull(appsPrimaryHandler.MeterFactory);
    }

    [Fact]
    public async Task ARequestOtherCodeSendsThroughTheInvokersClient_IsServedInProcessToo()
    {
        using var provider = BuildProvider(_ => { });
        BuildPipeline(provider, context => context.Response.WriteAsync($"{context.Request.Host} {context.Request.Path}"));
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        var body = await client.GetStringAsync("https://elsewhere.example/api/status");

        Assert.Equal("elsewhere.example /api/status", body);
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public async Task ACall_FailsSayingWhy_WhenTheHostBuiltNoPipeline()
    {
        using var provider = BuildProvider(_ => { });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(provider));

        Assert.StartsWith("The tool's API request GET https://api.example.com/api/status cannot run: a tool runs its API request in process", exception.Message);
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public async Task ACallOutsideAnMcpRequest_IsServedInProcess_NamedAtTheConfiguredBaseUrl()
    {
        using var provider = BuildProvider(services => services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["McpTools:BaseUrl"] = "https://api.example.com/" })
            .Build()));
        BuildPipeline(provider, context => context.Response.WriteAsync($"{context.Request.Scheme} {context.Request.Host} {context.Request.Path}"));
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;

        var result = await provider.GetRequiredService<IMcpHttpInvoker>().GetAsync("/api/status");

        Assert.False(result.IsError, Text(result));
        Assert.Equal("https api.example.com /api/status", Text(result));
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public async Task TheClientFactorysLogging_AndALoggerTheAppAdds_SeeTheRequestServedInProcess()
    {
        // The factory adds both at the end of the chain, after every builder action: the logger of AddLogger, and its own
        // logging handler that logs "Sending HTTP request".
        var logs = new List<(LogLevel Level, string Message)>();
        var loggedRequests = new List<string>();
        using var provider = BuildProvider(
            services => services.AddHttpClient(ClientName).AddLogger(_ => new RecordingClientLogger(loggedRequests)),
            logs: logs);
        BuildEchoPipeline(provider);

        var result = await CallAsync(provider);

        Assert.False(result.IsError, Text(result));
        Assert.Equal(["GET https://api.example.com/api/status 200"], loggedRequests);
        lock (logs)
        {
            Assert.Contains(logs, x => x.Message == "Sending HTTP request GET https://api.example.com/api/status");
        }

        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public void EachChainTheFactoryBuilds_GetsAnInProcessHandlerOfItsOwn()
    {
        // The factory builds the client's chain again once its handler lifetime ends; a handler joins one chain only.
        using var provider = BuildProvider(_ => { });
        var filter = provider.GetServices<IHttpMessageHandlerBuilderFilter>().OfType<InProcessApiHandlerFilter>().Single();

        var first = BuildChain(provider, filter);
        var second = BuildChain(provider, filter);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task ARedirectThePipelineLeavesAsItIs_ComesBackAsAnErrorResult_AndIsNotFollowedAgain()
    {
        // The redirect leaves the path base a proxy gave the MCP request, so the pipeline does not follow it. The invoker
        // follows none itself, though this one leads to the scheme, host and port it named.
        var hits = 0;
        using var provider = BuildProvider(_ => { });
        BuildPipeline(provider, context =>
        {
            Interlocked.Increment(ref hits);
            context.Response.Redirect("/elsewhere");
            return Task.CompletedTask;
        });
        var caller = CreateCaller();
        caller.Request.PathBase = "/v1";

        var result = await CallAsync(provider, caller);

        Assert.True(result.IsError);
        Assert.Equal("HTTP 302: Found", Text(result));
        Assert.Equal(1, hits);
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public async Task TheClientsTimeout_FailsAnInProcessCall_AsHttpClientDoes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = BuildProvider(services => services.AddHttpClient(ClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMilliseconds(200)));
        BuildPipeline(provider, _ => release.Task);

        try
        {
            var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => CallAsync(provider).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.Contains("HttpClient.Timeout", exception.Message);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheToolsRequest_IsServedInProcessWithoutTheClient_WhenTheAppTakesTheInProcessHandlersFilterAway(bool isReplaced)
    {
        using var provider = BuildProvider(services =>
        {
            services.AddHttpClient(ClientName)
                .AddHttpMessageHandler(() => new HeaderSettingHandler(ApiKeyHeader, "secret"))
                .ConfigureHttpClient(client => client.DefaultRequestHeaders.Add(TenantHeader, "t1"));
            TakeTheBuilderFiltersAway(services, isReplaced);
        });
        BuildEchoPipeline(provider);

        var result = await CallAsync(provider);

        var seen = Echoed(result);
        Assert.Equal(CallerToken, seen.GetProperty("authorization").GetString());
        Assert.Equal("true", seen.GetProperty("internalCall").GetString());
        Assert.Equal("", seen.GetProperty("apiKey").GetString());
        Assert.Equal("", seen.GetProperty("tenant").GetString());
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public async Task TheClientsTimeout_FailsACallServedWithoutTheClient_AsHttpClientDoes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = BuildProvider(services =>
        {
            services.AddHttpClient(ClientName).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMilliseconds(200));
            TakeTheBuilderFiltersAway(services, isReplaced: false);
        });
        BuildPipeline(provider, _ => release.Task);

        try
        {
            var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => CallAsync(provider).WaitAsync(CallTimeout));
            Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.Contains("HttpClient.Timeout", exception.Message);
            Assert.Empty(_primaryRequests);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task ACanceledCallServedWithoutTheClient_ThrowsTheCallersCancellation_AsHttpClientDoes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = BuildProvider(services => TakeTheBuilderFiltersAway(services, isReplaced: false));
        BuildPipeline(provider, _ =>
        {
            started.SetResult();
            return release.Task;
        });
        using var cancellation = new CancellationTokenSource();

        try
        {
            var calling = CallAsync(provider, invoker => invoker.GetAsync("/api/status", cancellation.Token));
            await started.Task.WaitAsync(CallTimeout);
            cancellation.Cancel();

            var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => calling.WaitAsync(CallTimeout));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Empty(_primaryRequests);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheStartupCheck_Warns_WhenTheAppTakesTheInProcessHandlersFilterAway_AndNamesTheFix(bool isReplaced)
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(services => TakeTheBuilderFiltersAway(services, isReplaced), logs: logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);
        await startupCheck.StartedAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning && x.Message.Contains(nameof(IHttpMessageHandlerBuilderFilter)));
        Assert.Contains(ClientName, warning.Message);
        Assert.Contains("RemoveAll<IHttpMessageHandlerBuilderFilter>()", warning.Message);
        Assert.Contains("Replace", warning.Message);
        Assert.Contains("message handlers and DefaultRequestHeaders do not apply", warning.Message);
        Assert.Contains("TryAddEnumerable", warning.Message);
        Assert.Contains("RemoveAllLoggers()", warning.Message);
    }

    [Fact]
    public async Task TheStartupCheck_DoesNotWarnAboutTheClient_WhenItServesInProcess()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(_ => { }, logs: logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);
        await startupCheck.StartedAsync(CancellationToken.None);

        Assert.DoesNotContain(logs, x => x.Level == LogLevel.Warning && x.Message.Contains(ClientName));
    }

    [Fact]
    public void TheInProcessHandlerCheck_LooksAtTheClientsChainOnce()
    {
        using var provider = BuildProvider(_ => { });
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var compared = 0;
        var created = 0;
        var check = new InProcessApiHandlerCheck(
            () =>
            {
                Interlocked.Increment(ref compared);
                return true;
            },
            () =>
            {
                Interlocked.Increment(ref created);
                return handlerFactory.CreateHandler(ClientName);
            });

        Assert.True(check.IsInTheClientsChain);
        Assert.True(check.IsInTheClientsChain);
        Assert.True(check.IsClientFromTheHandlerFactory);
        Assert.Equal(1, compared);
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task TheToolsRequest_IsServedInProcessWithoutTheClient_WhenTheAppRegistersAnHttpClientFactoryOfItsOwn()
    {
        using var provider = BuildProvider(services =>
        {
            services.AddHttpClient(ClientName).AddHttpMessageHandler(() => new HeaderSettingHandler(ApiKeyHeader, "secret"));
            services.AddSingleton<IHttpClientFactory>(new StubbingClientFactory(_primaryRequests));
        });
        BuildEchoPipeline(provider);

        var result = await CallAsync(provider);

        var seen = Echoed(result);
        Assert.Equal(CallerToken, seen.GetProperty("authorization").GetString());
        Assert.Equal("", seen.GetProperty("apiKey").GetString());
        Assert.Empty(_primaryRequests);
    }

    [Fact]
    public async Task TheStartupCheck_Warns_WhenTheAppRegistersAnHttpClientFactoryOfItsOwn_AndNamesTheFix()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(
            services => services.AddSingleton<IHttpClientFactory>(new StubbingClientFactory(_primaryRequests)),
            logs: logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);
        await startupCheck.StartedAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning && x.Message.Contains(ClientName));
        Assert.Contains("IHttpClientFactory registered after AddMcpTools", warning.Message);
        Assert.Contains("message handlers and DefaultRequestHeaders do not apply", warning.Message);
        Assert.Contains("ConfigurePrimaryHttpMessageHandler", warning.Message);
    }

    /// <summary>
    /// The app's services with <c>AddMcpTools</c>, and the configuration of the invoker's client registered before or
    /// after it. The client's primary handler is set by the app after <c>AddMcpTools</c> either way: one that records every
    /// request, unless <paramref name="primaryHandler"/> gives another.
    /// </summary>
    private ServiceProvider BuildProvider(
        Action<IServiceCollection> configureClient,
        bool isConfiguredBeforeAddMcpTools = false,
        List<(LogLevel Level, string Message)>? logs = null,
        Func<HttpMessageHandler>? primaryHandler = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.AddProvider(new ListLoggerProvider(logs));
            }
        });
        RegisterHostServices(services);
        if (isConfiguredBeforeAddMcpTools)
        {
            configureClient(services);
        }

        services.AddMcpTools(ToolAssembly);
        if (!isConfiguredBeforeAddMcpTools)
        {
            configureClient(services);
        }

        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(primaryHandler ?? (() => new RecordingPrimaryHandler(_primaryRequests)));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The two recipes that take the client factory's logging filter away, run after <c>AddMcpTools</c>, which take the
    /// filter <c>AddMcpTools</c> registers first away with it: <c>RemoveAll</c> every filter, and <c>Replace</c> the first.
    /// </summary>
    private static void TakeTheBuilderFiltersAway(IServiceCollection services, bool isReplaced)
    {
        if (isReplaced)
        {
            services.Replace(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, AppLoggingFilter>());
            return;
        }

        services.RemoveAll<IHttpMessageHandlerBuilderFilter>();
    }

    /// <summary>
    /// What the web host registers with TryAdd.
    /// </summary>
    private static void RegisterHostServices(IServiceCollection services)
    {
        var server = new Mock<IServer>();
        server.Setup(x => x.Features).Returns(new FeatureCollection());
        services.TryAddSingleton<IApplicationBuilderFactory, ApplicationBuilderFactory>();
        services.TryAddSingleton<IHttpContextFactory, DefaultHttpContextFactory>();
        services.TryAddSingleton(server.Object);
        services.TryAddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    }

    /// <summary>
    /// Builds the app's pipeline as the host does when it starts.
    /// </summary>
    private static void BuildPipeline(IServiceProvider provider, RequestDelegate api)
    {
        var app = provider.GetRequiredService<IApplicationBuilderFactory>().CreateBuilder(new FeatureCollection());
        app.Run(api);
        app.Build();
    }

    private static void BuildEchoPipeline(IServiceProvider provider)
    {
        BuildPipeline(provider, context => context.Response.WriteAsJsonAsync(new Dictionary<string, string>
        {
            ["apiKey"] = context.Request.Headers[ApiKeyHeader].ToString(),
            ["clientDefaults"] = context.Request.Headers[ClientDefaultsHeader].ToString(),
            ["tenant"] = context.Request.Headers[TenantHeader].ToString(),
            ["authorization"] = context.Request.Headers.Authorization.ToString(),
            ["internalCall"] = context.Request.Headers[LoopPreventionHeader].ToString()
        }));
    }

    private static DefaultHttpContext CreateCaller()
    {
        var caller = new DefaultHttpContext();
        caller.Request.Scheme = "https";
        caller.Request.Host = new HostString("api.example.com");
        caller.Request.Path = "/mcp";
        caller.Request.Headers.Authorization = CallerToken;
        return caller;
    }

    private static Task<CallToolResult> CallAsync(IServiceProvider provider, DefaultHttpContext? caller = null)
    {
        return CallAsync(provider, invoker => invoker.GetAsync("/api/status"), caller);
    }

    /// <summary>
    /// Calls the API through the registered invoker during an MCP request.
    /// </summary>
    private static Task<CallToolResult> CallAsync(IServiceProvider provider, Func<IMcpHttpInvoker, Task<CallToolResult>> call, DefaultHttpContext? caller = null)
    {
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = caller ?? CreateCaller();

        return call(provider.GetRequiredService<IMcpHttpInvoker>());
    }

    private static (List<DelegatingHandler> Handlers, HttpMessageHandler PrimaryHandler) WalkTheChain(HttpMessageHandler handler)
    {
        var handlers = new List<DelegatingHandler>();
        var current = handler;
        var delegating = current as DelegatingHandler;
        while (delegating is not null)
        {
            handlers.Add(delegating);
            current = delegating.InnerHandler!;
            delegating = current as DelegatingHandler;
        }

        return (handlers, current);
    }

    private static InProcessApiHandler BuildChain(IServiceProvider provider, InProcessApiHandlerFilter filter)
    {
        var builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
        builder.Name = ClientName;
        builder.PrimaryHandler = new RecordingPrimaryHandler([]);
        filter.Configure(_ => { })(builder);
        var (handlers, _) = WalkTheChain(builder.Build());

        return Assert.IsType<InProcessApiHandler>(Assert.Single(handlers));
    }

    private static JsonElement Echoed(CallToolResult result)
    {
        Assert.False(result.IsError, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement;
    }

    private static string Text(CallToolResult result)
    {
        return ((TextContentBlock)result.Content[0]).Text;
    }

    private sealed class HeaderSettingHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly string _value;

        public HeaderSettingHandler(string name, string value)
        {
            _name = name;
            _value = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(_name, _value);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RetryOn503Handler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return response;
            }

            response.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Records the URI and the API key of every request, and answers each.
    /// </summary>
    private sealed class RecordingPrimaryHandler : HttpMessageHandler
    {
        private readonly List<string> _requests;

        public RecordingPrimaryHandler(List<string> requests)
        {
            _requests = requests;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var apiKey = request.Headers.TryGetValues(ApiKeyHeader, out var values) ? string.Join(",", values) : "";
            lock (_requests)
            {
                _requests.Add($"{request.RequestUri} {apiKey}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("over http") });
        }
    }

    private sealed class RecordingClientLogger : IHttpClientLogger
    {
        private readonly List<string> _requests;

        public RecordingClientLogger(List<string> requests)
        {
            _requests = requests;
        }

        public object? LogRequestStart(HttpRequestMessage request)
        {
            return null;
        }

        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
        {
            lock (_requests)
            {
                _requests.Add($"{request.Method} {request.RequestUri} {(int)response.StatusCode}");
            }
        }

        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
        {
        }
    }

    /// <summary>
    /// A filter an app puts in place of the client factory's logging filter.
    /// </summary>
    private sealed class AppLoggingFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        {
            return next;
        }
    }

    /// <summary>
    /// A client factory an app or a test registers to stub outgoing calls: its clients send every request to a primary
    /// handler that records it, past every handler the client factory would add.
    /// </summary>
    private sealed class StubbingClientFactory : IHttpClientFactory
    {
        private readonly List<string> _requests;

        public StubbingClientFactory(List<string> requests)
        {
            _requests = requests;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new RecordingPrimaryHandler(_requests));
        }
    }
}
