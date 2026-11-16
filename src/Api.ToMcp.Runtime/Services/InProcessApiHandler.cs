using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The innermost handler of the invoker's <see cref="HttpClient"/>, right above its primary handler. It serves every
/// request through <see cref="InProcessApiPipeline"/>, after every other handler of the client has run, for the MCP request
/// the invoker gave it with <see cref="SetCaller"/>, and never passes a request on to the primary handler.
/// </summary>
internal sealed class InProcessApiHandler : DelegatingHandler
{
    private const string CallerOptionName = "Api.ToMcp.Caller";

    private static readonly HttpRequestOptionsKey<HttpContext> CallerKey = new(CallerOptionName);

    private readonly InProcessApiPipeline _pipeline;

    public InProcessApiHandler(InProcessApiPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    /// <summary>
    /// Gives the request the MCP request the tool call came in, whose connection and path base it carries. A tool call
    /// outside an MCP request has none.
    /// </summary>
    public static void SetCaller(HttpRequestMessage request, HttpContext? caller)
    {
        if (caller is not null)
        {
            request.Options.Set(CallerKey, caller);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(CallerKey, out var caller);

        return _pipeline.SendAsync(request, caller, cancellationToken);
    }
}
