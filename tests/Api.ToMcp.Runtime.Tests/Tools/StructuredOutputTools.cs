using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Tests.Tools;

/// <summary>Response the structured output tools describe.</summary>
public sealed class StructuredResponse
{
    /// <summary>Name documented from XML.</summary>
    public string Name { get; set; } = "";

    public StructuredKind Kind { get; set; }
}

public enum StructuredKind
{
    First,
    Second
}

/// <summary>A response that can contain itself.</summary>
public sealed class StructuredNode
{
    public string Name { get; set; } = "";

    public List<StructuredNode> Children { get; set; } = [];
}

/// <summary>Shaped like the tools the generator emits for actions with a typed response.</summary>
[McpServerToolType]
public static class GeneratedStructuredTools
{
    [McpServerTool(Name = "generated_strict", UseStructuredContent = true, OutputSchemaType = typeof(StructuredResponse))]
    [McpGeneratedTool]
    [Description("Generated tool whose response always has a body.")]
    public static Task<object> Strict()
    {
        return Task.FromResult<object>(new CallToolResult());
    }

    [McpServerTool(Name = "generated_nullable", UseStructuredContent = true, OutputSchemaType = typeof(StructuredResponse))]
    [McpGeneratedTool(IsOutputNullable = true)]
    [Description("Generated tool whose response can be empty.")]
    public static Task<object> Nullable()
    {
        return Task.FromResult<object>(new CallToolResult());
    }

    [McpServerTool(Name = "generated_recursive", UseStructuredContent = true, OutputSchemaType = typeof(StructuredNode))]
    [McpGeneratedTool]
    [Description("Generated tool whose response type can contain itself.")]
    public static Task<object> Recursive()
    {
        return Task.FromResult<object>(new CallToolResult());
    }
}

[McpServerToolType]
public static class HandWrittenStructuredTools
{
    [McpServerTool(Name = "handwritten_structured", UseStructuredContent = true, OutputSchemaType = typeof(StructuredResponse))]
    [Description("Hand-written tool that builds its own structured content.")]
    public static CallToolResult Run()
    {
        return new CallToolResult();
    }
}
