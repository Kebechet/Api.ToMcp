namespace Api.ToMcp.Generator.Models;

/// <summary>
/// The request headers a generated tool never sends from an argument. Mirrors ASP.NET Core's <c>HeaderNames</c>, which a
/// netstandard2.0 source generator cannot reference, and the loop prevention header of the runtime's invoker.
/// </summary>
internal static class HttpHeaderNames
{
    public const string Authorization = "Authorization";
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
}
