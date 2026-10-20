using Api.ToMcp.Runtime.Tests.Tools;

namespace Api.ToMcp.Generated;

/// <summary>
/// The list the generator emits into an app of the tools it generated; here, of the test tools shaped like them.
/// </summary>
public static class McpToolsInfo
{
    public static readonly Type[] ToolTypes =
    [
        typeof(GeneratedStructuredTools),
        typeof(GeneratedUnsentTools)
    ];
}
