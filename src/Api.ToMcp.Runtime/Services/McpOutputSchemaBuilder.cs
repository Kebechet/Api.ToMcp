using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string NullType = "null";

    /// <summary>
    /// Keywords that describe the schema document or the response as a whole, so they stay at the root when the
    /// value's schema moves into an <c>anyOf</c> branch.
    /// </summary>
    private static readonly string[] RootKeywords = ["$schema", "description"];

    private const string ValueBranchPointer = "#/anyOf/0";

    private readonly JsonSerializerOptions _serializerOptions;

    public McpOutputSchemaBuilder(IOptions<JsonOptions> jsonOptions)
    {
        _serializerOptions = CopyOptions(jsonOptions.Value.JsonSerializerOptions);
    }

    /// <summary>
    /// The schema of <paramref name="type"/> as MVC writes it. A strict schema promises a value; a nullable one is
    /// <c>{"anyOf": [value, {"type": "null"}]}</c>, which also makes the SDK wrap it as <c>{"result": ...}</c> for
    /// clients on a protocol older than 2026-07-28, whose structured content has to be an object.
    /// </summary>
    public JsonElement Build(Type type, bool isNullable, AIJsonSchemaCreateOptions? inferenceOptions)
    {
        var schema = AIJsonUtilities.CreateJsonSchema(type, serializerOptions: _serializerOptions, inferenceOptions: inferenceOptions);
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
                if (reference == "#")
                {
                    jsonObject[RefKeyword] = ValueBranchPointer;
                }
                else if (reference.StartsWith("#/", StringComparison.Ordinal))
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
