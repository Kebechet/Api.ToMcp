using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Gives every tool-argument property that has no description one from the property's XML <c>&lt;summary&gt;</c>.
/// The MCP SDK builds a tool's input schema by reflecting over its parameter types and reads only
/// <see cref="DescriptionAttribute"/>, so without this a request DTO's properties, nested objects and array items
/// reach the agent as bare types. An explicit <see cref="DescriptionAttribute"/> always wins.
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

    /// <summary>
    /// Called by the schema exporter for every node of a parameter's type graph, after it applied the
    /// <see cref="DescriptionAttribute"/>s. Only nodes that stand for a property or field are touched: the tool's own
    /// parameters already carry their descriptions, and a node without a <see cref="MemberInfo"/> behind it (an array's
    /// item type, a root) has no XML docs to read.
    /// </summary>
    public JsonNode DescribeProperty(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (context.PropertyInfo is null || schema is not JsonObject schemaObject || schemaObject.ContainsKey(DescriptionKeyword))
        {
            return schema;
        }

        if (context.PropertyAttributeProvider is not MemberInfo member || member.IsDefined(typeof(DescriptionAttribute), inherit: true))
        {
            return schema;
        }

        var summary = _summaryReader.GetSummary(member);
        if (summary is not null)
        {
            schemaObject[DescriptionKeyword] = summary;
        }

        return schema;
    }
}
