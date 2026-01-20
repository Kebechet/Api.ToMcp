using Api.ToMcp.Abstractions.Scopes;

namespace Api.ToMcp.Runtime;

public interface IMcpHttpInvoker
{
    Task<string> GetAsync(string route, CancellationToken ct = default);
    Task<string> PostAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<string> PutAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<string> PatchAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<string> DeleteAsync(string route, CancellationToken ct = default);

    /// <summary>
    /// Called before each tool invocation. Validates scope only if a scope mapper is configured.
    /// </summary>
    /// <param name="requiredScope">The scope required by the tool.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default);
}
