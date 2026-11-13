using ModelContextProtocol.Protocol;

namespace Api.ToMcp.Runtime;

/// <summary>
/// An <see cref="IMcpHttpInvoker"/> that also sends request headers and a form, which the tool of an action with
/// <c>[FromHeader]</c> or <c>[FromForm]</c> parameters needs. The default invoker, <c>McpHttpInvoker</c>, implements it.
/// </summary>
/// <remarks>
/// A tool sends such a request through <see cref="McpHttpRequest.SendAsync"/>, which uses this interface when the
/// registered invoker implements it. One that implements only <see cref="IMcpHttpInvoker"/> still serves a call without
/// headers and form; a call with them gets an error result that names this interface.
/// </remarks>
public interface IMcpHttpRequestInvoker : IMcpHttpInvoker
{
    /// <summary>
    /// Sends the request to the API, as the <see cref="IMcpHttpInvoker"/> methods do, with its headers and its body.
    /// </summary>
    Task<CallToolResult> SendAsync(McpHttpRequest request, CancellationToken ct = default);
}
