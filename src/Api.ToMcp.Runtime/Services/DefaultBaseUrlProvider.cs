using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;

namespace Api.ToMcp.Runtime.Services;

public sealed class DefaultBaseUrlProvider : ISelfBaseUrlProvider
{
    private readonly IServer _server;
    private readonly IConfiguration _configuration;
    private string? _cachedUrl;
    private readonly object _lock = new();

    public DefaultBaseUrlProvider(IServer server, IConfiguration configuration)
    {
        _server = server;
        _configuration = configuration;
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

            // Fallback to IServerAddressesFeature
            var addressFeature = _server.Features.Get<IServerAddressesFeature>();
            if (addressFeature?.Addresses.Any() == true)
            {
                var address = addressFeature.Addresses
                    .FirstOrDefault(a => a.StartsWith("https://"))
                    ?? addressFeature.Addresses.First();

                _cachedUrl = address.TrimEnd('/');
                return _cachedUrl;
            }

            throw new InvalidOperationException(
                "Unable to determine base URL. Configure 'McpTools:BaseUrl' in appsettings.json " +
                "or ensure the server has started before calling GetBaseUrl().");
        }
    }
}
