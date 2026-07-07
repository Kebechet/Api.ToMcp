using Api.ToMcp.Abstractions.Scopes;
using ModelContextProtocol.Protocol;

namespace Api.ToMcp.Runtime;

public interface IMcpHttpInvoker
{
    Task<CallToolResult> GetAsync(string route, CancellationToken ct = default);
    Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default);

    /// <summary>
    /// Called before each tool invocation. Validates scope only if a scope mapper is configured.
    /// </summary>
    /// <param name="requiredScope">The scope required by the tool.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default);
}
