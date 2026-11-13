using Api.ToMcp.Runtime.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Net.Http.Headers;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The request headers a tool's request never takes from a tool, as the generator never sends them from an argument: the
/// caller's credentials, the headers the request carries of its own, the hop-by-hop headers of the connection, those a
/// middleware reads to change the request's method, client or scheme, and those in which a front-end proxy that signs the
/// user in passes the user on to the app. The AI chooses every value a tool sends, and the tool's request never passes
/// that proxy, which strips a client's copy of such a header. <c>HttpClient</c> keeps the headers that describe the body,
/// such as <c>Content-Type</c>, among the content's, so it refuses them as request headers itself. Compared ignoring case,
/// as HTTP compares header names.
/// </summary>
internal static class ToolRequestHeaders
{
    private const string ProxyConnection = "Proxy-Connection";
    private const string HttpMethodOverride = "X-HTTP-Method-Override";
    private const string HttpMethod = "X-HTTP-Method";
    private const string MethodOverride = "X-Method-Override";
    private const string Forwarded = "Forwarded";
    private const string RealIp = "X-Real-IP";
    private const string TrueClientIp = "True-Client-IP";
    private const string CloudflareConnectingIp = "CF-Connecting-IP";
    private const string ClientIp = "X-Client-IP";
    private const string ClusterClientIp = "X-Cluster-Client-IP";
    private const string ArrClientCert = "X-ARR-ClientCert";
    private const string ClientCert = "X-Client-Cert";
    private const string SslCert = "X-SSL-CERT";
    private const string ForwardedPrefix = "X-Forwarded-";
    private const string OriginalPrefix = "X-Original-";
    private const string ClientPrincipalPrefix = "X-MS-CLIENT-PRINCIPAL";
    private const string TokenPrefix = "X-MS-TOKEN-";
    private const string AuthRequestPrefix = "X-Auth-Request-";
    private const string RemotePrefix = "Remote-";
    private const string RemoteUserPrefix = "X-Remote-";
    private const string GoogleAuthenticatedUserPrefix = "X-Goog-Authenticated-User-";
    private const string GoogleIapPrefix = "X-Goog-IAP-";
    private const string AmazonOidcPrefix = "X-Amzn-Oidc-";
    private const string AmazonMtlsPrefix = "X-Amzn-Mtls-";
    private const string CloudflareAccessPrefix = "Cf-Access-";
    private const string PomeriumPrefix = "X-Pomerium-";
    private const string SslClientPrefix = "X-SSL-Client-";
    private const string NginxSslClientPrefix = "ssl-client-";

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.Authorization,
        HeaderNames.ProxyAuthorization,
        HeaderNames.Cookie,
        HeaderNames.Host,
        McpLoopPreventionMiddleware.LoopPreventionHeader,
        HeaderNames.TraceParent,
        HeaderNames.TraceState,
        HeaderNames.Baggage,
        HeaderNames.CorrelationContext,
        HeaderNames.TransferEncoding,
        HeaderNames.Connection,
        HeaderNames.Upgrade,
        HeaderNames.TE,
        HeaderNames.KeepAlive,
        HeaderNames.Trailer,
        ProxyConnection,
        HttpMethodOverride,
        HttpMethod,
        MethodOverride,
        Forwarded,
        RealIp,
        TrueClientIp,
        CloudflareConnectingIp,
        ClientIp,
        ClusterClientIp,
        ArrClientCert,
        ClientCert,
        SslCert
    };

    /// <summary>
    /// The starts of the header families: the forwarding headers, App Service Authentication, oauth2-proxy, forward-auth
    /// proxies such as Authelia, Kubernetes front-proxy authentication, Google IAP, the AWS Application Load Balancer,
    /// Cloudflare Access, Pomerium, and the client certificate of a proxy such as nginx or the NGINX ingress.
    /// </summary>
    private static readonly string[] Prefixes =
    [
        ForwardedPrefix,
        OriginalPrefix,
        ClientPrincipalPrefix,
        TokenPrefix,
        AuthRequestPrefix,
        RemotePrefix,
        RemoteUserPrefix,
        GoogleAuthenticatedUserPrefix,
        GoogleIapPrefix,
        AmazonOidcPrefix,
        AmazonMtlsPrefix,
        CloudflareAccessPrefix,
        PomeriumPrefix,
        SslClientPrefix,
        NginxSslClientPrefix
    ];

    public static bool IsNeverSetFromArguments(string headerName)
    {
        return Names.Contains(headerName) || Prefixes.Any(x => headerName.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The headers that ASP.NET Core's own middleware trusts from the app's proxy, under the names the app gave them in
    /// its options: those <c>UseForwardedHeaders</c> takes the client's address, scheme, host and path base from and keeps
    /// the request's own ones in, and the client certificate of <c>UseCertificateForwarding</c>, which trusts it from any
    /// request. No list of fixed names can hold them, and the generator cannot see the app's options.
    /// </summary>
    public static IReadOnlyCollection<string> GetProxyHeaderNames(ForwardedHeadersOptions forwardedHeaders, CertificateForwardingOptions certificateForwarding)
    {
        string?[] names =
        [
            forwardedHeaders.ForwardedForHeaderName,
            forwardedHeaders.ForwardedHostHeaderName,
            forwardedHeaders.ForwardedProtoHeaderName,
            forwardedHeaders.ForwardedPrefixHeaderName,
            forwardedHeaders.OriginalForHeaderName,
            forwardedHeaders.OriginalHostHeaderName,
            forwardedHeaders.OriginalProtoHeaderName,
            forwardedHeaders.OriginalPrefixHeaderName,
            certificateForwarding.CertificateHeader
        ];

        return names.OfType<string>().Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
