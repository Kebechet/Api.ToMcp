using Api.ToMcp.Abstractions.Scopes;

namespace Api.ToMcp.Abstractions;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class McpExposeAttribute : Attribute
{
    public string? ToolName { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Scope the tool requires. Left at <see cref="McpScope.None"/>, it follows the HTTP method (GET is read,
    /// POST/PUT/PATCH are write, DELETE is delete). Set it for an endpoint whose verb lies about what it does,
    /// such as a search that takes its filter as a POST body and changes nothing. On a controller it applies to
    /// every action that names no scope of its own; an action that sets <see cref="McpScope.None"/> explicitly
    /// follows its HTTP method instead. Combined flags (<c>McpScope.Read | McpScope.Write</c>) require every one
    /// of them.
    /// </summary>
    public McpScope Scope { get; set; }

    /// <summary>
    /// The tool's <c>readOnlyHint</c>. Left unset, it is true when the tool's scope is read only and it is not a PUT,
    /// PATCH or DELETE: a scope is a permission, and a loose one must not make a state-changing method look safe.
    /// </summary>
    /// <remarks>
    /// The four hints are emitted on every tool, because a client reads an omitted hint as its spec default (not
    /// read-only, destructive, not idempotent, open-world). Set one only to override what the generator derives; on a
    /// controller it applies to every action that does not set it itself. As with <see cref="Scope"/>, the nearest
    /// <c>[McpExpose]</c> replaces a base controller's as a whole, so a derived controller's attribute hides every hint
    /// of its base's, including those it does not set. See
    /// https://modelcontextprotocol.io/specification/2025-06-18/server/tools#tool-annotations
    /// </remarks>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// The tool's <c>destructiveHint</c>. Left unset, it is true for a tool that is not read-only and is a PUT, PATCH or
    /// DELETE or has the <see cref="McpScope.Delete"/> scope: the spec reserves false for purely additive updates. Set it
    /// on a POST that overwrites or cancels something, or set it to false on a PUT that only ever adds.
    /// </summary>
    public bool Destructive { get; set; }

    /// <summary>
    /// The tool's <c>idempotentHint</c>. Left unset, it is true for a read-only tool and for PUT and DELETE.
    /// </summary>
    public bool Idempotent { get; set; }

    /// <summary>
    /// The tool's <c>openWorldHint</c>. Left unset, it is false: a generated tool calls the host's own API, a closed
    /// system. Set it on an endpoint that reaches the public internet or other open-ended external systems.
    /// </summary>
    public bool OpenWorld { get; set; }
}
