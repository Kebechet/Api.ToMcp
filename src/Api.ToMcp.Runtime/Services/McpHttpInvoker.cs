using System.Net.Http.Headers;
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

    private readonly HttpClient _httpClient;
    private readonly ISelfBaseUrlProvider _baseUrlProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<McpHttpInvoker> _logger;
    private readonly McpScopeOptions _scopeOptions;

    public McpHttpInvoker(
        HttpClient httpClient,
        ISelfBaseUrlProvider baseUrlProvider,
        IHttpContextAccessor httpContextAccessor,
        ILogger<McpHttpInvoker> logger,
        IOptions<McpScopeOptions> scopeOptions)
    {
        _httpClient = httpClient;
        _baseUrlProvider = baseUrlProvider;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _scopeOptions = scopeOptions.Value;
    }

    public async Task<CallToolResult> GetAsync(string route, CancellationToken ct = default)
    {
        var url = BuildUrl(route);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureRequest(request);

        _logger.LogDebug("MCP invoking GET {Url}", url);

        var response = await _httpClient.SendAsync(request, ct);
        return await HandleResponse(response, ct);
    }

    public async Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default)
    {
        var url = BuildUrl(route);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ConfigureRequest(request);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        _logger.LogDebug("MCP invoking POST {Url}", url);

        var response = await _httpClient.SendAsync(request, ct);
        return await HandleResponse(response, ct);
    }

    public async Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default)
    {
        var url = BuildUrl(route);
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        ConfigureRequest(request);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        _logger.LogDebug("MCP invoking PUT {Url}", url);

        var response = await _httpClient.SendAsync(request, ct);
        return await HandleResponse(response, ct);
    }

    public async Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default)
    {
        var url = BuildUrl(route);
        using var request = new HttpRequestMessage(HttpMethod.Patch, url);
        ConfigureRequest(request);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        _logger.LogDebug("MCP invoking PATCH {Url}", url);

        var response = await _httpClient.SendAsync(request, ct);
        return await HandleResponse(response, ct);
    }

    public async Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default)
    {
        var url = BuildUrl(route);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        ConfigureRequest(request);

        _logger.LogDebug("MCP invoking DELETE {Url}", url);

        var response = await _httpClient.SendAsync(request, ct);
        return await HandleResponse(response, ct);
    }

    public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default)
    {
        if (_scopeOptions.ClaimToScopeMapper is null)
        {
            return Task.CompletedTask;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("User is not authenticated. Scope validation requires authentication.");
        }

        var claimValue = httpContext.User.FindFirst(_scopeOptions.ClaimName)?.Value;
        if (string.IsNullOrEmpty(claimValue))
        {
            throw new UnauthorizedAccessException($"Claim '{_scopeOptions.ClaimName}' not found. Required scope: {requiredScope}");
        }

        var grantedScope = _scopeOptions.ClaimToScopeMapper(claimValue);

        if ((grantedScope & requiredScope) != requiredScope)
        {
            throw new UnauthorizedAccessException($"Insufficient scope. Required: {requiredScope}, Granted: {grantedScope}");
        }

        _logger.LogDebug("Scope validation passed. Required: {Required}, Granted: {Granted}", requiredScope, grantedScope);

        return Task.CompletedTask;
    }

    private string BuildUrl(string route)
    {
        var baseUrl = _baseUrlProvider.GetBaseUrl();

        // When behind a reverse proxy, Kestrel only listens on HTTP but UseHttpsRedirection()
        // redirects internal self-calls to HTTPS which doesn't exist locally.
        // Derive the correct HTTPS base URL from the incoming request instead.
        if (baseUrl.StartsWith("http://"))
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is not null)
            {
                var requestBaseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
                if (requestBaseUrl.StartsWith("https://"))
                {
                    baseUrl = requestBaseUrl;
                }
            }
        }

        var normalizedRoute = route.StartsWith("/") ? route : "/" + route;
        return baseUrl + normalizedRoute;
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
