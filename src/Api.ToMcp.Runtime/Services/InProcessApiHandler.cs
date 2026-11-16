using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The innermost handler of the invoker's <see cref="HttpClient"/>, right above its primary handler. It serves a request
/// the invoker marked with <see cref="ServeInProcess"/> through <see cref="InProcessApiPipeline"/>, after every other
/// handler of the client has run, and never passes it on to the primary handler. Any other request goes on to the primary
/// handler, which sends it over HTTP.
/// </summary>
internal sealed class InProcessApiHandler : DelegatingHandler
{
    private const string InProcessCallOptionName = "Api.ToMcp.InProcessCall";

    private static readonly HttpRequestOptionsKey<InProcessCall> InProcessCallKey = new(InProcessCallOptionName);

    private readonly InProcessApiPipeline _pipeline;

    public InProcessApiHandler(InProcessApiPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    /// <summary>
    /// Marks the request to be served in process, for the tool call that came in <paramref name="caller"/>.
    /// </summary>
    public static void ServeInProcess(HttpRequestMessage request, HttpContext? caller)
    {
        request.Options.Set(InProcessCallKey, new InProcessCall(caller));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(InProcessCallKey, out var inProcessCall))
        {
            return base.SendAsync(request, cancellationToken);
        }

        return _pipeline.SendAsync(request, inProcessCall.Caller, cancellationToken);
    }

    /// <summary>
    /// The mark. It wraps the MCP request, as <see cref="HttpRequestOptions"/> reads a null value as none, and a tool call
    /// outside an MCP request has none.
    /// </summary>
    private sealed record InProcessCall(HttpContext? Caller);
}
