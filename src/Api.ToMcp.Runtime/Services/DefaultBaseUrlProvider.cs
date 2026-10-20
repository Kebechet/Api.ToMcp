using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The address the tools call the API at: <c>McpTools:BaseUrl</c> as given when it is set, else an address the server
/// is bound to. An <c>http://</c> binding follows a request that came in over <c>https://</c> to that request's own
/// address, as a TLS-terminating reverse proxy sends it; a configured address never does.
/// </summary>
public sealed class DefaultBaseUrlProvider : ISelfBaseUrlProvider
{
    private const string BaseUrlConfigurationKey = "McpTools:BaseUrl";
    private static readonly string HttpsAddressPrefix = Uri.UriSchemeHttps + Uri.SchemeDelimiter;

    private readonly IServer _server;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private ResolvedBaseUrl? _cachedBaseUrl;
    private readonly object _lock = new();

    /// <summary>
    /// A provider that never follows a request to <c>https://</c>, as it cannot see the request.
    /// </summary>
    public DefaultBaseUrlProvider(IServer server, IConfiguration configuration)
    {
        _server = server;
        _configuration = configuration;
    }

    public DefaultBaseUrlProvider(IServer server, IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
        : this(server, configuration)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetBaseUrl()
    {
        var baseUrl = GetConfiguredOrBoundUrl();
        if (!baseUrl.IsHttpBinding)
        {
            return baseUrl.Url;
        }

        // When behind a reverse proxy, Kestrel only listens on HTTP but UseHttpsRedirection()
        // redirects internal self-calls to HTTPS which doesn't exist locally.
        // Derive the correct HTTPS base URL from the incoming request instead.
        var request = _httpContextAccessor?.HttpContext?.Request;
        return request is { IsHttps: true }
            ? HttpsAddressPrefix + request.Host.ToUriComponent()
            : baseUrl.Url;
    }

    private ResolvedBaseUrl GetConfiguredOrBoundUrl()
    {
        if (_cachedBaseUrl is not null)
            return _cachedBaseUrl;

        lock (_lock)
        {
            if (_cachedBaseUrl is not null)
                return _cachedBaseUrl;

            _cachedBaseUrl = ResolveConfiguredOrBoundUrl();
            return _cachedBaseUrl;
        }
    }

    private ResolvedBaseUrl ResolveConfiguredOrBoundUrl()
    {
        // First, check configuration
        var configuredUrl = _configuration[BaseUrlConfigurationKey];
        if (!string.IsNullOrEmpty(configuredUrl))
        {
            return new ResolvedBaseUrl(configuredUrl.TrimEnd('/'), IsHttpBinding: false);
        }

        // Prefer HTTPS addresses from IServerAddressesFeature for direct localhost self-calls.
        var addressFeature = _server.Features.Get<IServerAddressesFeature>();
        if (addressFeature?.Addresses.Any() == true)
        {
            var httpsAddress = addressFeature.Addresses
                .FirstOrDefault(a => a.StartsWith(HttpsAddressPrefix, StringComparison.OrdinalIgnoreCase));

            if (httpsAddress is not null)
            {
                return new ResolvedBaseUrl(NormalizeBindingAddress(httpsAddress), IsHttpBinding: false);
            }
        }

        // Use HTTP address from server if available (works when UseHttpsRedirection is not in the pipeline)
        if (addressFeature?.Addresses.Any() == true)
        {
            return new ResolvedBaseUrl(NormalizeBindingAddress(addressFeature.Addresses.First()), IsHttpBinding: true);
        }

        throw new InvalidOperationException(
            $"Unable to determine base URL. Configure '{BaseUrlConfigurationKey}' in appsettings.json " +
            "or ensure the server has started before calling GetBaseUrl().");
    }

    /// <summary>
    /// Normalizes wildcard binding addresses (e.g., http://+:80, http://0.0.0.0:80, http://[::]:80)
    /// into localhost addresses that are valid for HTTP client self-calls.
    /// Uri constructor cannot be used here because hosts like "+" and "*" are not valid URIs.
    /// </summary>
    internal static string NormalizeBindingAddress(string address)
    {
        var trimmed = address.TrimEnd('/');

        var schemeEnd = trimmed.IndexOf(Uri.SchemeDelimiter, StringComparison.Ordinal);
        if (schemeEnd < 0)
            return trimmed;

        var scheme = trimmed[..schemeEnd];
        var afterScheme = trimmed[(schemeEnd + Uri.SchemeDelimiter.Length)..];

        string host;
        string rest;

        if (afterScheme.StartsWith("["))
        {
            // IPv6 address in brackets, e.g. [::]:80
            var bracketEnd = afterScheme.IndexOf(']');
            if (bracketEnd < 0)
                return trimmed;

            host = afterScheme[..(bracketEnd + 1)];
            rest = afterScheme[(bracketEnd + 1)..];
        }
        else
        {
            var hostEnd = afterScheme.IndexOfAny([':', '/']);
            if (hostEnd < 0)
            {
                host = afterScheme;
                rest = "";
            }
            else
            {
                host = afterScheme[..hostEnd];
                rest = afterScheme[hostEnd..];
            }
        }

        string[] wildcardHosts = ["+", "*", "0.0.0.0", "[::]", "[::1]"];
        if (wildcardHosts.Any(w => string.Equals(host, w, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{scheme}{Uri.SchemeDelimiter}localhost{rest}";
        }

        return trimmed;
    }

    /// <summary>
    /// The configured or bound address, and whether it is an <c>http://</c> binding, which may follow the request to
    /// <c>https://</c>.
    /// </summary>
    private sealed record ResolvedBaseUrl(string Url, bool IsHttpBinding);
}
