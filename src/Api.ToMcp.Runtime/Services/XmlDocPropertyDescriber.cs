using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Gives every tool-argument property without a description of its own one from the property's XML
/// <c>&lt;summary&gt;</c>. The MCP SDK builds a tool's input schema by reflecting over its parameter types and reads only
/// <see cref="DescriptionAttribute"/>, so without this a request DTO's properties, nested objects and array items
/// reach the agent as bare types. A <see cref="DescriptionAttribute"/> on the property always wins.
/// </summary>
internal sealed class XmlDocPropertyDescriber
{
    private const string DescriptionKeyword = "description";

    private readonly XmlDocSummaryReader _summaryReader;

    public XmlDocPropertyDescriber(XmlDocSummaryReader summaryReader)
    {
        _summaryReader = summaryReader;
        SchemaCreateOptions = new AIJsonSchemaCreateOptions { TransformSchemaNode = DescribeProperty };
    }

    /// <summary>
    /// The options to pass as <see cref="McpServerToolCreateOptions.SchemaCreateOptions"/>, built once and shared by
    /// every tool.
    /// </summary>
    public AIJsonSchemaCreateOptions SchemaCreateOptions { get; }

    public IReadOnlyDictionary<Assembly, string> MissingXmlDocPathsByAssembly => _summaryReader.MissingXmlDocPathsByAssembly;

    public IReadOnlyDictionary<Assembly, XmlDocReadFailure> UnreadableXmlDocsByAssembly => _summaryReader.UnreadableXmlDocsByAssembly;

    /// <summary>
    /// Called by the schema exporter for every node of a parameter's type graph, after it applied the
    /// <see cref="DescriptionAttribute"/>s. Only nodes that stand for a property or field are touched: the tool's own
    /// parameters already carry their descriptions, and a node without a <see cref="MemberInfo"/> behind it (an array's
    /// item type, a root) has no XML docs to read. A description the SDK copied from the property's type is replaced:
    /// it says what the type is, the property's own summary what this property holds.
    /// </summary>
    public JsonNode DescribeProperty(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (context.PropertyInfo is null || !CanCarryDescription(schema))
        {
            return schema;
        }

        if (context.PropertyAttributeProvider is not MemberInfo member || HasOwnDescriptionAttribute(member, context.ParameterAttributeProvider))
        {
            return schema;
        }

        var summary = _summaryReader.GetSummary(member);
        if (summary is null)
        {
            return schema;
        }

        var schemaObject = schema as JsonObject ?? new JsonObject();
        schemaObject[DescriptionKeyword] = summary;

        return schemaObject;
    }

    /// <summary>
    /// An object schema, or the boolean schema <c>true</c> the exporter gives a property any JSON value fits
    /// (<c>object</c>, <c>JsonElement</c>, <c>JsonNode</c>, <c>JsonDocument</c>, <c>dynamic</c>, a type with its own
    /// <c>[JsonConverter]</c>). <c>true</c> has no keywords to hold a description, so a
    /// described one becomes <c>{}</c>, which accepts the same values. A <c>false</c> schema accepts nothing, which
    /// <c>{}</c> would not keep, so it is left alone.
    /// </summary>
    private static bool CanCarryDescription(JsonNode schema)
    {
        return schema is JsonObject || schema.GetValueKind() == JsonValueKind.True;
    }

    /// <summary>
    /// A <see cref="DescriptionAttribute"/> on the property itself, or on the record constructor parameter it comes
    /// from, which the SDK reads as well.
    /// </summary>
    private static bool HasOwnDescriptionAttribute(MemberInfo member, ICustomAttributeProvider? constructorParameter)
    {
        return member.IsDefined(typeof(DescriptionAttribute), inherit: true) ||
            constructorParameter?.IsDefined(typeof(DescriptionAttribute), inherit: true) is true;
    }
}
