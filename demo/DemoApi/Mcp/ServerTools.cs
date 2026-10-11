using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DemoApi.Mcp;

/// <summary>
/// A hand-written MCP-only tool with no API endpoint behind it. AddMcpTools registers it next to the generated
/// tools because the class carries [McpServerToolType]; its services come from the request's service provider.
/// </summary>
[McpServerToolType]
public static class ServerTools
{
    [McpServerTool(Name = "Server_GetEnvironment", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets the name of the hosting environment the API runs in.")]
    public static string GetEnvironment(IHostEnvironment environment)
    {
        return environment.EnvironmentName;
    }
}
