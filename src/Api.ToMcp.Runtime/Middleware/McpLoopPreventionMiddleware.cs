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
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("MCP endpoints cannot be called internally to prevent loops.");
            return;
        }

        await _next(context);
    }
}
