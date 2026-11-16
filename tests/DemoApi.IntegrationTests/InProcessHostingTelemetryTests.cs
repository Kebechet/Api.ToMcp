using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
/// The telemetry of a tool's in-process API request against that of the same request sent straight to Kestrel, which
/// ASP.NET Core's hosting records: the span and its events, the metrics and the log entries, read with an
/// <see cref="ActivityListener"/>, a <see cref="DiagnosticListener"/> subscriber, a <see cref="MeterListener"/> and a
/// capturing logger on real Kestrel. The invoker's HTTP client refuses every request, so a tool call that succeeds was
/// served in process. The tests of this class run one after another, as one of them sets an <see cref="AppContext"/>
/// switch hosting reads.
/// </summary>
public sealed class InProcessHostingTelemetryTests
{
    private const string Route = "/api/hosting-telemetry";
    private const string HostingCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string ActivityName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
    private const string StartEvent = ActivityName + ".Start";
    private const string StopEvent = ActivityName + ".Stop";
    private const string RequestDuration = "http.server.request.duration";
    private const string ActiveRequests = "http.server.active_requests";
    private const string Marker = "api_to_mcp.tool_request";
    private const string SuppressActivityOpenTelemetryData = "Microsoft.AspNetCore.Hosting.SuppressActivityOpenTelemetryData";
    private const string HttpResponseStatusCode = "http.response.status_code";

    [Theory]
    [InlineData("/items/7", StatusCodes.Status200OK)]
    [InlineData("/fail", StatusCodes.Status500InternalServerError)]
    public async Task AToolsRequest_GetsTheSpanEventsMetricsAndLogsOfOneSentToTheServer_WithTheMarker(string path, int status)
    {
        await using var host = await TelemetryHost.StartAsync();

        await host.SendDirectlyAsync(Route + path);
        var tool = await host.CallToolAsync(Route + path);
        await host.WaitUntilEveryRequestEndedAsync();

        Assert.Equal(status == StatusCodes.Status200OK, !tool.IsError);
        var (direct, inProcess) = host.TakeDirectAndToolRequest(Route + path);
        Assert.Equal(ActivityName, inProcess.Span.OperationName);
        Assert.Equal(ActivityKind.Server, inProcess.Span.Kind);
        Assert.Equal(direct.Span.Kind, inProcess.Span.Kind);
        Assert.Equal(direct.Span.Status, inProcess.Span.Status);
        Assert.Equal(WithMarker(direct.Span.Tags), inProcess.Span.Tags);
        Assert.Equal(direct.Events, inProcess.Events);
        Assert.Equal(status, Assert.Single(inProcess.Durations)[HttpResponseStatusCode]);
        Assert.Equal(WithMarker(Assert.Single(direct.Durations)), Assert.Single(inProcess.Durations));
        Assert.Equal(direct.HostingLogs, inProcess.HostingLogs);
        Assert.Equal([1, 2], inProcess.HostingLogs.Select(x => x.EventId));
        Assert.Equal(true, inProcess.Scope[Marker]);
        Assert.Equal(direct.Scope["RequestPath"], inProcess.Scope["RequestPath"]);
        Assert.False(direct.Scope.ContainsKey(Marker));
    }

    [Fact]
    public async Task AToolsRequest_ThatNoEndpointServes_GetsTheTelemetryOfOneSentToTheServer_ButTheContentLengthKestrelSets()
    {
        await using var host = await TelemetryHost.StartAsync();

        await host.SendDirectlyAsync(Route + "/missing");
        var tool = await host.CallToolAsync(Route + "/missing");
        await host.WaitUntilEveryRequestEndedAsync();

        Assert.True(tool.IsError);
        var (direct, inProcess) = host.TakeDirectAndToolRequest(Route + "/missing");
        Assert.Equal(WithMarker(direct.Span.Tags), inProcess.Span.Tags);
        Assert.Equal(direct.Events, inProcess.Events);
        Assert.Equal(true, Assert.Single(inProcess.Durations)["aspnetcore.request.is_unhandled"]);
        Assert.Equal(WithMarker(Assert.Single(direct.Durations)), Assert.Single(inProcess.Durations));
        Assert.Equal([1, 2, 16], inProcess.HostingLogs.Select(x => x.EventId));

        // Kestrel says a response the app left without a body has none, with Content-Length: 0; the tool's request leaves
        // the header out, as the app did.
        Assert.Equal(
            direct.HostingLogs.Select(x => x with { Message = x.Message.Replace(" - 404 0 - ", " - 404 - - ") }),
            inProcess.HostingLogs);
    }

    [Fact]
    public async Task AToolsSpan_IsAChildOfTheMcpRequestsSpan_InItsTrace()
    {
        await using var host = await TelemetryHost.StartAsync();

        await host.CallToolAsync(Route + "/items/7");
        await host.WaitUntilEveryRequestEndedAsync();

        var tool = Assert.Single(host.ServedRequests(Route + "/items/7"));
        var mcp = Assert.Single(host.ServedRequests("/mcp"), x => x.Span.SpanId == tool.Span.ParentSpanId);
        Assert.Equal(mcp.Span.TraceId, tool.Span.TraceId);
        Assert.Equal(true, tool.Span.Tags[Marker]);
        Assert.False(mcp.Span.Tags.ContainsKey(Marker));
    }

    [Fact]
    public async Task EveryRequest_IsMeasuredOnce_TheToolsAndTheOnesTheServerReceived()
    {
        await using var host = await TelemetryHost.StartAsync();

        await host.SendDirectlyAsync(Route + "/items/7");
        await host.CallToolAsync(Route + "/items/7");
        await host.CallToolAsync(Route + "/fail");
        await host.WaitUntilEveryRequestEndedAsync();

        var spans = host.Spans.Where(x => x.OperationName == ActivityName).ToList();
        var durations = host.Measurements.Where(x => x.Instrument == RequestDuration).ToList();
        var active = host.Measurements.Where(x => x.Instrument == ActiveRequests).ToList();
        Assert.Equal(spans.Count, durations.Count);
        Assert.All(spans, span => Assert.Single(durations, x => x.SpanId == span.SpanId));
        Assert.Equal(2, durations.Count(x => x.Tags.ContainsKey(Marker)));
        Assert.Equal(spans.Count, active.Count(x => x.Value == 1));
        Assert.Equal(spans.Count, active.Count(x => x.Value == -1));
        Assert.Equal(spans.Count, host.Logs.Count(x => x.Category == HostingCategory && x.EventId == 2));
        var duration = Assert.Single(host.Instruments, x => x.Name == RequestDuration);
        Assert.Single(host.Instruments, x => x.Name == ActiveRequests);

        // Hosting's advice, which the instrument keeps only when hosting creates it, before the first tool call.
        Assert.Equal(
            [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10],
            Assert.IsType<Histogram<double>>(duration).Advice?.HistogramBucketBoundaries);
    }

    [Theory]
    [InlineData("/untracked")]
    [InlineData("/quiet")]
    public async Task AToolsRequest_GoesWithoutItsDuration_WhenTheAppTurnsHttpMetricsOff_AsOneSentToTheServer(string path)
    {
        await using var host = await TelemetryHost.StartAsync();

        await host.SendDirectlyAsync(Route + path);
        var tool = await host.CallToolAsync(Route + path);
        await host.WaitUntilEveryRequestEndedAsync();

        Assert.False(tool.IsError, tool.Text);
        var (direct, inProcess) = host.TakeDirectAndToolRequest(Route + path);
        Assert.Empty(direct.Durations);
        Assert.Empty(inProcess.Durations);
        Assert.Equal(0, host.Measurements.Where(x => x.Instrument == ActiveRequests).Sum(x => x.Value));
    }

    [Fact]
    public async Task AToolsSpan_GetsTheTagsHostingCreatesItsSpanWith_WhenTheAppTurnsTheirSwitchOff()
    {
        AppContext.SetSwitch(SuppressActivityOpenTelemetryData, false);
        try
        {
            await using var host = await TelemetryHost.StartAsync();

            await host.SendDirectlyAsync(Route + "/items/7");
            await host.CallToolAsync(Route + "/items/7");
            await host.WaitUntilEveryRequestEndedAsync();

            var (direct, inProcess) = host.TakeDirectAndToolRequest(Route + "/items/7");
            Assert.Equal("GET", direct.Span.Tags["http.request.method"]);
            Assert.Equal(Route + "/items/7", direct.Span.Tags["url.path"]);
            Assert.Equal(WithMarker(direct.Span.Tags), inProcess.Span.Tags);
        }
        finally
        {
            AppContext.SetSwitch(SuppressActivityOpenTelemetryData, true);
        }
    }

    private static Dictionary<string, object?> WithMarker(IReadOnlyDictionary<string, object?> tags)
    {
        return new Dictionary<string, object?>(tags) { [Marker] = true };
    }

    private sealed record SpanRecord(
        string OperationName,
        ActivityKind Kind,
        ActivityStatusCode Status,
        ActivityTraceId TraceId,
        ActivitySpanId SpanId,
        ActivitySpanId ParentSpanId,
        Dictionary<string, object?> Tags);

    private sealed record EventRecord(string Name, string? Path, Activity? Current);

    private sealed record MeasurementRecord(string Instrument, long Value, Dictionary<string, object?> Tags, ActivitySpanId? SpanId);

    private sealed record LogRecord(string Category, int EventId, LogLevel Level, string Message, object? Template, Dictionary<string, object?> Scope, ActivitySpanId? SpanId);

    private sealed record HostingLogRecord(string Category, int EventId, LogLevel Level, string Message, object? Template);

    /// <summary>
    /// What hosting recorded for one request, with the elapsed time left out of its log entries.
    /// </summary>
    private sealed record ServedRequest(
        SpanRecord Span,
        List<string> Events,
        List<Dictionary<string, object?>> Durations,
        List<HostingLogRecord> HostingLogs,
        Dictionary<string, object?> Scope);

    /// <summary>
    /// The app on Kestrel with listeners on its own <see cref="ActivitySource"/>, <see cref="DiagnosticListener"/> and
    /// meters, which other tests' hosts do not share.
    /// </summary>
    private sealed class TelemetryHost : IAsyncDisposable, IObserver<KeyValuePair<string, object?>>
    {
        private static readonly Regex Elapsed = new(@" \d+\.\d{4}ms$");

        private readonly WebApplication _app;
        private readonly LogRecorder _logs;
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;
        private readonly IDisposable _eventSubscription;
        private readonly List<SpanRecord> _spans = [];
        private readonly List<EventRecord> _events = [];
        private readonly List<MeasurementRecord> _measurements = [];
        private readonly List<Instrument> _instruments = [];

        private TelemetryHost(WebApplication app, LogRecorder logs)
        {
            _app = app;
            _logs = logs;
            var activitySource = app.Services.GetRequiredService<ActivitySource>();
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, activitySource),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = OnActivityStopped
            };
            ActivitySource.AddActivityListener(_activityListener);
            _eventSubscription = app.Services.GetRequiredService<DiagnosticListener>().Subscribe(this);
            var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter.Scope, meterFactory) && instrument.Meter.Name == "Microsoft.AspNetCore.Hosting")
                    {
                        lock (_instruments)
                        {
                            _instruments.Add(instrument);
                        }

                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _meterListener.SetMeasurementEventCallback<double>(OnMeasurement);
            _meterListener.SetMeasurementEventCallback<long>(OnMeasurement);
            _meterListener.Start();
        }

        public IReadOnlyList<SpanRecord> Spans => Snapshot(_spans);

        public IReadOnlyList<MeasurementRecord> Measurements => Snapshot(_measurements);

        public IReadOnlyList<Instrument> Instruments => Snapshot(_instruments);

        public IReadOnlyList<LogRecord> Logs => Snapshot(_logs.Entries);

        public static async Task<TelemetryHost> StartAsync()
        {
            var logs = new LogRecorder();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.AddFilter(HostingCategory, LogLevel.Information);
            builder.Services.AddControllers().AddApplicationPart(typeof(HostingTelemetryController).Assembly);
            builder.Services.AddMcpTools(typeof(ParityProbeTools).Assembly);
            builder.Services.AddHttpClient(nameof(IMcpHttpInvoker)).ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler());

            var app = builder.Build();
            var host = new TelemetryHost(app, logs);
            app.Use((context, next) =>
            {
                if (context.Request.Path == Route + "/quiet")
                {
                    context.Features.Get<IHttpMetricsTagsFeature>()!.MetricsDisabled = true;
                }

                return next(context);
            });
            app.UseMcpLoopPrevention();
            app.MapControllers();
            app.MapGet(Route + "/untracked", () => Results.Ok(new { tracked = false })).DisableHttpMetrics();
            app.MapGet(Route + "/quiet", () => Results.Ok(new { tracked = false }));
            app.MapMcpEndpoint("mcp", allowAnonymous: true);
            await app.StartAsync();

            return host;
        }

        public async Task SendDirectlyAsync(string route)
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(_app.Urls.Single() + route);
        }

        public async Task<(bool IsError, string Text)> CallToolAsync(string route)
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(_app.Urls.Single() + "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                NullLoggerFactory.Instance);
            await using var client = await McpClient.CreateAsync(transport);

            var result = await client.CallToolAsync("parity_get", new Dictionary<string, object?> { ["route"] = route });
            return (result.IsError == true, ((TextContentBlock)result.Content[0]).Text);
        }

        /// <summary>
        /// Waits for hosting to end every request it started: Kestrel ends a request's telemetry after it sent the
        /// response, so the client can have it first.
        /// </summary>
        public async Task WaitUntilEveryRequestEndedAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var events = Snapshot(_events);
                if (events.Count(x => x.Name == StartEvent) == events.Count(x => x.Name == StopEvent))
                {
                    return;
                }

                Assert.True(DateTime.UtcNow < deadline, "A request's telemetry did not end.");
                await Task.Delay(10);
            }
        }

        /// <summary>
        /// The requests to the path whose span started, in the order they started.
        /// </summary>
        public List<ServedRequest> ServedRequests(string path)
        {
            var events = Snapshot(_events);
            var spans = Snapshot(_spans);
            var measurements = Snapshot(_measurements);
            var logs = Snapshot(_logs.Entries);

            return events
                .Where(x => x.Name == StartEvent && x.Path == path)
                .Select(start =>
                {
                    var spanId = start.Current!.SpanId;
                    var spanLogs = logs.Where(x => x.SpanId == spanId).ToList();
                    return new ServedRequest(
                        spans.Single(x => x.SpanId == spanId),
                        events.Where(x => x.Current?.SpanId == spanId).Select(x => x.Name).ToList(),
                        measurements.Where(x => x.Instrument == RequestDuration && x.SpanId == spanId).Select(x => x.Tags).ToList(),
                        spanLogs
                            .Where(x => x.Category == HostingCategory)
                            .Select(x => new HostingLogRecord(x.Category, x.EventId, x.Level, Elapsed.Replace(x.Message, " ms"), x.Template))
                            .ToList(),
                        spanLogs.First(x => x.Category == HostingCategory).Scope);
                })
                .ToList();
        }

        public (ServedRequest Direct, ServedRequest Tool) TakeDirectAndToolRequest(string path)
        {
            var requests = ServedRequests(path);
            Assert.Equal(2, requests.Count);
            return (Assert.Single(requests, x => !x.Span.Tags.ContainsKey(Marker)), Assert.Single(requests, x => x.Span.Tags.ContainsKey(Marker)));
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            // The server pools a request's HttpContext, so its path is read while the event is written.
            var context = value.Value as HttpContext ?? value.Value?.GetType().GetProperty("httpContext")?.GetValue(value.Value) as HttpContext;
            var path = context is null ? null : (context.Request.PathBase + context.Request.Path).ToString();
            lock (_events)
            {
                _events.Add(new EventRecord(value.Key, path, Activity.Current));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            _activityListener.Dispose();
            _meterListener.Dispose();
            _eventSubscription.Dispose();
            await _app.DisposeAsync();
        }

        private void OnActivityStopped(Activity activity)
        {
            var record = new SpanRecord(
                activity.OperationName,
                activity.Kind,
                activity.Status,
                activity.TraceId,
                activity.SpanId,
                activity.ParentSpanId,
                activity.TagObjects.ToDictionary(x => x.Key, x => x.Value));
            lock (_spans)
            {
                _spans.Add(record);
            }
        }

        private void OnMeasurement<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var record = new MeasurementRecord(
                instrument.Name,
                Convert.ToInt64(value),
                tags.ToArray().ToDictionary(x => x.Key, x => x.Value),
                Activity.Current?.SpanId);
            lock (_measurements)
            {
                _measurements.Add(record);
            }
        }

        private static List<T> Snapshot<T>(List<T> items)
        {
            lock (items)
            {
                return [.. items];
            }
        }
    }

    /// <summary>
    /// Records each entry with its category, event id, template and scope, and the span it was logged under.
    /// </summary>
    private sealed class LogRecorder : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopeProvider;

        public List<LogRecord> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(this, categoryName);
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly LogRecorder _recorder;
            private readonly string _category;

            public Logger(LogRecorder recorder, string category)
            {
                _recorder = recorder;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return _recorder._scopeProvider?.Push(state);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel != LogLevel.None;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scope = new Dictionary<string, object?>();
                _recorder._scopeProvider?.ForEachScope((value, _) =>
                {
                    var pairs = value as IEnumerable<KeyValuePair<string, object>>;
                    if (pairs is not null)
                    {
                        foreach (var (key, item) in pairs)
                        {
                            scope[key] = item;
                        }
                    }
                }, (object?)null);

                var template = (state as IEnumerable<KeyValuePair<string, object?>>)?.FirstOrDefault(x => x.Key == "{OriginalFormat}").Value;
                var entry = new LogRecord(_category, eventId.Id, logLevel, formatter(state, exception), template, scope, Activity.Current?.SpanId);
                lock (_recorder.Entries)
                {
                    _recorder.Entries.Add(entry);
                }
            }
        }
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
[Route("api/hosting-telemetry")]
public sealed class HostingTelemetryController : ControllerBase
{
    [HttpGet("items/{id}")]
    public IActionResult GetItem(int id)
    {
        return Ok(new { id });
    }

    [HttpGet("fail")]
    public IActionResult Fail()
    {
        throw new InvalidOperationException("The item store is down.");
    }
}
