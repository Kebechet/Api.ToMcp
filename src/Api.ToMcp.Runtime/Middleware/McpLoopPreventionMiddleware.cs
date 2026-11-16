using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Middleware;

/// <summary>
/// Turns on loop prevention by the header the invoker adds to every tool's request: the MCP endpoint
/// <c>MapMcpEndpoint</c> maps then refuses a request that carries it, wherever the endpoint is mapped, route parameters
/// included, and whether routing runs before or after this middleware. This middleware itself refuses such a request to
/// any path under <c>/mcp</c>, as it did before endpoints refused them, so an endpoint the SDK's <c>MapMcp</c> maps there
/// directly, which does not look for <see cref="IMcpToolRequestFeature"/>, refuses a tool's request too; any other route
/// serves such a request as usual. The endpoint <c>MapMcpEndpoint</c> maps refuses a tool's request without this
/// middleware: it carries <see cref="IMcpToolRequestFeature"/>.
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
    /// <paramref name="endpoint"/>, refusing a tool's request, which carries <see cref="IMcpToolRequestFeature"/>, and a
    /// request that this middleware has seen and that carries the header every tool's request carries.
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
