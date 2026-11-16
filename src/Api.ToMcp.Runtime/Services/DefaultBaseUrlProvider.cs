using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The address that names a tool's request to the app when the request has no MCP request to take its address from:
/// <c>McpTools:BaseUrl</c> as given when it is set, else an address the server is bound to. The request runs in process,
/// so the address is never called. No header of an MCP request decides it: <c>Host</c> and <c>X-Forwarded-Host</c> are
/// the client's to set, so following them, when tools still sent their requests to an address, sent the tool's request
/// to any server the app can reach (https://github.com/Kebechet/Api.ToMcp/issues/48).
/// </summary>
public sealed class DefaultBaseUrlProvider : ISelfBaseUrlProvider
{
    private const string BaseUrlConfigurationKey = "McpTools:BaseUrl";
    private static readonly string HttpsAddressPrefix = Uri.UriSchemeHttps + Uri.SchemeDelimiter;

    /// <summary>
    /// What Kestrel puts after the scheme of a unix socket's or a named pipe's address, as in <c>http://unix:/run/app.sock</c>
    /// and <c>http://pipe:/name</c>. A URL would take <c>unix</c> or <c>pipe</c> for its host and the socket's path for the
    /// start of its own.
    /// </summary>
    private static readonly string[] NonTcpHostPrefixes = ["unix:", "pipe:"];

    private readonly IServer _server;
    private readonly IConfiguration _configuration;
    private string? _cachedBaseUrl;
    private readonly object _lock = new();

    /// <summary>
    /// A provider that reads <c>McpTools:BaseUrl</c> from <paramref name="configuration"/>, and otherwise the addresses
    /// <paramref name="server"/> is bound to once it has started.
    /// </summary>
    public DefaultBaseUrlProvider(IServer server, IConfiguration configuration)
    {
        _server = server;
        _configuration = configuration;
    }

    /// <exception cref="InvalidOperationException">
    /// <c>McpTools:BaseUrl</c> is not set and the server is bound to no TCP address. The message says to set it.
    /// </exception>
    public string GetBaseUrl()
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

    private string ResolveConfiguredOrBoundUrl()
    {
        var configuredUrl = _configuration[BaseUrlConfigurationKey];
        if (!string.IsNullOrEmpty(configuredUrl))
        {
            return configuredUrl.TrimEnd(RouteTokens.PathSeparator);
        }

        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var tcpAddresses = addresses.Where(x => !IsUnixSocketOrNamedPipe(x)).Select(NormalizeBindingAddress).ToList();

        // An https:// address keeps the request from a redirect by UseHttpsRedirection.
        var address = tcpAddresses.FirstOrDefault(a => a.StartsWith(HttpsAddressPrefix, StringComparison.OrdinalIgnoreCase)) ?? tcpAddresses.FirstOrDefault();
        if (address is not null)
        {
            return address;
        }

        if (addresses.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unable to determine base URL. The server is bound only to a unix socket or a named pipe ({string.Join(", ", addresses)}), whose address names no host for a tool's request. " +
                $"Configure '{BaseUrlConfigurationKey}' in appsettings.json.");
        }

        throw new InvalidOperationException(
            $"Unable to determine base URL. Configure '{BaseUrlConfigurationKey}' in appsettings.json " +
            "or ensure the server has started before calling GetBaseUrl().");
    }

    private static bool IsUnixSocketOrNamedPipe(string address)
    {
        var schemeEnd = address.IndexOf(Uri.SchemeDelimiter, StringComparison.Ordinal);
        var afterScheme = schemeEnd < 0 ? address : address[(schemeEnd + Uri.SchemeDelimiter.Length)..];
        return NonTcpHostPrefixes.Any(x => afterScheme.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalizes wildcard binding addresses (e.g., http://+:80, http://0.0.0.0:80, http://[::]:80)
    /// into localhost addresses that are valid URLs.
    /// Uri constructor cannot be used here because hosts like "+" and "*" are not valid URIs.
    /// </summary>
    internal static string NormalizeBindingAddress(string address)
    {
        var trimmed = address.TrimEnd(RouteTokens.PathSeparator);

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
}
