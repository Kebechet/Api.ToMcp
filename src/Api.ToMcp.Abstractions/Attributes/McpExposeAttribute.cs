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
    /// such as a search that takes its filter as a POST body and changes nothing.
    /// </summary>
    public McpScope Scope { get; set; }
}
