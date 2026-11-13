using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The address the tools call the API at: <c>McpTools:BaseUrl</c> as given when it is set, else an address the server
/// is bound to. No header of the MCP request decides it: <c>Host</c> and <c>X-Forwarded-Host</c> are the client's to set,
/// so following them sent the tool's request to any server the app can reach
/// (https://github.com/Kebechet/Api.ToMcp/issues/48).
/// </summary>
public sealed class DefaultBaseUrlProvider : ISelfBaseUrlProvider
{
    private const string BaseUrlConfigurationKey = "McpTools:BaseUrl";
    private static readonly string HttpsAddressPrefix = Uri.UriSchemeHttps + Uri.SchemeDelimiter;

    /// <summary>
    /// What Kestrel puts after the scheme of a unix socket's or a named pipe's address, as in <c>http://unix:/run/app.sock</c>
    /// and <c>http://pipe:/name</c>. An HTTP client would take <c>unix</c> or <c>pipe</c> for a TCP host.
    /// </summary>
    private static readonly string[] NonTcpHostPrefixes = ["unix:", "pipe:"];

    private readonly IServer _server;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private ResolvedBaseUrl? _cachedBaseUrl;
    private readonly object _lock = new();

    /// <summary>
    /// A provider that cannot see the request, so it also calls an <c>http://</c> binding off loopback for an MCP request
    /// that came in over <c>https://</c>.
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

    /// <exception cref="InvalidOperationException">
    /// The server is bound to no TCP address; or the MCP request came in over <c>https://</c> and the only address is an
    /// <c>http://</c> binding off loopback, which would carry the caller's token in clear text off the machine, from an
    /// address the app's forwarded headers do not trust by default. Both say to set <c>McpTools:BaseUrl</c>.
    /// </exception>
    public string GetBaseUrl()
    {
        var baseUrl = GetConfiguredOrBoundUrl();
        if (baseUrl.IsHttpBindingOffLoopback && _httpContextAccessor?.HttpContext?.Request.IsHttps == true)
        {
            throw new InvalidOperationException(
                $"The MCP request came in over HTTPS, and the only address the server is bound to is '{baseUrl.Url}', an http:// address off loopback. " +
                "A tool's request there would carry the caller's Authorization in clear text, and the app would not trust it to stand for an HTTPS request. " +
                $"Configure '{BaseUrlConfigurationKey}' in appsettings.json with an address the app serves without a redirect, or bind the server to localhost or a wildcard address as well.");
        }

        return baseUrl.Url;
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
            return new ResolvedBaseUrl(configuredUrl.TrimEnd(RouteTokens.PathSeparator), IsHttpBindingOffLoopback: false);
        }

        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var tcpAddresses = addresses.Where(x => !IsUnixSocketOrNamedPipe(x)).Select(NormalizeBindingAddress).ToList();

        // Prefer HTTPS addresses from IServerAddressesFeature for direct localhost self-calls.
        var httpsAddress = tcpAddresses.FirstOrDefault(a => a.StartsWith(HttpsAddressPrefix, StringComparison.OrdinalIgnoreCase));
        if (httpsAddress is not null)
        {
            return new ResolvedBaseUrl(httpsAddress, IsHttpBindingOffLoopback: false);
        }

        // Use HTTP address from server if available (works when UseHttpsRedirection is not in the pipeline). One on
        // loopback, where a wildcard binding is called, is what the app's forwarded headers trust by default.
        var loopbackAddress = tcpAddresses.FirstOrDefault(IsLoopback);
        if (loopbackAddress is not null)
        {
            return new ResolvedBaseUrl(loopbackAddress, IsHttpBindingOffLoopback: false);
        }

        if (tcpAddresses.Count > 0)
        {
            return new ResolvedBaseUrl(tcpAddresses[0], IsHttpBindingOffLoopback: true);
        }

        if (addresses.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unable to determine base URL. The server is bound only to a unix socket or a named pipe ({string.Join(", ", addresses)}), which a tool cannot call over HTTP. " +
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

    private static bool IsLoopback(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    /// <summary>
    /// Normalizes wildcard binding addresses (e.g., http://+:80, http://0.0.0.0:80, http://[::]:80)
    /// into localhost addresses that are valid for HTTP client self-calls.
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

    /// <summary>
    /// The configured or bound address, and whether it is an <c>http://</c> binding off loopback, which a tool's request for
    /// an MCP request over <c>https://</c> must not be sent to.
    /// </summary>
    private sealed record ResolvedBaseUrl(string Url, bool IsHttpBindingOffLoopback);
}
