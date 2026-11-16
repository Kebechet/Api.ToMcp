using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The innermost handler of the invoker's <see cref="HttpClient"/>, right above its primary handler. It serves a request
/// the invoker marked with <see cref="ServeInProcess"/> through <see cref="InProcessApiPipeline"/>, after every other
/// handler of the client has run, and never passes a request on to the primary handler. It refuses any other request:
/// code other than the invoker that sends a request through this client, such as an invoker of the app's own registered
/// as a typed client under the client's name, means it for another server, and this app would run it instead.
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

    /// <exception cref="InvalidOperationException">The invoker did not mark the request.</exception>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(InProcessCallKey, out var inProcessCall))
        {
            throw new InvalidOperationException(
                $"The request {request.Method} {request.RequestUri} was sent through the HttpClient named {nameof(IMcpHttpInvoker)} by code other than the invoker " +
                "AddMcpTools registers. That client serves only the invoker's requests, in process, so the request is refused rather than run by this app. " +
                $"A client of your own takes that name when it is registered for {nameof(IMcpHttpInvoker)} as a typed client, as " +
                $"services.AddHttpClient<{nameof(IMcpHttpInvoker)}, MyInvoker>() does: give it a name of its own, as in " +
                $"services.AddHttpClient<{nameof(IMcpHttpInvoker)}, MyInvoker>(\"MyInvoker\").");
        }

        return _pipeline.SendAsync(request, inProcessCall.Caller, cancellationToken);
    }

    /// <summary>
    /// The mark. It wraps the MCP request, as <see cref="HttpRequestOptions"/> reads a null value as none, and a tool call
    /// outside an MCP request has none.
    /// </summary>
    private sealed record InProcessCall(HttpContext? Caller);
}
