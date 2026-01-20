using Api.ToMcp.Abstractions.Scopes;

namespace Api.ToMcp.Runtime.Options;

/// <summary>
/// Configuration options for scope-based access control.
/// When not configured, all tools are accessible without scope validation.
/// </summary>
public class McpScopeOptions
{
    /// <summary>
    /// Maps a claim value to McpScope. If null, scope checking is disabled (default).
    /// </summary>
    public Func<string, McpScope>? ClaimToScopeMapper { get; set; }

    /// <summary>
    /// Name of the claim to read scopes from (e.g., "scope", "permissions").
    /// Defaults to "scope".
    /// </summary>
    public string ClaimName { get; set; } = "scope";
}
