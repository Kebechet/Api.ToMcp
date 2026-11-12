using System.ComponentModel;
using Api.ToMcp.Abstractions.Scopes;

namespace Api.ToMcp.Runtime;

/// <summary>
/// Emitted by the Api.ToMcp source generator on every generated tool; not meant to be written by hand. It names the scope
/// the tool passes to <c>IMcpHttpInvoker.BeforeInvokeAsync</c>, so that, with OAuth sign-in discovery on, the MCP endpoint
/// can list the scopes in <c>scopes_supported</c> and refuse a call with a scope challenge before the tool runs.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpRequiredScopeAttribute : Attribute
{
    public McpRequiredScopeAttribute(McpScope scope)
    {
        Scope = scope;
    }

    public McpScope Scope { get; }
}
