using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The schema of a <c>Uri</c> in a generated tool's input and output, without the <c>uri</c> format the schema exporter
/// gives it. That format requires an absolute URI, but System.Text.Json and MVC's model binding read a relative one into
/// a <c>Uri</c>, and System.Text.Json writes a <c>Uri</c> back as the string it was made from: <c>/images/1.png</c>, or
/// an absolute one with a space or a non-ASCII letter as given, which <c>uri-reference</c> does not allow either. A
/// client that validates formats would reject a result holding such a URL, and refuse to send one the API accepts. See
/// https://github.com/Kebechet/Api.ToMcp/issues/54.
/// </summary>
internal static class UriSchema
{
    /// <summary>
    /// <paramref name="options"/> with the format removed after their own
    /// <see cref="AIJsonSchemaCreateOptions.TransformSchemaNode"/>.
    /// </summary>
    public static AIJsonSchemaCreateOptions Apply(AIJsonSchemaCreateOptions options)
    {
        var transformSchemaNode = options.TransformSchemaNode;

        return options with
        {
            TransformSchemaNode = (context, schema) =>
                RemoveFormat(context, transformSchemaNode is null ? schema : transformSchemaNode(context, schema))
        };
    }

    public static JsonNode RemoveFormat(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        var schemaObject = schema as JsonObject;
        if (context.TypeInfo.Type == typeof(Uri) && schemaObject is not null)
        {
            schemaObject.Remove(JsonSchemaKeywords.Format);
        }

        return schema;
    }
}
