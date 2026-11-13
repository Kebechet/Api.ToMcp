using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// A generated tool's input and output schema without a <c>format</c> the app does not hold a value to. The schema
/// exporter gives a <c>Uri</c> the <c>uri</c> format, which requires an absolute URI, and Microsoft.Extensions.AI gives a
/// string the format its DataAnnotations name: <c>uri</c> for <c>[Url]</c>, <c>[DataType(DataType.Url)]</c> and
/// <c>[DataType(DataType.ImageUrl)]</c>, <c>email</c> for <c>[EmailAddress]</c> and <c>[DataType(DataType.EmailAddress)]</c>,
/// and <c>date-time</c>, <c>date</c> or <c>time</c> for the <c>[DataType]</c> of a date or a time. But System.Text.Json
/// and MVC's model binding read a relative URL into a <c>Uri</c>, and System.Text.Json writes a <c>Uri</c> back as the
/// string it was made from: <c>/images/1.png</c>, or an absolute one with a space or a non-ASCII letter as given, which
/// <c>uri-reference</c> does not allow either. MVC never validates a <c>[DataType]</c>, and checks no attribute on what
/// it writes. A client that validates formats would reject a result holding such a value, and refuse to send one the API
/// accepts. See https://github.com/Kebechet/Api.ToMcp/issues/54.
/// </summary>
internal static class UncheckedFormatSchema
{
    private const string UriFormat = "uri";

    /// <summary>
    /// <paramref name="options"/> for a generated tool's input schema, with the formats MVC does not check removed after
    /// their own <see cref="AIJsonSchemaCreateOptions.TransformSchemaNode"/>. A format an attribute MVC validates gives
    /// stays: <c>uri</c> under <c>[Url]</c>, which requires an absolute http, https or ftp URL, and <c>email</c> under
    /// <c>[EmailAddress]</c>.
    /// </summary>
    public static AIJsonSchemaCreateOptions ApplyToInput(AIJsonSchemaCreateOptions options)
    {
        var transformSchemaNode = options.TransformSchemaNode;

        return options with
        {
            TransformSchemaNode = (context, schema) =>
                RemoveFromInput(context, transformSchemaNode is null ? schema : transformSchemaNode(context, schema))
        };
    }

    /// <summary>
    /// <paramref name="schema"/> of a value MVC writes, without the <c>uri</c> format and without any format a string's
    /// DataAnnotations give it, as MVC checks no attribute on what it writes.
    /// </summary>
    public static JsonNode RemoveFromOutput(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        var format = GetFormat(schema);
        if (format == UriFormat || (format is not null && context.TypeInfo.Type == typeof(string)))
        {
            schema.AsObject().Remove(JsonSchemaKeywords.Format);
        }

        return schema;
    }

    private static JsonNode RemoveFromInput(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        var format = GetFormat(schema);
        if (format is null)
        {
            return schema;
        }

        var attributes = GetAttributes(context);
        var isUncheckedUri = format == UriFormat && !attributes.Any(x => x is UrlAttribute);
        var isUncheckedDataType = format != UriFormat &&
            context.TypeInfo.Type == typeof(string) &&
            attributes.Any(IsPlainDataType) &&
            !attributes.Any(x => x is DataTypeAttribute && !IsPlainDataType(x));
        if (isUncheckedUri || isUncheckedDataType)
        {
            schema.AsObject().Remove(JsonSchemaKeywords.Format);
        }

        return schema;
    }

    private static string? GetFormat(JsonNode schema)
    {
        var schemaObject = schema as JsonObject;
        var format = schemaObject?[JsonSchemaKeywords.Format] as JsonValue;
        if (format is null || !format.TryGetValue<string>(out var text))
        {
            return null;
        }

        return text;
    }

    /// <summary>
    /// The attributes of the parameter or the property the node describes.
    /// </summary>
    private static List<Attribute> GetAttributes(AIJsonSchemaCreateContext context)
    {
        return new[] { context.ParameterAttributeProvider, context.PropertyAttributeProvider }
            .OfType<ICustomAttributeProvider>()
            .SelectMany(x => x.GetCustomAttributes(inherit: true).OfType<Attribute>())
            .ToList();
    }

    /// <summary>
    /// A <c>[DataType]</c> itself, whose <c>IsValid</c> accepts every value, rather than a subclass that validates, such as
    /// <c>[Url]</c> or <c>[EmailAddress]</c>.
    /// </summary>
    private static bool IsPlainDataType(Attribute attribute)
    {
        return attribute.GetType() == typeof(DataTypeAttribute);
    }
}
