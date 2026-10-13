using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Builds the output schema of a generated tool. Its structured content is the API's response body, written by MVC,
/// so the schema comes from MVC's JSON options rather than the MCP SDK's: the SDK writes enums as strings and drops
/// nulls, MVC by default does neither, and an app can change both with <c>AddJsonOptions</c>. A client that validates
/// structured content against the schema rejects every call the two disagree on.
/// </summary>
internal sealed class McpOutputSchemaBuilder
{
    private const string TypeKeyword = "type";
    private const string AnyOfKeyword = "anyOf";
    private const string RefKeyword = "$ref";
    private const string RequiredKeyword = "required";
    private const string FormatKeyword = "format";
    private const string SchemaKeyword = "$schema";
    private const string DescriptionKeyword = "description";
    private const string NullType = "null";

    /// <summary>
    /// Keywords that describe the schema document or the response as a whole, so they stay at the root when the
    /// value's schema moves into an <c>anyOf</c> branch.
    /// </summary>
    private static readonly string[] RootKeywords = [SchemaKeyword, DescriptionKeyword];

    private const string RootPointer = "#";
    private const string RootPointerPrefix = "#/";
    private const string ValueBranchPointer = "#/anyOf/0";

    private static readonly JsonElement AnyValueSchema = JsonDocument.Parse(new JsonObject().ToJsonString()).RootElement.Clone();

    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Whether MVC writes references as <c>$id</c>/<c>$ref</c>/<c>$values</c> metadata, which no exported schema
    /// describes; the exporter refuses such options. Every reference handler but <c>IgnoreCycles</c> preserves.
    /// </summary>
    private readonly bool _preservesReferences;

    public McpOutputSchemaBuilder(IOptions<JsonOptions> jsonOptions)
    {
        _serializerOptions = CopyOptions(jsonOptions.Value.JsonSerializerOptions);
        _preservesReferences = _serializerOptions.ReferenceHandler is { } referenceHandler && referenceHandler != ReferenceHandler.IgnoreCycles;
    }

    /// <summary>
    /// Whether a schema that accepts any value was built because MVC preserves references, for the startup warning.
    /// </summary>
    public bool HasBuiltAnyValueSchema { get; private set; }

    /// <summary>
    /// The schema of <paramref name="type"/> as MVC writes it. A strict schema promises a value; a nullable one is
    /// <c>{"anyOf": [value, {"type": "null"}]}</c>, which also makes the SDK wrap it as <c>{"result": ...}</c> for
    /// clients on a protocol older than 2026-07-28, whose structured content has to be an object. When MVC preserves
    /// references, a schema that accepts any value, so the tools still build.
    /// </summary>
    public JsonElement Build(Type type, bool isNullable, AIJsonSchemaCreateOptions? inferenceOptions)
    {
        if (_preservesReferences)
        {
            HasBuiltAnyValueSchema = true;
            return AnyValueSchema;
        }

        var schema = AIJsonUtilities.CreateJsonSchema(type, serializerOptions: _serializerOptions, inferenceOptions: MatchWhatMvcWrites(inferenceOptions));
        if (!isNullable || JsonNode.Parse(schema.GetRawText()) is not JsonObject valueSchema)
        {
            return schema;
        }

        using var document = JsonDocument.Parse(AllowNull(valueSchema).ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A copy, so building schemas does not make the app's MVC options read-only; without a resolver it gets the
    /// reflection one, which is what serialization falls back to as well.
    /// </summary>
    private static JsonSerializerOptions CopyOptions(JsonSerializerOptions mvcOptions)
    {
        var options = new JsonSerializerOptions(mvcOptions);
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();

        return options;
    }

    /// <summary>
    /// Takes out what the exporter promises but MVC does not always write, since a validating client rejects a body
    /// that breaks any of it: properties MVC can omit from <c>required</c>, and formats MVC can miss.
    /// </summary>
    private static AIJsonSchemaCreateOptions MatchWhatMvcWrites(AIJsonSchemaCreateOptions? inferenceOptions)
    {
        var transformSchemaNode = inferenceOptions?.TransformSchemaNode;

        return (inferenceOptions ?? AIJsonSchemaCreateOptions.Default) with
        {
            TransformSchemaNode = (context, schema) =>
                RemoveFormatMvcCanMiss(context, RemoveOmittableFromRequired(context, transformSchemaNode is null ? schema : transformSchemaNode(context, schema)))
        };
    }

    /// <summary>
    /// The exporter lists every required constructor parameter and <c>required</c> member in <c>required</c>, but MVC
    /// leaves a property out of the body when an ignore condition says so (a positional record's nullable parameter
    /// under <c>WhenWritingNull</c>, say).
    /// </summary>
    private static JsonNode RemoveOmittableFromRequired(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (context.TypeInfo.Kind != JsonTypeInfoKind.Object || schema is not JsonObject schemaObject || schemaObject[RequiredKeyword] is not JsonArray required)
        {
            return schema;
        }

        var omittableNames = context.TypeInfo.Properties
            .Where(CanBeOmitted)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in required.Where(x => x is JsonValue value && value.TryGetValue<string>(out var text) && omittableNames.Contains(text)).ToList())
        {
            required.Remove(name);
        }

        if (required.Count == 0)
        {
            schemaObject.Remove(RequiredKeyword);
        }

        return schema;
    }

    /// <summary>
    /// The <c>date-time</c> and <c>time</c> formats require an offset, which MVC writes for a <c>DateTime</c> only when
    /// its kind is UTC or local (<c>"2026-09-28T00:00:00"</c> otherwise, as from a database) and never for a
    /// <c>TimeOnly</c> (<c>"13:45:00"</c>), so both stay plain strings.
    /// </summary>
    private static JsonNode RemoveFormatMvcCanMiss(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        var type = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
        if ((type == typeof(DateTime) || type == typeof(TimeOnly)) && schema is JsonObject schemaObject)
        {
            schemaObject.Remove(FormatKeyword);
        }

        return schema;
    }

    /// <summary>
    /// Whether MVC can write the object without the property: a custom <c>ShouldSerialize</c>, or the property's own
    /// <c>[JsonIgnore(Condition = ...)]</c>, else the options' <c>DefaultIgnoreCondition</c>, omitting it.
    /// </summary>
    private static bool CanBeOmitted(JsonPropertyInfo property)
    {
        if (property.ShouldSerialize is not null)
        {
            return true;
        }

        var ignoreCondition = property.AttributeProvider?
            .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .OfType<JsonIgnoreAttribute>()
            .FirstOrDefault()?
            .Condition ?? property.Options.DefaultIgnoreCondition;

        return ignoreCondition switch
        {
            JsonIgnoreCondition.WhenWritingDefault => true,
            JsonIgnoreCondition.WhenWritingNull => !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null,
#if NET9_0_OR_GREATER
            JsonIgnoreCondition.WhenWriting => true,
#endif
            _ => false
        };
    }

    private static JsonObject AllowNull(JsonObject valueSchema)
    {
        var outputSchema = new JsonObject();
        foreach (var keyword in RootKeywords)
        {
            if (valueSchema.Remove(keyword, out var value))
            {
                outputSchema[keyword] = value;
            }
        }

        // The value's schema moves one level down, so a JSON pointer into it (the exporter's $ref for a recursive or
        // repeated type) has to follow; the SDK rewrites them the same way when it adds its own envelope.
        RewriteRefs(valueSchema);
        outputSchema[AnyOfKeyword] = new JsonArray(valueSchema, new JsonObject { [TypeKeyword] = NullType });

        return outputSchema;
    }

    private static void RewriteRefs(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            if (jsonObject[RefKeyword] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
            {
                if (reference == RootPointer)
                {
                    jsonObject[RefKeyword] = ValueBranchPointer;
                }
                else if (reference.StartsWith(RootPointerPrefix, StringComparison.Ordinal))
                {
                    jsonObject[RefKeyword] = ValueBranchPointer + reference.Substring(1);
                }
            }

            foreach (var (_, value) in jsonObject)
            {
                RewriteRefs(value);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                RewriteRefs(item);
            }
        }
    }
}
