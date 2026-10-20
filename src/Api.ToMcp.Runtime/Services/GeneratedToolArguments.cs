using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The JSON options a generated tool reads its arguments with and builds its input schema from: the MCP SDK's, without
/// the properties of an argument the tool never sends to the API. Advertised, a client fills them in for nothing, and an
/// <c>IFormFile</c> the SDK cannot even read, so the call failed. Left out, they are missing from the schema and from
/// its <c>required</c>, and a client that sends one anyway has it ignored.
/// </summary>
internal static class GeneratedToolArguments
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolver = options.TypeInfoResolver!.WithAddedModifier(LeaveOutUnsentProperties);
        options.MakeReadOnly();

        return options;
    }

    /// <summary>
    /// A property with neither a getter nor a setter is one the serializer and the schema exporter skip, as they do a
    /// <c>[JsonIgnore]</c> one; removing it instead would break a record whose constructor sets it. Such a constructor
    /// still reads the property's JSON value, so the property's converter skips that value and gives the default. It is
    /// no longer required either, which the serializer would refuse for a property it cannot set, failing every tool at
    /// startup.
    /// </summary>
    private static void LeaveOutUnsentProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in typeInfo.Properties.Where(IsNeverSent))
        {
            property.IsRequired = false;
            property.Get = null;
            property.Set = null;
            property.CustomConverter = (JsonConverter)Activator.CreateInstance(typeof(SkippingConverter<>).MakeGenericType(property.PropertyType))!;
        }
    }

    /// <summary>
    /// A property of a type MVC binds from a source of its own, such as a <c>CancellationToken</c> or an
    /// <c>IFormFile</c>, which neither the query string nor a JSON body carries; and one without a public getter, which
    /// the tool can only set: it leaves it out of a query string, and <c>JsonSerializer</c> out of a body.
    /// </summary>
    private static bool IsNeverSent(JsonPropertyInfo property)
    {
        return McpRequestValueFormatter.IsNotBoundFromTheQuery(property.PropertyType) || property is { Get: null, Set: not null };
    }

    /// <summary>
    /// Reads past a value and gives the type's default. It never writes one, as the property has no getter.
    /// </summary>
    private sealed class SkippingConverter<T> : JsonConverter<T>
    {
        public override bool HandleNull => true;

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteNullValue();
        }
    }
}
