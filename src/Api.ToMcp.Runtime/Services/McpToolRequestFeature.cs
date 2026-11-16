namespace Api.ToMcp.Runtime.Services;

internal sealed class McpToolRequestFeature : IMcpToolRequestFeature
{
    public McpToolRequestFeature(string? toolName)
    {
        ToolName = toolName;
    }

    public string? ToolName { get; }
}
