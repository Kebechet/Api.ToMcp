using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The name of the tool whose call the current code runs in, which <see cref="InProcessApiPipeline"/> gives the tool's
/// request in <see cref="IMcpToolRequestFeature"/>. The SDK runs a tool inside its call-tool filters, so the name this
/// filter sets flows into the tool and everything it awaits, and into no other call, however many run at once.
/// </summary>
internal static class McpToolCallName
{
    private static readonly AsyncLocal<string?> CurrentName = new();

    /// <summary>
    /// The name of the tool the client called, or <see langword="null"/> outside a tool call.
    /// </summary>
    public static string? Current => CurrentName.Value;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        // An async method hands its caller back the execution context it was called in, so the name ends with the call.
        return async (context, cancellationToken) =>
        {
            CurrentName.Value = context.Params?.Name;
            return await next(context, cancellationToken);
        };
    }
}
