using System.Collections;
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The log entries ASP.NET Core's hosting writes for a request the server receives, with the same message, the same
/// template under <c>{OriginalFormat}</c> and the same values under the same names, as its internal
/// <c>HostingRequestStartingLog</c>, <c>HostingRequestFinishedLog</c> and <c>HostingRequestUnhandledLog</c> write them
/// in ASP.NET Core 8, 9 and 10. The values are read when the entry is created, while the request is still being served.
/// </summary>
internal sealed class HostingLogEntry : IReadOnlyList<KeyValuePair<string, object?>>
{
    public static readonly Func<HostingLogEntry, Exception?, string> Formatter = (entry, _) => entry._message;

    private const string OriginalFormatKey = "{OriginalFormat}";
    private const string ElapsedMillisecondsKey = "ElapsedMilliseconds";
    private const string EmptyEntry = "-";
    private const string ElapsedMillisecondsFormat = "0.0000";
    private const string RequestStartingFormat = "Request starting {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {ContentType} {ContentLength}";
    private const string RequestFinishedFormat = "Request finished {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {StatusCode} {ContentLength} {ContentType} {ElapsedMilliseconds}ms";
    private const string RequestUnhandledFormat = "Request reached the end of the middleware pipeline without being handled by application code. Request path: {Method} {Scheme}://{Host}{PathBase}{Path}, Response status code: {StatusCode}";

    private readonly KeyValuePair<string, object?>[] _values;
    private readonly string _message;

    private HostingLogEntry(string message, KeyValuePair<string, object?>[] values)
    {
        _message = message;
        _values = values;
    }

    public int Count => _values.Length;

    public KeyValuePair<string, object?> this[int index] => _values[index];

    public static HostingLogEntry RequestStarting(HttpRequest request)
    {
        var message = $"Request starting {request.Protocol} {request.Method} {request.Scheme}://{request.Host.Value}{request.PathBase.Value}{request.Path.Value}{request.QueryString.Value} - {EscapedValueOrEmptyMarker(request.ContentType)} {ValueOrEmptyMarker(request.ContentLength)}";
        return new HostingLogEntry(message,
        [
            new(nameof(request.Protocol), request.Protocol),
            new(nameof(request.Method), request.Method),
            new(nameof(request.ContentType), request.ContentType),
            new(nameof(request.ContentLength), request.ContentLength),
            new(nameof(request.Scheme), request.Scheme),
            new(nameof(request.Host), request.Host.Value),
            new(nameof(request.PathBase), request.PathBase.Value),
            new(nameof(request.Path), request.Path.Value),
            new(nameof(request.QueryString), request.QueryString.Value),
            new(OriginalFormatKey, RequestStartingFormat)
        ]);
    }

    public static HostingLogEntry RequestFinished(HttpContext context, TimeSpan elapsed)
    {
        var request = context.Request;
        var response = context.Response;
        var message = $"Request finished {request.Protocol} {request.Method} {request.Scheme}://{request.Host.Value}{request.PathBase.Value}{request.Path.Value}{request.QueryString.Value} - {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {ValueOrEmptyMarker(response.ContentLength)} {EscapedValueOrEmptyMarker(response.ContentType)} {elapsed.TotalMilliseconds.ToString(ElapsedMillisecondsFormat, CultureInfo.InvariantCulture)}ms";
        return new HostingLogEntry(message,
        [
            new(ElapsedMillisecondsKey, elapsed.TotalMilliseconds),
            new(nameof(response.StatusCode), response.StatusCode),
            new(nameof(response.ContentType), response.ContentType),
            new(nameof(response.ContentLength), response.ContentLength),
            new(nameof(request.Protocol), request.Protocol),
            new(nameof(request.Method), request.Method),
            new(nameof(request.Scheme), request.Scheme),
            new(nameof(request.Host), request.Host.Value),
            new(nameof(request.PathBase), request.PathBase.Value),
            new(nameof(request.Path), request.Path.Value),
            new(nameof(request.QueryString), request.QueryString.Value),
            new(OriginalFormatKey, RequestFinishedFormat)
        ]);
    }

    public static HostingLogEntry RequestUnhandled(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var message = $"Request reached the end of the middleware pipeline without being handled by application code. Request path: {request.Method} {request.Scheme}://{request.Host}{request.PathBase}{request.Path}, Response status code: {response.StatusCode}";
        return new HostingLogEntry(message,
        [
            new(nameof(request.Method), request.Method),
            new(nameof(request.Scheme), request.Scheme),
            new(nameof(request.Host), request.Host),
            new(nameof(request.PathBase), request.PathBase),
            new(nameof(request.Path), request.Path),
            new(nameof(response.StatusCode), response.StatusCode),
            new(OriginalFormatKey, RequestUnhandledFormat)
        ]);
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        return ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override string ToString()
    {
        return _message;
    }

    private static string EscapedValueOrEmptyMarker(string? value)
    {
        return value?.Length > 0 ? value.Replace(' ', '+') : EmptyEntry;
    }

    private static string ValueOrEmptyMarker(long? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? EmptyEntry;
    }
}

/// <summary>
/// The scope ASP.NET Core's hosting puts every log entry of a request it serves under, with the same keys and values, and
/// with <see cref="InProcessRequestDiagnostics.ToolRequestMarker"/>, so the entries of the tool's request can be told apart
/// from those of the MCP request, and from those of a request the server received.
/// </summary>
internal sealed class HostingLogScope : IReadOnlyList<KeyValuePair<string, object>>
{
    private const string RequestIdKey = "RequestId";
    private const string RequestPathKey = "RequestPath";

    private readonly string _requestId;
    private readonly string _requestPath;

    public HostingLogScope(HttpContext context)
    {
        _requestId = context.TraceIdentifier;
        _requestPath = (context.Request.PathBase.HasValue ? context.Request.PathBase + context.Request.Path : context.Request.Path).ToString();
    }

    public int Count => 3;

    public KeyValuePair<string, object> this[int index] => index switch
    {
        0 => new KeyValuePair<string, object>(RequestIdKey, _requestId),
        1 => new KeyValuePair<string, object>(RequestPathKey, _requestPath),
        2 => new KeyValuePair<string, object>(InProcessRequestDiagnostics.ToolRequestMarker, true),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override string ToString()
    {
        return $"{RequestPathKey}:{_requestPath} {RequestIdKey}:{_requestId} {InProcessRequestDiagnostics.ToolRequestMarker}:true";
    }
}
