using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime;

/// <summary>
/// Tells a tool's request apart from a client's.
/// </summary>
public static class McpToolRequestExtensions
{
    /// <summary>
    /// Whether the request is a tool's, run through the app's request pipeline in process: whether it carries
    /// <see cref="IMcpToolRequestFeature"/>, which no client can send. Use it where the app has to tell a tool's request
    /// from a client's, such as a rate limiter's partition: the tool's request carries the connection of the MCP request
    /// it was made for, whose client still holds that request.
    /// </summary>
    /// <param name="context">The request's context.</param>
    public static bool IsMcpToolRequest(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Features.Get<IMcpToolRequestFeature>() is not null;
    }
}
