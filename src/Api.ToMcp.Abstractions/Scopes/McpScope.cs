using System;

namespace Api.ToMcp.Abstractions.Scopes;

/// <summary>
/// Defines the scope required to invoke an MCP tool.
/// Scopes are mapped from HTTP methods.
/// </summary>
[Flags]
public enum McpScope
{
    None = 0,
    /// <summary>
    /// Allows GET, HEAD, OPTIONS methods.
    /// </summary>
    Read = 1,
    /// <summary>
    /// Allows POST, PUT, PATCH methods.
    /// </summary>
    Write = 2,
    /// <summary>
    /// Allows DELETE method.
    /// </summary>
    Delete = 4,
    /// <summary>
    /// Allows all methods.
    /// </summary>
    All = Read | Write | Delete
}
