using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>An invoice whose total its own converter writes.</summary>
public sealed class StructuredInvoice
{
    /// <summary>Total as the till prints it.</summary>
    [JsonConverter(typeof(StructuredMoneyConverter))]
    public StructuredMoney Total { get; set; } = new();
}

public sealed class StructuredMoney
{
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "";
}

public sealed class StructuredMoneyConverter : JsonConverter<StructuredMoney>
{
    public override StructuredMoney Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = reader.GetString()!.Split(' ');
        return new StructuredMoney { Amount = decimal.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Currency = parts[1] };
    }

    public override void Write(Utf8JsonWriter writer, StructuredMoney value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"{value.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {value.Currency}");
    }
}

/// <summary>
/// A response whose converted property holds the first of two lines, so the exporter writes the second line's amount as
/// a $ref into it.
/// </summary>
public sealed class StructuredRefInvoice
{
    [JsonConverter(typeof(StructuredLineConverter))]
    public StructuredLine First { get; set; } = new();

    public StructuredLine Second { get; set; } = new();
}

public sealed class StructuredLine
{
    public StructuredMoney Amount { get; set; } = new();

    public string Sku { get; set; } = "";
}

public sealed class StructuredLineConverter : JsonConverter<StructuredLine>
{
    public override StructuredLine Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new StructuredLine { Sku = reader.GetString()! };
    }

    public override void Write(Utf8JsonWriter writer, StructuredLine value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Sku);
    }
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

    [McpServerTool(Name = "generated_converted", UseStructuredContent = true, OutputSchemaType = typeof(StructuredInvoice))]
    [McpGeneratedTool]
    [Description("Generated tool whose request and response have a property its own converter writes.")]
    public static Task<object> Converted(StructuredInvoice invoice)
    {
        return Task.FromResult<object>(new CallToolResult());
    }

    [McpServerTool(Name = "generated_refs", UseStructuredContent = true, OutputSchemaType = typeof(StructuredRefInvoice))]
    [McpGeneratedTool]
    [Description("Generated tool whose request and response hold a $ref into a converted property.")]
    public static Task<object> Refs(StructuredRefInvoice invoice)
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
