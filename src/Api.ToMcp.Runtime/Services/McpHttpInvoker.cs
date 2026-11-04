using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Api.ToMcp.Runtime.Services;

public sealed class McpHttpInvoker : IMcpHttpInvoker
{
    private const string LoopPreventionHeader = "X-MCP-Internal-Call";
    private const string LoopPreventionValue = "true";
    private const char PathSeparator = '/';

    private readonly HttpClient _httpClient;
    private readonly ISelfBaseUrlProvider _baseUrlProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<McpHttpInvoker> _logger;
    private readonly McpScopeOptions _scopeOptions;
    private readonly InProcessApiPipeline? _inProcessPipeline;

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
        InProcessApiPipeline? inProcessPipeline)
    {
        _httpClient = httpClient;
        _baseUrlProvider = baseUrlProvider;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _scopeOptions = scopeOptions.Value;
        _inProcessPipeline = inProcessPipeline;
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

    private async Task<CallToolResult> SendAsync(HttpMethod method, string route, string? jsonBody, CancellationToken ct)
    {
        var caller = _httpContextAccessor.HttpContext;
        var isInProcess = _scopeOptions.AreApiCallsInProcess && _inProcessPipeline is { IsCaptured: true };
        var url = isInProcess && caller is { Request.Host.HasValue: true }
            ? BuildInProcessUrl(caller.Request, route)
            : BuildUrl(route);

        using var request = new HttpRequestMessage(method, url);
        ConfigureRequest(request);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        _logger.LogDebug("MCP invoking {Method} {Url}", method.Method, url);

        using var response = isInProcess
            ? await _inProcessPipeline!.SendAsync(request, caller, ct)
            : await _httpClient.SendAsync(request, ct);
        return await HandleResponse(response, ct);
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

        if (httpContext.User?.Identity?.IsAuthenticated != true)
        {
            throw RefuseScope("User is not authenticated. Scope validation requires authentication.");
        }

        var claimValue = httpContext.User.FindFirst(_scopeOptions.ClaimName)?.Value;
        if (string.IsNullOrEmpty(claimValue))
        {
            throw RefuseScope($"Claim '{_scopeOptions.ClaimName}' not found. Required scope: {requiredScope}");
        }

        var grantedScope = _scopeOptions.ClaimToScopeMapper(claimValue);

        if ((grantedScope & requiredScope) != requiredScope)
        {
            throw RefuseScope($"Insufficient scope. Required: {requiredScope}, Granted: {grantedScope}");
        }

        _logger.LogDebug("Scope validation passed. Required: {Required}, Granted: {Granted}", requiredScope, grantedScope);

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
    /// The route at the provider's address, used as given: <see cref="DefaultBaseUrlProvider"/> decides itself when an
    /// address it took from the server bindings has to follow the request to <c>https://</c>.
    /// </summary>
    private string BuildUrl(string route)
    {
        return _baseUrlProvider.GetBaseUrl() + NormalizeRoute(route);
    }

    /// <summary>
    /// The route at the address the MCP request came to, with its scheme and host as the app saw them, so the app filters
    /// and redirects the request as it did the MCP request; the pipeline hands over its path base. The request never
    /// leaves the process, so its host only names the request to the app.
    /// </summary>
    private static string BuildInProcessUrl(HttpRequest caller, string route)
    {
        return caller.Scheme + Uri.SchemeDelimiter + caller.Host.ToUriComponent() + NormalizeRoute(route);
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
