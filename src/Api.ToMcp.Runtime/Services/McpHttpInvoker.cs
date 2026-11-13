using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Options;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Api.ToMcp.Runtime.Services;

public sealed class McpHttpInvoker : IMcpHttpRequestInvoker
{
    private const string LoopPreventionHeader = McpLoopPreventionMiddleware.LoopPreventionHeader;
    private const string LoopPreventionValue = "true";
    private const char PathSeparator = RouteTokens.PathSeparator;

    private readonly HttpClient _httpClient;
    private readonly ISelfBaseUrlProvider _baseUrlProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<McpHttpInvoker> _logger;
    private readonly McpScopeOptions _scopeOptions;
    private readonly InProcessApiPipeline? _inProcessPipeline;
    private readonly string _forwardedProtoHeaderName;
    private readonly HashSet<string> _proxyHeaderNames;

    /// <summary>
    /// An invoker that sends every request over HTTP. Only the invoker <c>AddMcpTools</c> registers is handed the app's
    /// request pipeline to serve the requests in process.
    /// </summary>
    public McpHttpInvoker(
        HttpClient httpClient,
        ISelfBaseUrlProvider baseUrlProvider,
        IHttpContextAccessor httpContextAccessor,
        ILogger<McpHttpInvoker> logger,
        IOptions<McpScopeOptions> scopeOptions)
        : this(httpClient, baseUrlProvider, httpContextAccessor, logger, scopeOptions, inProcessPipeline: null)
    {
    }

    internal McpHttpInvoker(
        HttpClient httpClient,
        ISelfBaseUrlProvider baseUrlProvider,
        IHttpContextAccessor httpContextAccessor,
        ILogger<McpHttpInvoker> logger,
        IOptions<McpScopeOptions> scopeOptions,
        InProcessApiPipeline? inProcessPipeline,
        string? forwardedProtoHeaderName = null,
        IEnumerable<string>? proxyHeaderNames = null)
    {
        _httpClient = httpClient;
        _baseUrlProvider = baseUrlProvider;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _scopeOptions = scopeOptions.Value;
        _inProcessPipeline = inProcessPipeline;
        _forwardedProtoHeaderName = forwardedProtoHeaderName ?? ForwardedHeadersDefaults.XForwardedProtoHeaderName;
        _proxyHeaderNames = new HashSet<string>(proxyHeaderNames ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public Task<CallToolResult> GetAsync(string route, CancellationToken ct = default)
    {
        return SendAsync(HttpMethod.Get, route, jsonBody: null, ct);
    }

    public Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default)
    {
        return SendAsync(HttpMethod.Post, route, jsonBody, ct);
    }

    public Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default)
    {
        return SendAsync(HttpMethod.Put, route, jsonBody, ct);
    }

    public Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default)
    {
        return SendAsync(HttpMethod.Patch, route, jsonBody, ct);
    }

    public Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default)
    {
        return SendAsync(HttpMethod.Delete, route, jsonBody: null, ct);
    }

    private Task<CallToolResult> SendAsync(HttpMethod method, string route, string? jsonBody, CancellationToken ct)
    {
        return SendAsync(new McpHttpRequest(method, route) { JsonBody = jsonBody }, ct);
    }

    /// <summary>
    /// Sends the request as the other methods do, with its headers next to the invoker's own and its JSON body or form.
    /// A request with a header that the app's middleware trusts from its proxy, under a name the app configured for
    /// <c>UseForwardedHeaders</c> or <c>UseCertificateForwarding</c>, gets an error result instead, and is not sent.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The request has a JSON body and a form, or a header the invoker sets itself, a content header such as
    /// <c>Content-Type</c>, an invalid header name, or a header value with a character an HTTP header cannot carry.
    /// </exception>
    public async Task<CallToolResult> SendAsync(McpHttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var proxyHeader = request.Headers.FirstOrDefault(x => _proxyHeaderNames.Contains(x.Key)).Key;
        if (proxyHeader is not null)
        {
            return RefuseProxyHeader(request, proxyHeader);
        }

        var caller = _httpContextAccessor.HttpContext;
        var inProcessPipeline = _scopeOptions.AreApiCallsInProcess && _inProcessPipeline is not null && _inProcessPipeline.IsCaptured
            ? _inProcessPipeline
            : null;
        var url = inProcessPipeline is not null && caller is not null && caller.Request.Host.HasValue
            ? BuildInProcessUrl(caller.Request, request.Route)
            : BuildUrl(request.Route);

        using var message = new HttpRequestMessage(request.Method, url);
        ConfigureRequest(message);
        if (inProcessPipeline is null)
        {
            ForwardTheScheme(message, caller);
        }

        AddHeaders(message, request.Headers);
        message.Content = CreateContent(request);

        _logger.LogDebug("MCP invoking {Method} {Url}", request.Method.Method, url);

        using var response = inProcessPipeline is not null
            ? await SendInProcessAsync(inProcessPipeline, message, caller, ct)
            : await SendOverHttpAsync(message, request, ct);
        return await HandleResponse(response, ct);
    }

    /// <summary>
    /// The error result for a header the app takes the client's address, scheme, host or certificate from, which would
    /// let the AI choose them.
    /// </summary>
    private CallToolResult RefuseProxyHeader(McpHttpRequest request, string headerName)
    {
        _logger.LogWarning(
            "MCP tool call refused: its request would carry the header {Header}, which the app trusts from its proxy through ForwardedHeadersOptions or CertificateForwardingOptions.",
            headerName);

        const string Reason = "which the app trusts from its proxy: its ForwardedHeadersOptions or CertificateForwardingOptions name it, " +
            "and its value would become the client's address, scheme, host or certificate";
        var argument = request.FindHeaderArgument(headerName);
        var text = argument is null
            ? $"The tool's request would carry the header '{headerName}', {Reason}."
            : $"The argument '{argument}' is sent as the request header '{headerName}', {Reason}. Call the tool without '{argument}'.";

        return ToResult(text, isError: true);
    }

    private static void AddHeaders(HttpRequestMessage message, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (ToolRequestHeaders.IsNeverSetFromArguments(name))
            {
                throw new ArgumentException($"The tool's request carries the header '{name}' of its own, or never from a tool.", nameof(headers));
            }

            if (!McpRequestValueFormatter.IsHeaderValue(value))
            {
                throw new ArgumentException($"The value of the header '{name}' holds a character an HTTP header cannot carry.", nameof(headers));
            }

            // Kestrel drops the spaces and tabs around a value, which the in-process request would keep.
            if (!message.Headers.TryAddWithoutValidation(name, McpRequestValueFormatter.TrimHeaderValue(value)))
            {
                throw new ArgumentException($"'{name}' is no header the tool's request can carry: it describes the body, or is no valid header name.", nameof(headers));
            }
        }
    }

    private static HttpContent? CreateContent(McpHttpRequest request)
    {
        if (request.JsonBody is not null && request.FormBody is not null)
        {
            throw new ArgumentException("A request has a JSON body or a form, not both.", nameof(request));
        }

        if (request.JsonBody is not null)
        {
            return CreateJsonContent(request.JsonBody);
        }

        return request.FormBody is null
            ? null
            : new StringContent(request.FormBody, Encoding.UTF8, MediaTypeNames.Application.FormUrlEncoded);
    }

    /// <summary>
    /// Sends the request over HTTP and follows a redirect as <see cref="InProcessApiPipeline"/> does: only back to the
    /// scheme, host and port the tool called, so an action that redirects to a URL from its input cannot send the tool's
    /// request to another server. Any other redirect comes back as it is. The invoker's client follows none itself,
    /// unless the app gave it a handler of its own that does. Each hop sends the JSON body or the form of
    /// <paramref name="body"/> again, unless the redirect turns the request into a GET.
    /// </summary>
    private async Task<HttpResponseMessage> SendOverHttpAsync(HttpRequestMessage request, McpHttpRequest body, CancellationToken ct)
    {
        var sent = request;
        McpHttpRequest? hopBody = body;
        var response = await _httpClient.SendAsync(sent, ct);
        for (var redirections = 0; redirections < InProcessApiPipeline.MaxAutomaticRedirections; redirections++)
        {
            var target = InProcessApiPipeline.GetRedirectTarget(response, sent.RequestUri!, PathString.Empty);
            if (target is null)
            {
                return response;
            }

            var isRedirectedAsGet = InProcessApiPipeline.IsRedirectedAsGet((int)response.StatusCode, sent.Method);
            if (isRedirectedAsGet)
            {
                hopBody = null;
            }

            // HttpClient sends a request only once, so each hop is a new one. The hop leads back to the address the tool
            // called, so it keeps Authorization, as in process.
            var next = new HttpRequestMessage(isRedirectedAsGet ? HttpMethod.Get : sent.Method, target);
            foreach (var header in sent.Headers)
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (hopBody is not null)
            {
                next.Content = CreateContent(hopBody);
            }

            response.Dispose();
            if (sent != request)
            {
                sent.Dispose();
            }

            sent = next;
            response = await _httpClient.SendAsync(sent, ct);
        }

        return response;
    }

    /// <summary>
    /// Serves the request in process under the <see cref="HttpClient.Timeout"/> of the invoker's client, and fails a
    /// timed-out or canceled call with the exceptions <see cref="HttpClient"/> throws, so the call ends as it did over HTTP.
    /// </summary>
    private async Task<HttpResponseMessage> SendInProcessAsync(InProcessApiPipeline pipeline, HttpRequestMessage request, HttpContext? caller, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_httpClient.Timeout);
        try
        {
            return await pipeline.SendAsync(request, caller, timeout.Token);
        }
        catch (OperationCanceledException exception) when (ct.IsCancellationRequested)
        {
            throw new TaskCanceledException(exception.Message, exception, ct);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"The request was canceled due to the configured HttpClient.Timeout of {_httpClient.Timeout.TotalSeconds} seconds elapsing.",
                new TimeoutException(exception.Message, exception),
                exception.CancellationToken);
        }
    }

    public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default)
    {
        if (_scopeOptions.ClaimToScopeMapper is null)
        {
            return Task.CompletedTask;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            throw RefuseScope(
                "Scope validation needs the HTTP request of the tool call, and the call runs outside one, as it does in a stateful session with " +
                "HttpServerTransportOptions.PerSessionExecutionContext = true; set that option to false.");
        }

        var scopeCheck = McpScopeCheck.Check(httpContext.User, requiredScope, _scopeOptions.ClaimName, _scopeOptions.ClaimToScopeMapper);
        if (scopeCheck.Refusal is not null)
        {
            throw RefuseScope(scopeCheck.Refusal);
        }

        _logger.LogDebug("Scope validation passed. Required: {Required}, Granted: {Granted}", requiredScope, scopeCheck.GrantedScope);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The exception that refuses the call, whose message the MCP client gets as the tool's error result.
    /// </summary>
    private McpScopeValidationException RefuseScope(string reason)
    {
        _logger.LogWarning("MCP tool call refused by scope validation: {Reason}", reason);

        return new McpScopeValidationException(reason);
    }

    /// <summary>
    /// The route at the provider's address, used as given, whatever host the MCP request names.
    /// </summary>
    private string BuildUrl(string route)
    {
        return _baseUrlProvider.GetBaseUrl() + NormalizeRoute(route);
    }

    /// <summary>
    /// The route at the address the MCP request came to, with its scheme, host and path base as the app saw them, so the
    /// app filters and redirects the request as it did the MCP request. The request never leaves the process, so its host
    /// only names the request to the app.
    /// </summary>
    private static string BuildInProcessUrl(HttpRequest caller, string route)
    {
        return caller.Scheme + Uri.SchemeDelimiter + caller.Host.ToUriComponent() + caller.PathBase.ToUriComponent() + NormalizeRoute(route);
    }

    /// <summary>
    /// Tells the app that a request at an <c>http://</c> address on loopback stands for an MCP request that came in over
    /// <c>https://</c>, whatever provider gave the address.
    /// </summary>
    private void ForwardTheScheme(HttpRequestMessage request, HttpContext? caller)
    {
        // When behind a reverse proxy, Kestrel only listens on HTTP but UseHttpsRedirection()
        // redirects internal self-calls to HTTPS which doesn't exist locally. The app trusts the
        // proxy's X-Forwarded-Proto, and ForwardedHeadersOptions trusts loopback, where a call to
        // a loopback address comes from, by default, so the header keeps the call on HTTPS as its
        // MCP request was. Off loopback the app would not trust it.
        var url = request.RequestUri!;
        if (caller is not null && caller.Request.IsHttps && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)
        {
            request.Headers.Add(_forwardedProtoHeaderName, Uri.UriSchemeHttps);
        }
    }

    private static StringContent CreateJsonContent(string jsonBody)
    {
        return new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
    }

    private static string NormalizeRoute(string route)
    {
        return route.StartsWith(PathSeparator) ? route : PathSeparator + route;
    }

    private void ConfigureRequest(HttpRequestMessage request)
    {
        // Add loop prevention header
        request.Headers.Add(LoopPreventionHeader, LoopPreventionValue);

        // Forward authentication if present
        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext?.Request.Headers.Authorization.Count > 0)
        {
            var authHeader = httpContext.Request.Headers.Authorization.ToString();

            if (AuthenticationHeaderValue.TryParse(authHeader, out var parsed))
            {
                request.Headers.Authorization = parsed;
                _logger.LogDebug("Forwarding Authorization header ({Scheme} scheme)", parsed.Scheme);
            }
            else
            {
                _logger.LogWarning("Failed to parse incoming Authorization header; it will not be forwarded.");
            }
        }
        else
        {
            _logger.LogDebug("No Authorization header to forward.");
        }
    }

    private async Task<CallToolResult> HandleResponse(HttpResponseMessage response, CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var truncatedBody = content.Length > 1000 ? content.Substring(0, 1000) : content;

            _logger.LogWarning("MCP HTTP call failed with status {StatusCode}: {Content}",
                (int)response.StatusCode, content.Length > 500 ? content.Substring(0, 500) : content);

            // Report the failure via isError = true (not by throwing) so the model sees a
            // deliberate error result rather than a soft success. See issue #7.
            var message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            return ToResult(string.IsNullOrEmpty(truncatedBody) ? message : $"{message} - {truncatedBody}", isError: true);
        }

        return ToResult(content, isError: false);
    }

    private static CallToolResult ToResult(string text, bool isError) => new CallToolResult
    {
        IsError = isError,
        Content = { new TextContentBlock { Text = text } }
    };
}
