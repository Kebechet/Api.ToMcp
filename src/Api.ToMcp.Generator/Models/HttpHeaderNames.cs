namespace Api.ToMcp.Generator.Models;

/// <summary>
/// The request headers a generated tool never sends from an argument, and the starts of the names of the header families
/// it never sends. Mirrors ASP.NET Core's <c>HeaderNames</c>, which a netstandard2.0 source generator cannot reference, the
/// loop prevention header of the runtime's invoker, and the headers of front-end proxies the runtime's invoker refuses too.
/// </summary>
internal static class HttpHeaderNames
{
    public const string Authorization = "Authorization";
    public const string ProxyAuthorization = "Proxy-Authorization";
    public const string Cookie = "Cookie";
    public const string Host = "Host";

    /// <summary>
    /// The header <c>McpHttpInvoker</c> marks a tool's request with, which <c>UseMcpLoopPrevention</c> reads.
    /// </summary>
    public const string LoopPrevention = "X-MCP-Internal-Call";

    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
    public const string Baggage = "baggage";
    public const string CorrelationContext = "Correlation-Context";
    public const string TransferEncoding = "Transfer-Encoding";
    public const string ContentType = "Content-Type";
    public const string ContentLength = "Content-Length";
    public const string ContentEncoding = "Content-Encoding";
    public const string ContentLanguage = "Content-Language";
    public const string ContentLocation = "Content-Location";
    public const string ContentMD5 = "Content-MD5";
    public const string ContentRange = "Content-Range";
    public const string ContentDisposition = "Content-Disposition";
    public const string Allow = "Allow";
    public const string Expires = "Expires";
    public const string LastModified = "Last-Modified";
    public const string Connection = "Connection";
    public const string Upgrade = "Upgrade";
    public const string TE = "TE";
    public const string KeepAlive = "Keep-Alive";
    public const string Trailer = "Trailer";
    public const string ProxyConnection = "Proxy-Connection";

    /// <summary>
    /// Read by <c>UseHttpMethodOverride</c>, which turns a POST into the method it names.
    /// </summary>
    public const string HttpMethodOverride = "X-HTTP-Method-Override";

    public const string HttpMethod = "X-HTTP-Method";
    public const string MethodOverride = "X-Method-Override";
    public const string Forwarded = "Forwarded";
    public const string RealIp = "X-Real-IP";
    public const string TrueClientIp = "True-Client-IP";
    public const string CloudflareConnectingIp = "CF-Connecting-IP";
    public const string ClientIp = "X-Client-IP";
    public const string ClusterClientIp = "X-Cluster-Client-IP";

    /// <summary>
    /// The client certificate App Service and IIS ARR forward.
    /// </summary>
    public const string ArrClientCert = "X-ARR-ClientCert";

    /// <summary>
    /// The default header of <c>AddCertificateForwarding</c>, whose middleware trusts it from any request.
    /// </summary>
    public const string ClientCert = "X-Client-Cert";

    /// <summary>
    /// <c>X-Forwarded-For</c>, <c>-Proto</c>, <c>-Host</c> and <c>-Prefix</c>, which <c>UseForwardedHeaders</c> reads, and the
    /// <c>X-Forwarded-User</c>, <c>-Email</c> and <c>-Client-Cert</c> that authenticating proxies set.
    /// </summary>
    public const string ForwardedPrefix = "X-Forwarded-";

    /// <summary>
    /// The default name of the header by which the invoker tells the app, over HTTP to a loopback address, that the MCP
    /// request came in over HTTPS.
    /// </summary>
    public const string ForwardedProto = "X-Forwarded-Proto";

    /// <summary>
    /// Where <c>UseForwardedHeaders</c> keeps the values it replaced.
    /// </summary>
    public const string OriginalPrefix = "X-Original-";

    /// <summary>
    /// App Service Authentication's signed-in user: <c>X-MS-CLIENT-PRINCIPAL</c>, <c>-NAME</c>, <c>-ID</c> and <c>-IDP</c>.
    /// </summary>
    public const string ClientPrincipalPrefix = "X-MS-CLIENT-PRINCIPAL";

    /// <summary>
    /// App Service Authentication's tokens of the signed-in user.
    /// </summary>
    public const string TokenPrefix = "X-MS-TOKEN-";

    /// <summary>
    /// oauth2-proxy's signed-in user.
    /// </summary>
    public const string AuthRequestPrefix = "X-Auth-Request-";

    /// <summary>
    /// The signed-in user of forward-auth proxies such as Authelia: <c>Remote-User</c>, <c>-Groups</c>, <c>-Name</c> and
    /// <c>-Email</c>.
    /// </summary>
    public const string RemotePrefix = "Remote-";

    /// <summary>
    /// The user of Kubernetes front-proxy authentication: <c>X-Remote-User</c>, <c>-Group</c> and <c>-Extra-</c>.
    /// </summary>
    public const string RemoteUserPrefix = "X-Remote-";

    /// <summary>
    /// Google Identity-Aware Proxy's signed-in user and its assertion.
    /// </summary>
    public const string GoogleAuthenticatedUserPrefix = "X-Goog-Authenticated-User-";

    public const string GoogleIapPrefix = "X-Goog-IAP-";

    /// <summary>
    /// The OIDC claims and token an AWS Application Load Balancer passes on.
    /// </summary>
    public const string AmazonOidcPrefix = "X-Amzn-Oidc-";

    /// <summary>
    /// The client certificate of an AWS Application Load Balancer with mutual TLS.
    /// </summary>
    public const string AmazonMtlsPrefix = "X-Amzn-Mtls-";

    /// <summary>
    /// Cloudflare Access's assertion and signed-in user.
    /// </summary>
    public const string CloudflareAccessPrefix = "Cf-Access-";

    /// <summary>
    /// Pomerium's assertion and claims.
    /// </summary>
    public const string PomeriumPrefix = "X-Pomerium-";

    /// <summary>
    /// The client certificate a TLS-terminating proxy such as nginx is commonly set up to pass on.
    /// </summary>
    public const string SslClientPrefix = "X-SSL-Client-";
}
