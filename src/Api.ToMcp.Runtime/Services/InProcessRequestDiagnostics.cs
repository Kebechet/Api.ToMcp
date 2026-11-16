using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Gives the tool's in-process API request the telemetry ASP.NET Core's hosting gives a request the server receives, as
/// its internal <c>HostingApplicationDiagnostics</c> of the ASP.NET Core the runtime is built for does, through hosting's
/// own instruments: the server activity <c>Microsoft.AspNetCore.Hosting.HttpRequestIn</c> of the host's
/// <see cref="ActivitySource"/>, with the events of the host's <see cref="DiagnosticListener"/>; the instruments
/// <c>http.server.request.duration</c> and <c>http.server.active_requests</c> of the meter
/// <c>Microsoft.AspNetCore.Hosting</c>; and the entries "Request starting" and "Request finished" of the log category
/// <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>, under the request's log scope. The activity, the duration and the
/// scope carry <see cref="ToolRequestMarker"/>.
/// </summary>
/// <remarks>
/// Unlike hosting's, the activity is always a child of the tool call's, which the request runs under, and never of a
/// <c>traceparent</c> header. Hosting's <c>EventSource</c> counters do not count the request: that source is internal.
/// </remarks>
internal sealed class InProcessRequestDiagnostics
{
    /// <summary>
    /// The tag, with the value <see langword="true"/>, that tells the tool's request apart from one the server received.
    /// </summary>
    internal const string ToolRequestMarker = "api_to_mcp.tool_request";

    private const string RootRoute = "/";
    private const string Http11Version = "1.1";
    private const string Http2Version = "2";
    private const string Http3Version = "3";
    private const string Http10Version = "1.0";
    private const string Http09Version = "0.9";
    private const int DefaultHttpPort = 80;
    private const int DefaultHttpsPort = 443;

    private static readonly Dictionary<string, string> KnownHttpMethods = new[]
    {
        HttpMethods.Connect,
        HttpMethods.Delete,
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Options,
        HttpMethods.Patch,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Trace
    }.ToDictionary(x => x, StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;
    private readonly DiagnosticListener? _diagnosticListener;
    private readonly ActivitySource? _activitySource;
    private readonly Lazy<HostingInstruments?> _instruments;

    public InProcessRequestDiagnostics(
        ILoggerFactory loggerFactory,
        DiagnosticListener? diagnosticListener,
        ActivitySource? activitySource,
        IMeterFactory? meterFactory)
    {
        _logger = loggerFactory.CreateLogger(HostingTelemetryNames.LoggerCategory);
        _diagnosticListener = diagnosticListener;
        _activitySource = activitySource;

        // Taken at the first request rather than now: the host resolves this before it creates hosting's instruments,
        // which then have to be hosting's own, with its advice.
        _instruments = new Lazy<HostingInstruments?>(() => meterFactory is null ? null : new HostingInstruments(meterFactory));

#if NET10_0_OR_GREATER
        var isSwitchSet = AppContext.TryGetSwitch(HostingTelemetryNames.SuppressActivityOpenTelemetryDataSwitch, out var isSuppressed);
        SuppressActivityOpenTelemetryData = !isSwitchSet || isSuppressed;
#endif
    }

#if NET10_0_OR_GREATER
    /// <summary>
    /// Whether the activity goes without the tags of OpenTelemetry's semantic conventions when it is created, which ASP.NET
    /// Core 10's hosting gives it only when the app turns the switch it is read from off.
    /// </summary>
    internal bool SuppressActivityOpenTelemetryData { get; set; }
#endif

    /// <summary>
    /// Takes the host's instruments from its services: the web host registers the <see cref="DiagnosticListener"/>, the
    /// <see cref="ActivitySource"/> and the <see cref="IMeterFactory"/> hosting resolves, and without one of them that
    /// part of the telemetry is left out.
    /// </summary>
    public static InProcessRequestDiagnostics Create(IServiceProvider services)
    {
        return new InProcessRequestDiagnostics(
            services.GetRequiredService<ILoggerFactory>(),
            services.GetService<DiagnosticListener>(),
            services.GetService<ActivitySource>(),
            services.GetService<IMeterFactory>());
    }

    /// <summary>
    /// Starts the request's telemetry as hosting does once it created the request's <see cref="HttpContext"/>, before the
    /// app runs: the active request, the activity and its events, the log scope and "Request starting".
    /// </summary>
    public RequestState BeginRequest(HttpContext httpContext)
    {
        var state = new RequestState(Stopwatch.GetTimestamp());

        var instruments = _instruments.Value;
        if (instruments is not null && instruments.IsEnabled)
        {
            var request = httpContext.Request;
            state.MetricsTags = new MetricsTagsFeature(request.Method, request.Scheme, request.Protocol);
            state.MetricsTags.Tags.Add(new KeyValuePair<string, object?>(ToolRequestMarker, true));
            httpContext.Features.Set<IHttpMetricsTagsFeature>(state.MetricsTags);
            instruments.ActiveRequests.Add(1, CreateRequestTags(state.MetricsTags));
        }

        var isDiagnosticListenerEnabled = _diagnosticListener is not null && _diagnosticListener.IsEnabled();
        var isActivityCreatedForTheListener = isDiagnosticListenerEnabled && _diagnosticListener!.IsEnabled(HostingTelemetryNames.ActivityName, httpContext);
        var isLoggingEnabled = _logger.IsEnabled(LogLevel.Critical);

        state.Activity = StartActivity(httpContext, isLoggingEnabled || isActivityCreatedForTheListener, out var hasDiagnosticListener);
        state.HasDiagnosticListener = hasDiagnosticListener;
        if (state.Activity is not null)
        {
            httpContext.Features.Set<IHttpActivityFeature>(new HttpActivityFeature(state.Activity));
        }

        if (isDiagnosticListenerEnabled && _diagnosticListener!.IsEnabled(HostingTelemetryNames.BeginRequestEvent))
        {
            _diagnosticListener.Write(HostingTelemetryNames.BeginRequestEvent, new RequestEventPayload(httpContext, state.StartTimestamp));
        }

        if (isLoggingEnabled)
        {
            state.Scope = _logger.BeginScope(new HostingLogScope(httpContext));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                state.IsStartLogged = true;
                _logger.Log(LogLevel.Information, HostingTelemetryNames.RequestStartingEventId, HostingLogEntry.RequestStarting(httpContext.Request), null, HostingLogEntry.Formatter);
            }
        }

        return state;
    }

    /// <summary>
    /// Ends the request's telemetry as hosting does once the app ended and the response's <c>OnCompleted</c> callbacks ran:
    /// "Request finished", the request's measurements, the events, the end of the activity and of the log scope.
    /// </summary>
    /// <param name="httpContext">The request, with the status the server answers it with.</param>
    /// <param name="state">What <see cref="BeginRequest"/> started.</param>
    /// <param name="exception">The exception the server reports for the request, if any.</param>
    public void EndRequest(HttpContext httpContext, RequestState state, Exception? exception)
    {
        var endTimestamp = Stopwatch.GetTimestamp();
        var isUnhandled = httpContext.Items.ContainsKey(HostingTelemetryNames.RequestUnhandledItem);

        if (state.IsStartLogged)
        {
            var elapsed = Stopwatch.GetElapsedTime(state.StartTimestamp, endTimestamp);
            _logger.Log(LogLevel.Information, HostingTelemetryNames.RequestFinishedEventId, HostingLogEntry.RequestFinished(httpContext, elapsed), null, HostingLogEntry.Formatter);
        }

        if (state.MetricsTags is not null)
        {
            RecordRequestEnd(httpContext, state, state.MetricsTags, isUnhandled, exception, endTimestamp);
        }

        if (isUnhandled)
        {
            _logger.Log(LogLevel.Information, HostingTelemetryNames.RequestUnhandledEventId, HostingLogEntry.RequestUnhandled(httpContext), null, HostingLogEntry.Formatter);
        }

        if (_diagnosticListener is not null && _diagnosticListener.IsEnabled())
        {
            if (exception is null)
            {
                if (_diagnosticListener.IsEnabled(HostingTelemetryNames.EndRequestEvent))
                {
                    _diagnosticListener.Write(HostingTelemetryNames.EndRequestEvent, new RequestEventPayload(httpContext, endTimestamp));
                }
            }
            else if (_diagnosticListener.IsEnabled(HostingTelemetryNames.UnhandledExceptionEvent))
            {
                _diagnosticListener.Write(HostingTelemetryNames.UnhandledExceptionEvent, new UnhandledExceptionPayload(httpContext, endTimestamp, exception));
            }
        }

        // Stopped after the duration is recorded, as hosting does, so a listener can take the activity as an exemplar.
        if (state.Activity is not null)
        {
            StopActivity(httpContext, state.Activity, state.HasDiagnosticListener);
        }

        state.Scope?.Dispose();
    }

    private Activity? StartActivity(HttpContext httpContext, bool isDiagnosticsOrLoggingEnabled, out bool hasDiagnosticListener)
    {
        hasDiagnosticListener = false;
        if (_activitySource is null)
        {
            return null;
        }

        var tags = CreateActivityTags(httpContext);

        // A default parent context makes the tool call's activity, the current one, the parent.
        var activity = _activitySource.HasListeners()
            ? _activitySource.CreateActivity(HostingTelemetryNames.ActivityName, ActivityKind.Server, default(ActivityContext), tags)
            : null;
        if (activity is null)
        {
            if (!isDiagnosticsOrLoggingEnabled)
            {
                return null;
            }

            activity = new Activity(HostingTelemetryNames.ActivityName);
            foreach (var tag in tags)
            {
                activity.AddTag(tag.Key, tag.Value);
            }
        }

        _diagnosticListener?.OnActivityImport(activity, httpContext);
        activity.Start();
        if (_diagnosticListener is not null && _diagnosticListener.IsEnabled(HostingTelemetryNames.ActivityStartEvent))
        {
            hasDiagnosticListener = true;
            _diagnosticListener.Write(HostingTelemetryNames.ActivityStartEvent, httpContext);
        }

        return activity;
    }

    private void StopActivity(HttpContext httpContext, Activity activity, bool hasDiagnosticListener)
    {
        if (hasDiagnosticListener)
        {
            // The listener reads the duration in the event, before Stop sets it.
            if (activity.Duration == TimeSpan.Zero)
            {
                activity.SetEndTime(DateTime.UtcNow);
            }

            _diagnosticListener!.Write(HostingTelemetryNames.ActivityStopEvent, httpContext);
        }

        activity.Stop();
    }

    private TagList CreateActivityTags(HttpContext httpContext)
    {
        var tags = new TagList();
#if NET10_0_OR_GREATER
        if (!SuppressActivityOpenTelemetryData)
        {
            AddOpenTelemetryActivityTags(ref tags, httpContext.Request);
        }
#endif
        tags.Add(ToolRequestMarker, true);
        return tags;
    }

#if NET10_0_OR_GREATER
    /// <summary>
    /// The tags ASP.NET Core 10's hosting gives the activity when it creates it, for a sampler to read, unless
    /// <see cref="SuppressActivityOpenTelemetryData"/>.
    /// </summary>
    private static void AddOpenTelemetryActivityTags(ref TagList tags, HttpRequest request)
    {
        if (request.Host.HasValue)
        {
            tags.Add(HostingTelemetryNames.ServerAddress, request.Host.Host);
            var port = request.Host.Port ?? GetDefaultPort(request.Scheme);
            if (port is not null)
            {
                tags.Add(HostingTelemetryNames.ServerPort, port.Value);
            }
        }

        var method = GetNormalizedHttpMethod(request.Method);
        tags.Add(HostingTelemetryNames.HttpRequestMethod, method);
        if (request.Method != method)
        {
            tags.Add(HostingTelemetryNames.HttpRequestMethodOriginal, request.Method);
        }

        var userAgent = request.Headers.UserAgent;
        if (userAgent.Count > 0 && !string.IsNullOrEmpty(userAgent[0]))
        {
            tags.Add(HostingTelemetryNames.UserAgentOriginal, userAgent[0]);
        }

        tags.Add(HostingTelemetryNames.UrlScheme, request.Scheme);
        var path = request.PathBase.HasValue || request.Path.HasValue ? (request.PathBase + request.Path).ToString() : RootRoute;
        tags.Add(HostingTelemetryNames.UrlPath, path);
    }

    private static int? GetDefaultPort(string scheme)
    {
        if (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultHttpPort;
        }

        if (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultHttpsPort;
        }

        return null;
    }
#endif

    private void RecordRequestEnd(HttpContext httpContext, RequestState state, MetricsTagsFeature metricsTags, bool isUnhandled, Exception? exception, long endTimestamp)
    {
        var instruments = _instruments.Value!;
        var tags = CreateRequestTags(metricsTags);
        if (instruments.ActiveRequests.Enabled)
        {
            instruments.ActiveRequests.Add(-1, tags);
        }

        var endpoint = GetOriginalEndpoint(httpContext);
        if (IsDurationDisabled(endpoint, metricsTags) || !instruments.RequestDuration.Enabled)
        {
            return;
        }

        var version = GetHttpVersion(metricsTags.Protocol);
        if (version is not null)
        {
            tags.Add(HostingTelemetryNames.NetworkProtocolVersion, version);
        }

        if (isUnhandled)
        {
            tags.Add(HostingTelemetryNames.IsUnhandled, true);
        }

        tags.Add(HostingTelemetryNames.HttpResponseStatusCode, httpContext.Response.StatusCode);
        var route = endpoint?.Metadata.GetMetadata<IRouteDiagnosticsMetadata>()?.Route;
        if (route is not null)
        {
#if NET10_0_OR_GREATER
            route = string.IsNullOrEmpty(route) ? RootRoute : route;
#endif
            tags.Add(HostingTelemetryNames.HttpRoute, route);
        }

        // Before error.type, so a middleware's own error.type, such as the exception handler's, wins.
        foreach (var tag in metricsTags.TagsList)
        {
            tags.Add(tag);
        }

        if (exception is not null && !HasTag(tags, HostingTelemetryNames.ErrorType))
        {
            tags.Add(HostingTelemetryNames.ErrorType, exception.GetType().FullName);
        }

        instruments.RequestDuration.Record(Stopwatch.GetElapsedTime(state.StartTimestamp, endTimestamp).TotalSeconds, tags);
    }

    private static bool IsDurationDisabled(Endpoint? endpoint, MetricsTagsFeature metricsTags)
    {
#if NET9_0_OR_GREATER
        if (endpoint?.Metadata.GetMetadata<IDisableHttpMetricsMetadata>() is not null)
        {
            return true;
        }
#endif
        return metricsTags.MetricsDisabled;
    }

    private static TagList CreateRequestTags(MetricsTagsFeature metricsTags)
    {
        return new TagList
        {
            { HostingTelemetryNames.UrlScheme, metricsTags.Scheme },
            { HostingTelemetryNames.HttpRequestMethod, GetNormalizedHttpMethod(metricsTags.Method) }
        };
    }

    private static Endpoint? GetOriginalEndpoint(HttpContext httpContext)
    {
        var endpoint = httpContext.GetEndpoint();
        if (endpoint is null && httpContext.Items.TryGetValue(HostingTelemetryNames.OriginalEndpointItem, out var item))
        {
            endpoint = item as Endpoint;
        }

        return endpoint;
    }

    private static bool HasTag(TagList tags, string name)
    {
        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i].Key == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetNormalizedHttpMethod(string method)
    {
        return KnownHttpMethods.TryGetValue(method, out var knownMethod) ? knownMethod : HostingTelemetryNames.OtherHttpMethod;
    }

    private static string? GetHttpVersion(string protocol)
    {
        if (HttpProtocol.IsHttp11(protocol))
        {
            return Http11Version;
        }

        if (HttpProtocol.IsHttp2(protocol))
        {
            return Http2Version;
        }

        if (HttpProtocol.IsHttp3(protocol))
        {
            return Http3Version;
        }

        if (HttpProtocol.IsHttp10(protocol))
        {
            return Http10Version;
        }

        return HttpProtocol.IsHttp09(protocol) ? Http09Version : null;
    }

    /// <summary>
    /// What <see cref="BeginRequest"/> started for a request, for <see cref="EndRequest"/> to end.
    /// </summary>
    public sealed class RequestState
    {
        public RequestState(long startTimestamp)
        {
            StartTimestamp = startTimestamp;
        }

        public long StartTimestamp { get; }

        public Activity? Activity { get; set; }

        public bool HasDiagnosticListener { get; set; }

        public IDisposable? Scope { get; set; }

        public bool IsStartLogged { get; set; }

        /// <summary>
        /// The request's metric tags, when a listener measures the request.
        /// </summary>
        public MetricsTagsFeature? MetricsTags { get; set; }
    }

    /// <summary>
    /// Hosting's instruments. A meter of <see cref="IMeterFactory"/> is the one the factory gave hosting for the same
    /// name, and a meter hands back the instrument it already has of the same type, name, unit, description and tags, so
    /// these are the very instruments hosting measures a request the server receives with: a listener sees one instrument
    /// of each name, and each request is measured once.
    /// </summary>
    private sealed class HostingInstruments
    {
#if NET9_0_OR_GREATER
        private static readonly double[] RequestDurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];
#endif

        public HostingInstruments(IMeterFactory meterFactory)
        {
            var meter = meterFactory.Create(HostingTelemetryNames.MeterName);
            ActiveRequests = meter.CreateUpDownCounter<long>(
                HostingTelemetryNames.ActiveRequests,
                HostingTelemetryNames.ActiveRequestsUnit,
                HostingTelemetryNames.ActiveRequestsDescription);
#if NET9_0_OR_GREATER
            RequestDuration = meter.CreateHistogram(
                HostingTelemetryNames.RequestDuration,
                HostingTelemetryNames.RequestDurationUnit,
                HostingTelemetryNames.RequestDurationDescription,
                advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = RequestDurationBuckets });
#else
            RequestDuration = meter.CreateHistogram<double>(
                HostingTelemetryNames.RequestDuration,
                HostingTelemetryNames.RequestDurationUnit,
                HostingTelemetryNames.RequestDurationDescription);
#endif
        }

        public UpDownCounter<long> ActiveRequests { get; }

        public Histogram<double> RequestDuration { get; }

        public bool IsEnabled => ActiveRequests.Enabled || RequestDuration.Enabled;
    }

    /// <summary>
    /// The tags of the request's duration, to which the app's middleware adds its own as to those of a request the server
    /// received, and the method, scheme and protocol of the request as it began, which tag the active request and its end.
    /// </summary>
    /// <remarks>
    /// Not sealed, for <see cref="MetricsDisabled"/> to be virtual: ASP.NET Core 9 added the property to the interface as
    /// abstract, so the runtime's net8.0 build, run on ASP.NET Core 9 or later, fails to load a class that does not
    /// implement it, and the runtime binds an interface member the build did not know to a public virtual one of the same
    /// name and signature.
    /// </remarks>
    public class MetricsTagsFeature : IHttpMetricsTagsFeature
    {
        public MetricsTagsFeature(string method, string scheme, string protocol)
        {
            Method = method;
            Scheme = scheme;
            Protocol = protocol;
        }

        public ICollection<KeyValuePair<string, object?>> Tags => TagsList;

        public List<KeyValuePair<string, object?>> TagsList { get; } = [];

        public virtual bool MetricsDisabled { get; set; }

        public string Method { get; }

        public string Scheme { get; }

        public string Protocol { get; }
    }

    private sealed class HttpActivityFeature : IHttpActivityFeature
    {
        public HttpActivityFeature(Activity activity)
        {
            Activity = activity;
        }

        public Activity Activity { get; set; }
    }

    /// <summary>
    /// The payload of hosting's <c>BeginRequest</c> and <c>EndRequest</c> events, whose subscribers read its properties by
    /// these names.
    /// </summary>
    private sealed class RequestEventPayload
    {
        public RequestEventPayload(HttpContext httpContext, long timestamp)
        {
            this.httpContext = httpContext;
            this.timestamp = timestamp;
        }

        public HttpContext httpContext { get; }

        public long timestamp { get; }

        public override string ToString()
        {
            return $"{{ {nameof(httpContext)} = {httpContext}, {nameof(timestamp)} = {timestamp} }}";
        }
    }

    /// <summary>
    /// The payload of hosting's <c>UnhandledException</c> event, whose subscribers read its properties by these names.
    /// </summary>
    private sealed class UnhandledExceptionPayload
    {
        public UnhandledExceptionPayload(HttpContext httpContext, long timestamp, Exception exception)
        {
            this.httpContext = httpContext;
            this.timestamp = timestamp;
            this.exception = exception;
        }

        public HttpContext httpContext { get; }

        public long timestamp { get; }

        public Exception exception { get; }

        public override string ToString()
        {
            return $"{{ {nameof(httpContext)} = {httpContext}, {nameof(timestamp)} = {timestamp}, {nameof(exception)} = {exception} }}";
        }
    }
}
