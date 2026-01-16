using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Middleware;

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
        // Check if this is an MCP endpoint being called internally
        if (context.Request.Path.StartsWithSegments("/mcp") &&
            context.Request.Headers.ContainsKey(LoopPreventionHeader))
        {
            if (context.RequestAborted.IsCancellationRequested)
                return;

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                "MCP endpoints cannot be called internally to prevent loops.",
                context.RequestAborted);
            return;
        }

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Expected when client disconnects (normal for SSE/MCP connections)
        }
    }
}
