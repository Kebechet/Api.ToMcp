namespace Api.ToMcp.Generator.Models;

/// <summary>
/// Internal representation of McpScope for the generator.
/// Source generators cannot reference runtime assemblies, so this mirrors
/// Api.ToMcp.Abstractions.Scopes.McpScope for compile-time use.
/// The generator emits code that references the real McpScope at runtime.
/// </summary>
[System.Flags]
internal enum McpScopeModel
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    All = Read | Write | Delete
}
