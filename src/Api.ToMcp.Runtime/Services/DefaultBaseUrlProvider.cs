using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Api.ToMcp.Runtime.Services;

public sealed class DefaultBaseUrlProvider : ISelfBaseUrlProvider
{
    private const string VsTunnelUrlEnvVar = "VS_TUNNEL_URL";

    private readonly IServer _server;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private string? _cachedUrl;
    private readonly object _lock = new();

    public DefaultBaseUrlProvider(IServer server, IConfiguration configuration, IHostEnvironment environment)
    {
        _server = server;
        _configuration = configuration;
        _environment = environment;
    }

    public string GetBaseUrl()
    {
        if (_cachedUrl is not null)
            return _cachedUrl;

        lock (_lock)
        {
            if (_cachedUrl is not null)
                return _cachedUrl;

            // First, check configuration
            var configuredUrl = _configuration["McpTools:BaseUrl"];
            if (!string.IsNullOrEmpty(configuredUrl))
            {
                _cachedUrl = configuredUrl.TrimEnd('/');
                return _cachedUrl;
            }

            // In Development, check for VS DevTunnel URL
            if (_environment.IsDevelopment())
            {
                var tunnelUrl = Environment.GetEnvironmentVariable(VsTunnelUrlEnvVar);
                if (!string.IsNullOrEmpty(tunnelUrl))
                {
                    _cachedUrl = tunnelUrl.TrimEnd('/');
                    return _cachedUrl;
                }
            }

            // Fallback to IServerAddressesFeature
            var addressFeature = _server.Features.Get<IServerAddressesFeature>();
            if (addressFeature?.Addresses.Any() == true)
            {
                var address = addressFeature.Addresses
                    .FirstOrDefault(a => a.StartsWith("https://"))
                    ?? addressFeature.Addresses.First();

                _cachedUrl = NormalizeBindingAddress(address);
                return _cachedUrl;
            }

            throw new InvalidOperationException(
                "Unable to determine base URL. Configure 'McpTools:BaseUrl' in appsettings.json " +
                "or ensure the server has started before calling GetBaseUrl().");
        }
    }

    /// <summary>
    /// Normalizes wildcard binding addresses (e.g., http://+:80, http://0.0.0.0:80, http://[::]:80)
    /// into localhost addresses that are valid for HTTP client self-calls.
    /// Uri constructor cannot be used here because hosts like "+" and "*" are not valid URIs.
    /// </summary>
    internal static string NormalizeBindingAddress(string address)
    {
        var trimmed = address.TrimEnd('/');

        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return trimmed;

        var scheme = trimmed[..schemeEnd];
        var afterScheme = trimmed[(schemeEnd + 3)..];

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
            return $"{scheme}://localhost{rest}";
        }

        return trimmed;
    }
}
