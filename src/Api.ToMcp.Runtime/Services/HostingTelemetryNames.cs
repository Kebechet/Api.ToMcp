namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The names ASP.NET Core's hosting gives the telemetry of a request the server receives, mirrored from its internal
/// <c>HostingApplicationDiagnostics</c>, <c>HostingMetrics</c>, <c>HostingTelemetryHelpers</c> and <c>LoggerEventIds</c>,
/// which use the same ones in ASP.NET Core 8, 9 and 10.
/// </summary>
internal static class HostingTelemetryNames
{
    public const string ActivityName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
    public const string ActivityStartEvent = ActivityName + ".Start";
    public const string ActivityStopEvent = ActivityName + ".Stop";
    public const string BeginRequestEvent = "Microsoft.AspNetCore.Hosting.BeginRequest";
    public const string EndRequestEvent = "Microsoft.AspNetCore.Hosting.EndRequest";
    public const string UnhandledExceptionEvent = "Microsoft.AspNetCore.Hosting.UnhandledException";

    /// <summary>
    /// The switch that keeps ASP.NET Core 10's hosting from tagging the activity as OpenTelemetry's semantic conventions
    /// say when it creates it; on unless the app sets it to <see langword="false"/>.
    /// </summary>
    public const string SuppressActivityOpenTelemetryDataSwitch = "Microsoft.AspNetCore.Hosting.SuppressActivityOpenTelemetryData";

    public const string MeterName = "Microsoft.AspNetCore.Hosting";
    public const string ActiveRequests = "http.server.active_requests";
    public const string ActiveRequestsUnit = "{request}";
    public const string ActiveRequestsDescription = "Number of active HTTP server requests.";
    public const string RequestDuration = "http.server.request.duration";
    public const string RequestDurationUnit = "s";
    public const string RequestDurationDescription = "Duration of HTTP server requests.";

    public const string UrlScheme = "url.scheme";
    public const string UrlPath = "url.path";
    public const string HttpRequestMethod = "http.request.method";
    public const string HttpRequestMethodOriginal = "http.request.method_original";
    public const string HttpResponseStatusCode = "http.response.status_code";
    public const string HttpRoute = "http.route";
    public const string NetworkProtocolVersion = "network.protocol.version";
    public const string ServerAddress = "server.address";
    public const string ServerPort = "server.port";
    public const string UserAgentOriginal = "user_agent.original";
    public const string ErrorType = "error.type";
    public const string IsUnhandled = "aspnetcore.request.is_unhandled";

    /// <summary>
    /// The method a tag names for a method other than the nine of HTTP, as OpenTelemetry's semantic conventions say.
    /// </summary>
    public const string OtherHttpMethod = "_OTHER";

    public const string LoggerCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
    public const int RequestStartingEventId = 1;
    public const int RequestFinishedEventId = 2;
    public const int RequestUnhandledEventId = 16;

    /// <summary>
    /// The item the end of the app's pipeline sets on a request no middleware or endpoint served.
    /// </summary>
    public const string RequestUnhandledItem = "__RequestUnhandled";

    /// <summary>
    /// The item in which a middleware that runs the pipeline again, such as the exception handler, keeps the endpoint the
    /// request had matched before it cleared it.
    /// </summary>
    public const string OriginalEndpointItem = "__OriginalEndpoint";
}
