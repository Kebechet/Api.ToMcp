using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The schema of a property with a <c>[JsonConverter]</c> of its own. The schema exporter describes such a property by
/// its type when the type is an object or a collection, but the serializer writes and reads it as the converter does,
/// such as the string <c>"12.50 EUR"</c> for a <c>Money</c> object, so a client that validates against that schema
/// rejects the value, and one that follows it sends a value the converter cannot read. Such a property accepts any
/// value instead, as a type with a converter of its own already does. A property of a type without members, such as an
/// enum or a <c>DateTime</c>, keeps its schema: there the exporter reads the converter, and knows those System.Text.Json
/// ships, such as <c>JsonStringEnumConverter</c>.
/// </summary>
internal static class ConvertedPropertySchema
{
    private const string DescriptionKeyword = "description";

    /// <summary>
    /// <paramref name="options"/>, or the defaults, with this schema applied before their own
    /// <see cref="AIJsonSchemaCreateOptions.TransformSchemaNode"/>, so that one can still describe the property.
    /// </summary>
    public static AIJsonSchemaCreateOptions Apply(AIJsonSchemaCreateOptions? options)
    {
        var transformSchemaNode = options?.TransformSchemaNode;

        return (options ?? AIJsonSchemaCreateOptions.Default) with
        {
            TransformSchemaNode = (context, schema) =>
            {
                var convertedSchema = AcceptWhatTheConverterWrites(context, schema);
                return transformSchemaNode is null ? convertedSchema : transformSchemaNode(context, convertedSchema);
            }
        };
    }

    /// <summary>
    /// The boolean schema <c>true</c>, or <c>{"description": ...}</c> when the exporter gave the property a
    /// description, for a property of an object or collection type with a converter of its own; any other schema as
    /// it is.
    /// </summary>
    private static JsonNode AcceptWhatTheConverterWrites(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (context.PropertyInfo?.CustomConverter is null || context.TypeInfo.Kind == JsonTypeInfoKind.None)
        {
            return schema;
        }

        if (schema is JsonObject schemaObject && schemaObject[DescriptionKeyword] is { } description)
        {
            return new JsonObject { [DescriptionKeyword] = description.DeepClone() };
        }

        return JsonValue.Create(true);
    }
}
