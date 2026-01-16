namespace Api.ToMcp.Abstractions;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class McpExposeAttribute : Attribute
{
    public string? ToolName { get; set; }
    public string? Description { get; set; }
}
