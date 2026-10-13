using System.ComponentModel;

namespace Api.ToMcp.Runtime;

/// <summary>
/// Emitted by the Api.ToMcp source generator on every generated tool that has an output schema; not meant to be
/// written by hand. <c>AddMcpTools</c> reads it once at startup, when it builds the tools: it builds such a tool's
/// output schema with the API's own MVC JSON options, because the structured content is the API's response body.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpGeneratedToolAttribute : Attribute
{
    /// <summary>
    /// Whether the action can succeed without a body, so the output schema also allows null.
    /// </summary>
    public bool IsOutputNullable { get; set; }
}
