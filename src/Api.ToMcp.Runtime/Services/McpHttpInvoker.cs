using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Api.ToMcp.Runtime.Services;

public sealed class McpHttpInvoker : IMcpHttpInvoker
{
    private const string LoopPreventionHeader = "X-MCP-Internal-Call";
    private const string LoopPreventionValue = "true";

    private readonly HttpClient _httpClient;
    private readonly ISelfBaseUrlProvider _baseUrlProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<McpHttpInvoker> _logger;

    public McpHttpInvoker(
        HttpClient httpClient,
        ISelfBaseUrlProvider baseUrlProvider,
        IHttpContextAccessor httpContextAccessor,
        ILogger<McpHttpInvoker> logger)
    {
        _httpClient = httpClient;
        _baseUrlProvider = baseUrlProvider;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<string> GetAsync(string route, CancellationToken ct = default)
    {
        var url = BuildUrl(route);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureRequest(request);

        _logger.LogDebug("MCP invoking GET {Url}", url);

        var response = await _httpClient.SendAsync(request, ct);
        return await HandleResponse(response, ct);
    }

    public async Task<string> PostAsync(string route, string? jsonBody, CancellationToken ct = default)
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

    private string BuildUrl(string route)
    {
        var baseUrl = _baseUrlProvider.GetBaseUrl();
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
            }
        }
    }

    private async Task<string> HandleResponse(HttpResponseMessage response, CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MCP HTTP call failed with status {StatusCode}: {Content}",
                (int)response.StatusCode, content.Length > 500 ? content.Substring(0, 500) : content);

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                error = true,
                statusCode = (int)response.StatusCode,
                message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                body = content.Length > 1000 ? content.Substring(0, 1000) : content
            });
        }

        return content;
    }
}
