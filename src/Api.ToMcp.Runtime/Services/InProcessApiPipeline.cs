using System.Collections;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Serves a generated tool's API request inside this process, through the request pipeline the host built for the
/// server: every middleware a request the server receives passes, the startup filters' included, with no socket and no
/// address to call. The request carries the connection of the MCP request it is made for, as the same client's next
/// request would.
/// </summary>
/// <remarks>
/// The hosting layer around the pipeline is not part of it: the request is not logged or counted as one the server
/// received, and it runs under the tool call's <see cref="Activity"/> instead of one of its own. Nor is anything the
/// server does before the pipeline: Kestrel's connection middleware and server-wide limits, TLS, or an IIS module.
/// </remarks>
internal sealed class InProcessApiPipeline
{
    private readonly IHttpContextFactory _httpContextFactory;
    private readonly ILogger<InProcessApiPipeline> _logger;
    private RequestDelegate? _application;

    public InProcessApiPipeline(IHttpContextFactory httpContextFactory, ILogger<InProcessApiPipeline> logger)
    {
        _httpContextFactory = httpContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Whether the host has built the pipeline, which it does when it starts, before the server takes requests.
    /// </summary>
    public bool IsCaptured => _application is not null;

    public void Capture(RequestDelegate application)
    {
        _application = application;
    }

    /// <summary>
    /// Serves the request and returns the response the app wrote, as an <see cref="HttpClient"/> would have received it
    /// from the server.
    /// </summary>
    /// <param name="request">The request, whose absolute URI gives the scheme, host, path and query the app sees.</param>
    /// <param name="caller">The MCP request the tool call came in, whose path base and connection the request carries.</param>
    /// <param name="cancellationToken">Aborts the request, as a client that goes away does.</param>
    /// <exception cref="HttpRequestException">
    /// The app aborted the request, or threw after its response started, which a server answers by dropping the
    /// connection.
    /// </exception>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpContext? caller, CancellationToken cancellationToken)
    {
        var application = _application ?? throw new InvalidOperationException("The host has not built the app's request pipeline yet.");
        var startTimestamp = Stopwatch.GetTimestamp();

        using var exchange = await InProcessExchange.CreateAsync(request, caller, cancellationToken);
        var callerActivity = Activity.Current;

        Task<Exception?> serving;

        // A request the server receives starts in an execution context of its own. Flowing the caller's would also flow
        // the IHttpContextAccessor's holder of the MCP request's context, which the context factory clears when it sets
        // the request's own, so the MCP request would lose its context.
        using (ExecutionContext.SuppressFlow())
        {
            serving = Task.Run(() => ServeAsync(application, exchange, callerActivity));
        }

        var unhandledException = await serving;

        cancellationToken.ThrowIfCancellationRequested();
        if (exchange.Lifetime.IsAborted)
        {
            throw new HttpRequestException("The app aborted the tool's in-process API request.");
        }

        if (unhandledException is not null && !exchange.Response.IsAnsweredWithUnhandledException)
        {
            throw new HttpRequestException("The app threw after the response of the tool's in-process API request started.", unhandledException);
        }

        _logger.LogInformation(
            "Served the tool's API request {Method} {Url} in process with {StatusCode} in {ElapsedMilliseconds} ms.",
            request.Method.Method,
            request.RequestUri,
            exchange.Response.Feature.StatusCode,
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

        return exchange.CreateResponseMessage(request);
    }

    /// <summary>
    /// Runs the request through the pipeline as the server's hosting runs one it receives, and returns the exception the
    /// app let escape, if any.
    /// </summary>
    private async Task<Exception?> ServeAsync(RequestDelegate application, InProcessExchange exchange, Activity? callerActivity)
    {
        Activity.Current = callerActivity;

        var context = _httpContextFactory.Create(exchange.Features);
        Exception? unhandledException = null;
        try
        {
            using (_logger.BeginScope(new RequestLogScope(context)))
            {
                try
                {
                    await application(context);

                    // A response without a body has not started yet. Like a server, run its OnStarting callbacks only
                    // when the app succeeded; one that throws fails the request as the app would.
                    await exchange.Response.Feature.StartAsync();
                }
                catch (Exception exception)
                {
                    unhandledException = exception;
                    LogUnhandledException(exception, context);
                    exchange.Response.AnswerUnhandledException();
                }

                await exchange.Response.Feature.CompleteAsync(_logger);
            }
        }
        finally
        {
            _httpContextFactory.Dispose(context);
        }

        return unhandledException;
    }

    private void LogUnhandledException(Exception exception, HttpContext context)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug("The tool's in-process API request {Method} {Path} was canceled.", context.Request.Method, context.Request.Path);
            return;
        }

        _logger.LogError(
            exception,
            "An unhandled exception was thrown by the app while serving the tool's API request {Method} {Path} in process.",
            context.Request.Method,
            context.Request.Path);
    }

    /// <summary>
    /// The features a server would give the request, and the response the app writes into them.
    /// </summary>
    private sealed class InProcessExchange : IDisposable
    {
        private InProcessExchange(FeatureCollection features, InProcessResponse response, RequestLifetimeFeature lifetime)
        {
            Features = features;
            Response = response;
            Lifetime = lifetime;
        }

        public FeatureCollection Features { get; }

        public InProcessResponse Response { get; }

        public RequestLifetimeFeature Lifetime { get; }

        public static async Task<InProcessExchange> CreateAsync(HttpRequestMessage request, HttpContext? caller, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new ArgumentException("The request has no URI.", nameof(request));
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            // The caller's path base is handed over as such, as the app may have taken it from a header such as
            // X-Forwarded-Prefix rather than from the path, and a UsePathBase middleware passes a path without it unchanged.
            var requestBody = new InProcessRequestBodyStream(body);
            var requestFeature = new HttpRequestFeature
            {
                Protocol = HttpProtocol.Http11,
                Method = request.Method.Method,
                Scheme = uri.Scheme,
                PathBase = caller?.Request.PathBase.Value ?? string.Empty,
                Path = PathString.FromUriComponent(uri).Value ?? string.Empty,
                QueryString = uri.Query,
                RawTarget = uri.PathAndQuery,
                Body = requestBody
            };
            requestFeature.Headers.Host = request.Headers.Host ?? uri.Authority;
            foreach (var (name, values) in request.Headers.Where(x => !string.Equals(x.Key, HeaderNames.Host, StringComparison.OrdinalIgnoreCase)))
            {
                requestFeature.Headers[name] = values.ToArray();
            }

            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers)
                {
                    requestFeature.Headers[name] = values.ToArray();
                }

                requestFeature.Headers.ContentLength = body.Length;
            }

            var response = new InProcessResponse();
            var lifetime = new RequestLifetimeFeature(cancellationToken);
            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(requestFeature);
            features.Set<IHttpMaxRequestBodySizeFeature>(requestBody);
            features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature(body.Length > 0));
            features.Set<IHttpResponseFeature>(response.Feature);
            features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new InProcessResponseBodyStream(response.Body, response.Feature)));
            features.Set<IHttpRequestLifetimeFeature>(lifetime);
            features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
            {
                ConnectionId = caller?.Connection.Id ?? Guid.NewGuid().ToString("N"),
                RemoteIpAddress = caller?.Connection.RemoteIpAddress,
                RemotePort = caller?.Connection.RemotePort ?? 0,
                LocalIpAddress = caller?.Connection.LocalIpAddress,
                LocalPort = caller?.Connection.LocalPort ?? 0
            });
            if (caller?.Features.Get<ITlsConnectionFeature>() is { } tlsConnection)
            {
                features.Set(tlsConnection);
            }

            return new InProcessExchange(features, response, lifetime);
        }

        public HttpResponseMessage CreateResponseMessage(HttpRequestMessage request)
        {
            var message = new HttpResponseMessage((HttpStatusCode)Response.Feature.StatusCode)
            {
                RequestMessage = request,
                Version = HttpVersion.Version11,
                Content = new ByteArrayContent(Response.Body.ToArray())
            };
            if (!string.IsNullOrEmpty(Response.Feature.ReasonPhrase))
            {
                message.ReasonPhrase = Response.Feature.ReasonPhrase;
            }

            // The content measures its own length, which is what the app wrote whatever header it set.
            foreach (var (name, values) in Response.Feature.Headers.Where(x => !string.Equals(x.Key, HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)))
            {
                if (!message.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
                {
                    message.Content.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
                }
            }

            return message;
        }

        public void Dispose()
        {
            Lifetime.Dispose();
        }
    }

    private sealed class InProcessResponse
    {
        public InProcessResponseFeature Feature { get; } = new();

        public MemoryStream Body { get; } = new();

        public bool IsAnsweredWithUnhandledException { get; private set; }

        public void AnswerUnhandledException()
        {
            if (Feature.HasStarted)
            {
                return;
            }

            Feature.AnswerUnhandledException();
            Body.SetLength(0);
            IsAnsweredWithUnhandledException = true;
        }
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public RequestBodyDetectionFeature(bool canHaveBody)
        {
            CanHaveBody = canHaveBody;
        }

        public bool CanHaveBody { get; }
    }

    /// <summary>
    /// Aborts the request when the tool call is canceled, as a server does when its client goes away, and records an
    /// abort by the app, which a server answers by dropping the connection.
    /// </summary>
    private sealed class RequestLifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource _aborted;

        public RequestLifetimeFeature(CancellationToken callCancellation)
        {
            _aborted = CancellationTokenSource.CreateLinkedTokenSource(callCancellation);
            RequestAborted = _aborted.Token;
        }

        public CancellationToken RequestAborted { get; set; }

        public bool IsAborted { get; private set; }

        public void Abort()
        {
            IsAborted = true;
            _aborted.Cancel();
        }

        public void Dispose()
        {
            _aborted.Dispose();
        }
    }

    /// <summary>
    /// The scope ASP.NET Core's hosting puts every log entry of a request it serves under, with the same keys, so the
    /// entries of the tool's request can be told apart from those of the MCP request as before.
    /// </summary>
    private sealed class RequestLogScope : IReadOnlyList<KeyValuePair<string, object>>
    {
        private const string RequestIdKey = "RequestId";
        private const string RequestPathKey = "RequestPath";

        private readonly string _requestId;
        private readonly string _requestPath;

        public RequestLogScope(HttpContext context)
        {
            _requestId = context.TraceIdentifier;
            _requestPath = context.Request.Path.ToString();
        }

        public int Count => 2;

        public KeyValuePair<string, object> this[int index] => index switch
        {
            0 => new KeyValuePair<string, object>(RequestIdKey, _requestId),
            1 => new KeyValuePair<string, object>(RequestPathKey, _requestPath),
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
            return $"{RequestPathKey}:{_requestPath} {RequestIdKey}:{_requestId}";
        }
    }
}
