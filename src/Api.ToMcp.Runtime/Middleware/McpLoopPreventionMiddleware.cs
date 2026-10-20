using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Middleware;

/// <summary>
/// Turns on loop prevention for every request: the MCP endpoint <c>MapMcpEndpoint</c> maps then refuses a request that
/// carries the header the invoker adds to a tool's own call to the API. The endpoint does the refusing, not this
/// middleware, so it is refused wherever the endpoint is mapped, route parameters included, and whether routing runs
/// before or after this middleware; any other route serves such a request as usual.
/// </summary>
public sealed class McpLoopPreventionMiddleware
{
    private const string LoopPreventionHeader = "X-MCP-Internal-Call";
    private readonly RequestDelegate _next;

    public McpLoopPreventionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
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
    /// <paramref name="endpoint"/>, refusing a request that this middleware has seen and that a tool sent.
    /// </summary>
    internal static RequestDelegate RefuseInternalCalls(RequestDelegate endpoint)
    {
        return context =>
        {
            if (context.Features.Get<LoopPreventionFeature>() is null || !context.Request.Headers.ContainsKey(LoopPreventionHeader))
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
