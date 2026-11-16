using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Options;
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
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Extensions;

/// <summary>
/// The invoker's named <see cref="HttpClient"/>, as <c>AddMcpTools</c> registers it and an app configures it: every call
/// goes through it, and a request served in process passes every handler the app gives it. The client's primary handler
/// records each request it gets, which only a request sent over HTTP reaches.
/// </summary>
public class McpInvokerHttpClientTests
{
    private const string ClientName = nameof(IMcpHttpInvoker);
    private const string CallerToken = "Bearer caller-token";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string TenantHeader = "X-Tenant";
    private const string ClientDefaultsHeader = "X-Client-Defaults";
    private const string LoopPreventionHeader = McpLoopPreventionMiddleware.LoopPreventionHeader;
    private const string ProviderBaseUrl = "http://localhost:5000";

    private static readonly Assembly ToolAssembly = typeof(StaticParityTools).Assembly;

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
    public void TheInProcessHandler_SitsBelowEveryOtherHandler_RightAboveTheAppsPrimaryHandler(bool isConfiguredBeforeAddMcpTools)
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
        Assert.IsType<RecordingPrimaryHandler>(primaryHandler);
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
        // The redirect leaves the path base a proxy gave the MCP request, so the pipeline does not follow it. Over HTTP the
        // invoker follows every redirect to the address it called, which this one is.
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

    [Fact]
    public async Task TheInvokersRequest_GoesThroughTheAppsHandlers_ToThePrimaryHandler_WhenInProcessCallsAreOff()
    {
        using var provider = BuildProvider(services =>
        {
            services.Configure<McpScopeOptions>(options => options.AreApiCallsInProcess = false);
            services.AddSingleton<ISelfBaseUrlProvider>(new FixedBaseUrlProvider(ProviderBaseUrl));
            services.AddHttpClient(ClientName).AddHttpMessageHandler(() => new HeaderSettingHandler(ApiKeyHeader, "secret"));
        });
        BuildEchoPipeline(provider);

        var result = await CallAsync(provider);

        Assert.Equal("over http", Text(result));
        Assert.Equal([$"{ProviderBaseUrl}/api/status secret"], _primaryRequests);
    }

    [Fact]
    public async Task AnInvokerFromThePublicConstructor_SendsThroughThePrimaryHandler_OfTheClientThatServesInProcess()
    {
        using var provider = BuildProvider(_ => { });
        BuildEchoPipeline(provider);
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = CreateCaller();
        var invoker = new McpHttpInvoker(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName),
            new FixedBaseUrlProvider(ProviderBaseUrl),
            accessor,
            NullLogger<McpHttpInvoker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new McpScopeOptions()));

        var result = await invoker.GetAsync("/api/status");

        Assert.Equal("over http", Text(result));
        Assert.Equal([$"{ProviderBaseUrl}/api/status "], _primaryRequests);
    }

    /// <summary>
    /// The app's services with <c>AddMcpTools</c>, and the configuration of the invoker's client registered before or
    /// after it. The client's primary handler is set by the app after <c>AddMcpTools</c> either way.
    /// </summary>
    private ServiceProvider BuildProvider(
        Action<IServiceCollection> configureClient,
        bool isConfiguredBeforeAddMcpTools = false,
        List<(LogLevel Level, string Message)>? logs = null)
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

        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => new RecordingPrimaryHandler(_primaryRequests));

        return services.BuildServiceProvider();
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

    private sealed class FixedBaseUrlProvider : ISelfBaseUrlProvider
    {
        private readonly string _baseUrl;

        public FixedBaseUrlProvider(string baseUrl)
        {
            _baseUrl = baseUrl;
        }

        public string GetBaseUrl()
        {
            return _baseUrl;
        }
    }
}
