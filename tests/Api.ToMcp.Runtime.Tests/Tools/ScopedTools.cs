using System.Reflection;
using Api.ToMcp.Abstractions.Scopes;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Tests.Tools;

/// <summary>
/// Tools that name the scope they require, as generated tools do. The class has no <c>[McpServerToolType]</c>, so
/// <c>AddMcpTools</c> leaves it out of this assembly's tools; a test registers the ones it needs.
/// </summary>
public static class ScopedTools
{
    public const string ReadTool = "scoped_read";
    public const string WriteTool = "scoped_write";
    public const string ReadDeleteTool = "scoped_read_delete";
    public const string UnnamedBitTool = "scoped_unnamed_bit";
    public const string UnscopedTool = "unscoped";

    [McpServerTool(Name = ReadTool)]
    [McpRequiredScope(McpScope.Read)]
    public static string Read() => "read";

    [McpServerTool(Name = WriteTool)]
    [McpRequiredScope(McpScope.Write)]
    public static string Write() => "write";

    [McpServerTool(Name = ReadDeleteTool)]
    [McpRequiredScope(McpScope.Read | McpScope.Delete)]
    public static string ReadDelete() => "read and delete";

    [McpServerTool(Name = UnnamedBitTool)]
    [McpRequiredScope((McpScope)8)]
    public static string UnnamedBit() => "unnamed bit";

    [McpServerTool(Name = UnscopedTool)]
    public static string Unscoped() => "unscoped";

    public static McpServerTool Create(string toolName)
    {
        var method = typeof(ScopedTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(x => x.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        return McpServerTool.Create(method);
    }
}
