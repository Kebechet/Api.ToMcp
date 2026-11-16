using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The name of the tool whose call the current code runs in, which <see cref="InProcessApiPipeline"/> gives the tool's
/// request in <see cref="IMcpToolRequestFeature"/>. The SDK runs a tool inside its call-tool filters, so the name this
/// filter sets flows into the tool and everything it awaits, and into no other call, however many run at once. Work the
/// call starts, such as a task or a timer, reads the name while the call runs and <see langword="null"/> once it has
/// ended, whether it returned or threw.
/// </summary>
internal static class McpToolCallName
{
    private static readonly AsyncLocal<NameHolder?> CurrentHolder = new();

    /// <summary>
    /// The name of the tool the client called, or <see langword="null"/> outside a tool call.
    /// </summary>
    public static string? Current => CurrentHolder.Value?.Name;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        // An async method hands its caller back the execution context it was called in, so the name ends with the call.
        return async (context, cancellationToken) =>
        {
            var holder = new NameHolder { Name = context.Params?.Name };
            CurrentHolder.Value = holder;
            try
            {
                return await next(context, cancellationToken);
            }
            finally
            {
                // Work the call started keeps the execution context it captured, holder included, after the call has
                // ended. Emptying the holder ends the name there too, as HttpContextAccessor does with its context.
                holder.Name = null;
            }
        };
    }

    private sealed class NameHolder
    {
        public string? Name { get; set; }
    }
}
