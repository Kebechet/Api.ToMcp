using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Middleware;

/// <summary>
/// Turns on loop prevention for a tool's request over HTTP: the MCP endpoint <c>MapMcpEndpoint</c> maps then refuses a
/// request that carries the header the invoker adds to a tool's own call to the API. The endpoint does the refusing, not
/// this middleware, so it is refused wherever the endpoint is mapped, route parameters included, and whether routing
/// runs before or after this middleware. This middleware still refuses such a request to any path under <c>/mcp</c>, as
/// it did before endpoints refused them, so an endpoint the SDK's <c>MapMcp</c> maps there directly keeps the
/// protection; any other route serves such a request as usual. A tool's in-process request needs none of this: it
/// carries <see cref="IMcpToolRequestFeature"/>, by which the endpoint refuses it with or without this middleware.
/// </summary>
public sealed class McpLoopPreventionMiddleware
{
    /// <summary>
    /// The header <see cref="Services.McpHttpInvoker"/> adds to a tool's own request to the API, by which this middleware
    /// and the MCP endpoint tell it apart.
    /// </summary>
    internal const string LoopPreventionHeader = "X-MCP-Internal-Call";

    private static readonly PathString DefaultMcpPath = new(RouteTokens.PathSeparator + McpExtensions.DefaultMcpPattern);

    private readonly RequestDelegate _next;

    public McpLoopPreventionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(DefaultMcpPath) && context.Request.Headers.ContainsKey(LoopPreventionHeader))
        {
            await RefuseAsync(context);
            return;
        }

        context.Features.Set(LoopPreventionFeature.Instance);

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Expected when client disconnects (normal for SSE/MCP connections)
        }
    }

    /// <summary>
    /// <paramref name="endpoint"/>, refusing a tool's request: one a tool runs in process, which carries
    /// <see cref="IMcpToolRequestFeature"/>, and one that this middleware has seen and that carries the header a tool's
    /// request over HTTP carries.
    /// </summary>
    internal static RequestDelegate RefuseInternalCalls(RequestDelegate endpoint)
    {
        return context =>
        {
            var isMarkedByThisMiddleware = context.Features.Get<LoopPreventionFeature>() is not null && context.Request.Headers.ContainsKey(LoopPreventionHeader);
            if (!context.IsMcpToolRequest() && !isMarkedByThisMiddleware)
            {
                return endpoint(context);
            }

            return RefuseAsync(context);
        };
    }

    private static async Task RefuseAsync(HttpContext context)
    {
        if (context.RequestAborted.IsCancellationRequested)
            return;

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync(
            "MCP endpoints cannot be called internally to prevent loops.",
            context.RequestAborted);
    }

    /// <summary>
    /// Marks a request that went through this middleware.
    /// </summary>
    private sealed class LoopPreventionFeature
    {
        public static readonly LoopPreventionFeature Instance = new();
    }
}
