using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.RegularExpressions;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

/// <summary>
/// The logs, the span, the events and the metrics the tool's in-process request gets, as ASP.NET Core's hosting gives
/// them a request the server receives, read with an <see cref="ActivityListener"/>, a <see cref="DiagnosticListener"/>
/// subscriber, a <see cref="MeterListener"/> and a capturing logger.
/// </summary>
public class InProcessRequestDiagnosticsTests
{
    private const string ItemsUrl = "https://api.example.com/api/items/7?x=1";
    private const string ItemsRoute = "/api/items/{id}";
    private const string HostingCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string ActivityName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
    private const string StartEvent = "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start";
    private const string StopEvent = "Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop";
    private const string BeginRequestEvent = "Microsoft.AspNetCore.Hosting.BeginRequest";
    private const string EndRequestEvent = "Microsoft.AspNetCore.Hosting.EndRequest";
    private const string UnhandledExceptionEvent = "Microsoft.AspNetCore.Hosting.UnhandledException";
    private const string HostingMeter = "Microsoft.AspNetCore.Hosting";
    private const string RequestDuration = "http.server.request.duration";
    private const string ActiveRequests = "http.server.active_requests";
    private const string Marker = "api_to_mcp.tool_request";

    [Fact]
    public async Task SendAsync_RunsTheApp_UnderAServerSpanOfItsOwn_AChildOfTheToolCalls()
    {
        Activity? currentInside = null;
        Activity? featureInside = null;
        using var telemetry = new Telemetry(app => app.Run(context =>
        {
            currentInside = Activity.Current;
            featureInside = context.Features.Get<IHttpActivityFeature>()?.Activity;
            return Task.CompletedTask;
        }));
        using var toolCall = new Activity("tool call").Start();

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        var span = Assert.Single(telemetry.Spans);
        Assert.Equal(ActivityName, span.OperationName);
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Same(telemetry.ActivitySource, span.Source);
        Assert.Same(toolCall, span.Parent);
        Assert.Equal(toolCall.TraceId, span.TraceId);
        Assert.Equal(toolCall.SpanId, span.ParentSpanId);
        Assert.Equal(true, span.GetTagItem(Marker));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Same(span, currentInside);
        Assert.Same(span, featureInside);
    }

    [Fact]
    public async Task SendAsync_WritesHostingsEvents_AroundTheRequest_UnderItsSpan()
    {
        HttpContext? contextInside = null;
        using var telemetry = new Telemetry(app => app.Run(context =>
        {
            contextInside = context;
            return Task.CompletedTask;
        }));

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        var span = Assert.Single(telemetry.Spans);
        Assert.Equal([StartEvent, BeginRequestEvent, EndRequestEvent, StopEvent], telemetry.Events.Select(x => x.Name));
        Assert.All(telemetry.Events, x => Assert.Same(span, x.Current));
        Assert.Same(contextInside, telemetry.Events[0].Payload);
        Assert.Same(contextInside, telemetry.Events[3].Payload);
        Assert.Same(contextInside, ReadProperty(telemetry.Events[1].Payload, "httpContext"));
        Assert.IsType<long>(ReadProperty(telemetry.Events[1].Payload, "timestamp"));
        Assert.Same(contextInside, ReadProperty(telemetry.Events[2].Payload, "httpContext"));
    }

    [Fact]
    public async Task SendAsync_WritesHostingsUnhandledExceptionEvent_AndLeavesTheSpansStatusToItsListeners_ForAnEscapedException()
    {
        var thrown = new InvalidOperationException("boom");
        using var telemetry = new Telemetry(app => app.Run(_ => throw thrown));
        int? statusAtStop = null;
        telemetry.OnEvent = (name, payload) =>
        {
            if (name == StopEvent)
            {
                statusAtStop = ((HttpContext)payload!).Response.StatusCode;
            }
        };

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        Assert.Equal([StartEvent, BeginRequestEvent, UnhandledExceptionEvent, StopEvent], telemetry.Events.Select(x => x.Name));
        Assert.Same(thrown, ReadProperty(telemetry.Events[2].Payload, "exception"));
        Assert.Equal(StatusCodes.Status500InternalServerError, statusAtStop);
        Assert.Equal(ActivityStatusCode.Unset, Assert.Single(telemetry.Spans).Status);
    }

    [Fact]
    public async Task SendAsync_MeasuresTheRequest_WithHostingsInstrumentsAndTags()
    {
        using var telemetry = new Telemetry(app =>
        {
            app.UseRouting();
            app.Use((context, next) =>
            {
                context.Features.Get<IHttpMetricsTagsFeature>()!.Tags.Add(new KeyValuePair<string, object?>("app.tag", "app"));
                return next(context);
            });
            app.UseEndpoints(endpoints => endpoints.MapGet(ItemsRoute, (int id) => Results.Ok(id)));
        });

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        var duration = Assert.Single(telemetry.Measurements, x => x.Instrument.Name == RequestDuration);
        Assert.Equal(HostingMeter, duration.Instrument.Meter.Name);
        Assert.Equal("s", duration.Instrument.Unit);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["url.scheme"] = "https",
                ["http.request.method"] = "GET",
                ["network.protocol.version"] = "1.1",
                ["http.response.status_code"] = 200,
                ["http.route"] = ItemsRoute,
                [Marker] = true,
                ["app.tag"] = "app"
            },
            duration.Tags);
        var active = telemetry.Measurements.Where(x => x.Instrument.Name == ActiveRequests).ToList();
        Assert.Equal([1L, -1L], active.Select(x => x.Value));
        Assert.All(active, x => Assert.Equal(new Dictionary<string, object?> { ["url.scheme"] = "https", ["http.request.method"] = "GET" }, x.Tags));
    }

    [Theory]
    [InlineData(false, StatusCodes.Status500InternalServerError, "System.InvalidOperationException")]
    [InlineData(true, StatusCodes.Status413PayloadTooLarge, "Microsoft.AspNetCore.Http.BadHttpRequestException")]
    public async Task SendAsync_MeasuresAnEscapedException_WithTheStatusTheRequestIsAnsweredWith_AndItsType(bool isBadRequest, int status, string errorType)
    {
        using var telemetry = new Telemetry(app => app.Run(_ => throw (isBadRequest
            ? new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge)
            : new InvalidOperationException("boom"))));

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        var duration = Assert.Single(telemetry.Measurements, x => x.Instrument.Name == RequestDuration);
        Assert.Equal(status, duration.Tags["http.response.status_code"]);
        Assert.Equal(errorType, duration.Tags["error.type"]);
        Assert.Contains(telemetry.Logs, x => x.EventId == 2 && x.Message.Contains($" - {status} 0 - "));
    }

    [Fact]
    public async Task SendAsync_KeepsTheErrorTypeAMiddlewareSet_OverThatOfTheEscapedException()
    {
        using var telemetry = new Telemetry(app =>
        {
            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (InvalidOperationException)
                {
                    context.Features.Get<IHttpMetricsTagsFeature>()!.Tags.Add(new KeyValuePair<string, object?>("error.type", "from-middleware"));
                    throw;
                }
            });
            app.Run(_ => throw new InvalidOperationException("boom"));
        });

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        var duration = Assert.Single(telemetry.Measurements, x => x.Instrument.Name == RequestDuration);
        Assert.Equal("from-middleware", duration.Tags["error.type"]);
        Assert.Single(duration.TagList, x => x.Key == "error.type");
    }

    [Fact]
    public async Task SendAsync_MeasuresAndLogsARequestNoMiddlewareServed_AsUnhandled()
    {
        using var telemetry = new Telemetry(app => app.Use((context, next) => next(context)));

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        var duration = Assert.Single(telemetry.Measurements, x => x.Instrument.Name == RequestDuration);
        Assert.Equal(true, duration.Tags["aspnetcore.request.is_unhandled"]);
        Assert.Equal(404, duration.Tags["http.response.status_code"]);
        var unhandled = Assert.Single(telemetry.Logs, x => x.EventId == 16);
        Assert.Equal(HostingCategory, unhandled.Category);
        Assert.Equal(LogLevel.Information, unhandled.Level);
        Assert.Equal(
            "Request reached the end of the middleware pipeline without being handled by application code. Request path: GET https://api.example.com/api/items/7, Response status code: 404",
            unhandled.Message);
    }

    [Fact]
    public async Task SendAsync_RecordsWithHostingsOwnInstruments_SoAListenerSeesOneOfEachName()
    {
        // As HostingMetrics creates them when the host starts, before any request.
        var hostingDuration = default(Histogram<double>);
        var hostingActiveRequests = default(UpDownCounter<long>);
        using var telemetry = new Telemetry(
            app => app.Run(context => Task.CompletedTask),
            beforeFirstRequest: services =>
            {
                var meter = services.GetRequiredService<IMeterFactory>().Create(HostingMeter);
                hostingActiveRequests = meter.CreateUpDownCounter<long>(ActiveRequests, "{request}", "Number of active HTTP server requests.");
                hostingDuration = meter.CreateHistogram<double>(RequestDuration, "s", "Duration of HTTP server requests.");
            });

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        Assert.Single(telemetry.PublishedInstruments, x => x.Name == RequestDuration);
        Assert.Single(telemetry.PublishedInstruments, x => x.Name == ActiveRequests);
        Assert.Same(hostingDuration, Assert.Single(telemetry.Measurements, x => x.Instrument.Name == RequestDuration).Instrument);
        Assert.All(telemetry.Measurements.Where(x => x.Instrument.Name == ActiveRequests), x => Assert.Same(hostingActiveRequests, x.Instrument));
    }

    [Fact]
    public async Task SendAsync_LeavesTheDurationOut_WhenAMiddlewareTurnsTheRequestsMetricsOff_OnAnAspNetCoreThatCan()
    {
        // This net8.0 build runs on the ASP.NET Core the tests run on, whose interface has MetricsDisabled from 9 on.
        var metricsDisabled = typeof(IHttpMetricsTagsFeature).GetProperty("MetricsDisabled");
        using var telemetry = new Telemetry(app => app.Run(context =>
        {
            metricsDisabled?.SetValue(context.Features.Get<IHttpMetricsTagsFeature>(), true);
            return Task.CompletedTask;
        }));

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        Assert.Equal(metricsDisabled is null ? 1 : 0, telemetry.Measurements.Count(x => x.Instrument.Name == RequestDuration));
        Assert.Equal([1L, -1L], telemetry.Measurements.Where(x => x.Instrument.Name == ActiveRequests).Select(x => x.Value));
    }

    [Fact]
    public async Task SendAsync_SetsNoMetricsTagsFeature_AndMeasuresNothing_WhenNoListenerMeasures()
    {
        IHttpMetricsTagsFeature? featureInside = null;
        using var telemetry = new Telemetry(
            app => app.Run(context =>
            {
                featureInside = context.Features.Get<IHttpMetricsTagsFeature>();
                return Task.CompletedTask;
            }),
            listensToMeters: false);

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        Assert.Null(featureInside);
    }

    [Fact]
    public async Task SendAsync_LogsRequestStartingAndFinished_AsHostingDoes_UnderTheRequestsScopeAndSpan()
    {
        string? traceIdentifier = null;
        using var telemetry = new Telemetry(app => app.Run(async context =>
        {
            traceIdentifier = context.TraceIdentifier;
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("created");
        }));
        var caller = new DefaultHttpContext();
        caller.Request.PathBase = "/v1";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/api/items/7")
        {
            Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json")
        };

        using var response = await telemetry.SendAsync(request, caller);

        var span = Assert.Single(telemetry.Spans);
        var starting = Assert.Single(telemetry.Logs, x => x.EventId == 1);
        Assert.Equal(HostingCategory, starting.Category);
        Assert.Equal(LogLevel.Information, starting.Level);
        Assert.Equal("Request starting HTTP/1.1 POST https://api.example.com/v1/api/items/7 - application/json;+charset=utf-8 7", starting.Message);
        Assert.Equal("Request starting {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {ContentType} {ContentLength}", starting.State["{OriginalFormat}"]);
        Assert.Equal("POST", starting.State["Method"]);
        Assert.Equal("/v1", starting.State["PathBase"]);
        Assert.Equal(7L, starting.State["ContentLength"]);
        var finished = Assert.Single(telemetry.Logs, x => x.EventId == 2);
        Assert.Equal(HostingCategory, finished.Category);
        Assert.Matches(new Regex(@"^Request finished HTTP/1\.1 POST https://api\.example\.com/v1/api/items/7 - 201 - text/plain;\+charset=utf-8 \d+\.\d{4}ms$"), finished.Message);
        Assert.Equal("Request finished {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {StatusCode} {ContentLength} {ContentType} {ElapsedMilliseconds}ms", finished.State["{OriginalFormat}"]);
        Assert.Equal(201, finished.State["StatusCode"]);
        Assert.IsType<double>(finished.State["ElapsedMilliseconds"]);
        Assert.All([starting, finished], x =>
        {
            Assert.Equal(span.SpanId.ToHexString(), x.SpanId);
            Assert.Equal(traceIdentifier, x.Scope["RequestId"]);
            Assert.Equal("/v1/api/items/7", x.Scope["RequestPath"]);
            Assert.Equal(true, x.Scope[Marker]);
            Assert.Equal($"RequestPath:/v1/api/items/7 RequestId:{traceIdentifier} {Marker}:true", x.ScopeText);
        });
    }

    [Fact]
    public async Task SendAsync_EndsTheTelemetryOfACanceledCall_WhenTheAppEnds_With499AndNoError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var telemetry = new Telemetry(app => app.Run(async context =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }));
        using var cancellation = new CancellationTokenSource();

        var sending = telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await telemetry.RequestEnded.WaitAsync(TimeSpan.FromSeconds(10));

        var duration = Assert.Single(telemetry.Measurements, x => x.Instrument.Name == RequestDuration);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, duration.Tags["http.response.status_code"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));
        Assert.Equal(0L, telemetry.Measurements.Where(x => x.Instrument.Name == ActiveRequests).Sum(x => (long)x.Value));
        Assert.Equal([StartEvent, BeginRequestEvent, EndRequestEvent, StopEvent], telemetry.Events.Select(x => x.Name));
        Assert.Contains(telemetry.Logs, x => x.EventId == 2 && x.Message.Contains(" - 499 - - "));
    }

    [Fact]
    public async Task SendAsync_KeepsTheStatusOfACanceledCall_WhoseResponseStarted()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var telemetry = new Telemetry(app => app.Run(async context =>
        {
            await context.Response.WriteAsync("partial");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }));
        using var cancellation = new CancellationTokenSource();

        var sending = telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl), cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await telemetry.RequestEnded.WaitAsync(TimeSpan.FromSeconds(10));

        var duration = Assert.Single(telemetry.Measurements, x => x.Instrument.Name == RequestDuration);
        Assert.Equal(StatusCodes.Status200OK, duration.Tags["http.response.status_code"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));
    }

    [Fact]
    public async Task SendAsync_StartsTheSpanForHostingsLogs_WhenNothingListensToIt()
    {
        Activity? currentInside = null;
        using var telemetry = new Telemetry(
            app => app.Run(context =>
            {
                currentInside = Activity.Current;
                return Task.CompletedTask;
            }),
            listensToActivities: false,
            subscribesToEvents: false);
        using var toolCall = new Activity("tool call").Start();

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        Assert.NotNull(currentInside);
        Assert.Equal(ActivityName, currentInside.OperationName);
        Assert.Same(toolCall, currentInside.Parent);
        Assert.Equal(true, currentInside.GetTagItem(Marker));
    }

    [Fact]
    public async Task SendAsync_StartsNoSpan_AndNoScope_WhenNothingListensAndHostingsLogIsOff()
    {
        Activity? currentInside = null;
        using var telemetry = new Telemetry(
            app => app.Run(context =>
            {
                currentInside = Activity.Current;
                context.RequestServices.GetRequiredService<ILogger<InProcessRequestDiagnosticsTests>>().LogInformation("inside");
                return Task.CompletedTask;
            }),
            listensToActivities: false,
            subscribesToEvents: false,
            configureLogging: logging => logging.AddFilter(HostingCategory, LogLevel.None));
        using var toolCall = new Activity("tool call").Start();

        using var response = await telemetry.SendAsync(new HttpRequestMessage(HttpMethod.Get, ItemsUrl));

        Assert.Same(toolCall, currentInside);
        var inside = Assert.Single(telemetry.Logs, x => x.Message == "inside");
        Assert.Empty(inside.Scope);
        Assert.DoesNotContain(telemetry.Logs, x => x.Category == HostingCategory);
    }

    private static object? ReadProperty(object? payload, string name)
    {
        return payload!.GetType().GetProperty(name)!.GetValue(payload);
    }

    private sealed record DiagnosticEvent(string Name, object? Payload, Activity? Current);

    private sealed record Measurement(Instrument Instrument, object Value, KeyValuePair<string, object?>[] TagList)
    {
        public Dictionary<string, object?> Tags => TagList.ToDictionary(x => x.Key, x => x.Value);
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        int EventId,
        string Message,
        Dictionary<string, object?> State,
        Dictionary<string, object?> Scope,
        string? ScopeText,
        string? SpanId);

    /// <summary>
    /// A pipeline with the host's <see cref="ActivitySource"/>, <see cref="DiagnosticListener"/> and
    /// <see cref="IMeterFactory"/>, and what a listener of each recorded of its requests. The listeners take only this
    /// pipeline's instances, as other tests run at the same time.
    /// </summary>
    private sealed class Telemetry : IDisposable, IObserver<KeyValuePair<string, object?>>
    {
        private readonly ServiceProvider _services;
        private readonly InProcessApiPipeline _pipeline;
        private readonly ActivityListener? _activityListener;
        private readonly MeterListener? _meterListener;
        private readonly IDisposable? _eventSubscription;
        private readonly LoggerProvider _loggerProvider = new();
        private readonly List<Activity> _spans = [];
        private readonly List<DiagnosticEvent> _events = [];
        private readonly List<Measurement> _measurements = [];
        private readonly List<Instrument> _publishedInstruments = [];
        private readonly TaskCompletionSource _requestEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Telemetry(
            Action<IApplicationBuilder> configure,
            bool listensToActivities = true,
            bool subscribesToEvents = true,
            bool listensToMeters = true,
            Action<ILoggingBuilder>? configureLogging = null,
            Action<IServiceProvider>? beforeFirstRequest = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(logging =>
            {
                logging.AddProvider(_loggerProvider);
                configureLogging?.Invoke(logging);
            });
            services.AddSingleton(DiagnosticListener);
            services.AddSingleton(ActivitySource);
            services.AddMetrics();
            services.AddRouting();
            _services = services.BuildServiceProvider();

            if (listensToActivities)
            {
                _activityListener = new ActivityListener
                {
                    ShouldListenTo = source => ReferenceEquals(source, ActivitySource),
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = OnActivityStopped
                };
                ActivitySource.AddActivityListener(_activityListener);
            }

            if (subscribesToEvents)
            {
                _eventSubscription = DiagnosticListener.Subscribe(this);
            }

            if (listensToMeters)
            {
                var meterFactory = _services.GetRequiredService<IMeterFactory>();
                _meterListener = new MeterListener
                {
                    InstrumentPublished = (instrument, listener) =>
                    {
                        if (ReferenceEquals(instrument.Meter.Scope, meterFactory))
                        {
                            lock (_publishedInstruments)
                            {
                                _publishedInstruments.Add(instrument);
                            }

                            listener.EnableMeasurementEvents(instrument);
                        }
                    }
                };
                _meterListener.SetMeasurementEventCallback<double>(OnMeasurement);
                _meterListener.SetMeasurementEventCallback<long>(OnMeasurement);
                _meterListener.Start();
            }

            beforeFirstRequest?.Invoke(_services);
            _pipeline = new InProcessApiPipeline(
                new DefaultHttpContextFactory(_services),
                _services.GetRequiredService<ILogger<InProcessApiPipeline>>(),
                InProcessRequestDiagnostics.Create(_services));
            var builder = new PipelineCapturingApplicationBuilderFactory(new ApplicationBuilderFactory(_services), _pipeline)
                .CreateBuilder(new FeatureCollection());
            configure(builder);
            builder.Build();
        }

        public ActivitySource ActivitySource { get; } = new("Microsoft.AspNetCore");

        public DiagnosticListener DiagnosticListener { get; } = new("Microsoft.AspNetCore");

        public Action<string, object?>? OnEvent { get; set; }

        /// <summary>
        /// Completes when the request's span stops, the last of its telemetry, which a canceled call's request records
        /// after the call ended.
        /// </summary>
        public Task RequestEnded => _requestEnded.Task;

        public IReadOnlyList<Activity> Spans => Snapshot(_spans);

        public IReadOnlyList<DiagnosticEvent> Events => Snapshot(_events);

        public IReadOnlyList<Measurement> Measurements => Snapshot(_measurements);

        public IReadOnlyList<Instrument> PublishedInstruments => Snapshot(_publishedInstruments);

        public IReadOnlyList<LogEntry> Logs => Snapshot(_loggerProvider.Entries);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpContext? caller = null, CancellationToken cancellationToken = default)
        {
            return _pipeline.SendAsync(request, caller, cancellationToken);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            lock (_events)
            {
                _events.Add(new DiagnosticEvent(value.Key, value.Value, Activity.Current));
            }

            OnEvent?.Invoke(value.Key, value.Value);
        }

        public void Dispose()
        {
            _activityListener?.Dispose();
            _meterListener?.Dispose();
            _eventSubscription?.Dispose();
            _services.Dispose();
            DiagnosticListener.Dispose();
            ActivitySource.Dispose();
        }

        private void OnActivityStopped(Activity activity)
        {
            lock (_spans)
            {
                _spans.Add(activity);
            }

            _requestEnded.TrySetResult();
        }

        private void OnMeasurement<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            lock (_measurements)
            {
                _measurements.Add(new Measurement(instrument, value!, tags.ToArray()));
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
    /// Records each entry with its category, event id, state and scope, and the span it was logged under.
    /// </summary>
    private sealed class LoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopeProvider;

        public List<LogEntry> Entries { get; } = [];

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
            private readonly LoggerProvider _provider;
            private readonly string _category;

            public Logger(LoggerProvider provider, string category)
            {
                _provider = provider;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return _provider._scopeProvider?.Push(state);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel != LogLevel.None;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = new Dictionary<string, object?>();
                var scopeTexts = new List<string>();
                _provider._scopeProvider?.ForEachScope((scope, _) =>
                {
                    var pairs = scope as IEnumerable<KeyValuePair<string, object>>;
                    if (pairs is not null)
                    {
                        foreach (var (key, value) in pairs)
                        {
                            values[key] = value;
                        }

                        scopeTexts.Add(scope!.ToString()!);
                    }
                }, (object?)null);

                var stateValues = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(x => x.Key, x => x.Value) ?? [];
                var entry = new LogEntry(
                    _category,
                    logLevel,
                    eventId.Id,
                    formatter(state, exception),
                    stateValues,
                    values,
                    scopeTexts.LastOrDefault(),
                    Activity.Current?.SpanId.ToHexString());
                lock (_provider.Entries)
                {
                    _provider.Entries.Add(entry);
                }
            }
        }
    }
}
