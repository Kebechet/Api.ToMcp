using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpOutputSchemaBuilderTests
{
    [Fact]
    public void Build_DescribesThePayload_AsMvcWritesItByDefault()
    {
        var properties = Build(typeof(Gadget)).GetProperty("properties");

        Assert.Equal(["name", "kind", "weight"], properties.EnumerateObject().Select(x => x.Name));
        Assert.Equal("integer", properties.GetProperty("kind").GetProperty("type").GetString());
    }

    [Fact]
    public void Build_FollowsTheJsonOptionsTheAppGaveMvc()
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.JsonSerializerOptions.PropertyNamingPolicy = null;
        jsonOptions.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        var properties = Build(typeof(Gadget), jsonOptions: jsonOptions).GetProperty("properties");

        Assert.Equal(["Name", "Kind", "Weight"], properties.EnumerateObject().Select(x => x.Name));
        Assert.Equal(["Small", "Large"], properties.GetProperty("Kind").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Build_StrictSchema_PromisesAValue()
    {
        var schema = Build(typeof(Gadget));

        Assert.Equal(JsonValueKind.String, schema.GetProperty("type").ValueKind);
        Assert.Equal("object", schema.GetProperty("type").GetString());
    }

    [Fact]
    public void Build_StrictSchema_DescribesAnArrayResponse()
    {
        var schema = Build(typeof(List<Gadget>));

        Assert.Equal("array", schema.GetProperty("type").GetString());
    }

    [Fact]
    public void Build_NullableSchema_AllowsTheValueOrNull()
    {
        var schema = Build(typeof(Gadget), isNullable: true);

        var branches = schema.GetProperty("anyOf").EnumerateArray().ToList();
        Assert.Equal(2, branches.Count);
        Assert.Equal("object", branches[0].GetProperty("type").GetString());
        Assert.Equal("{\"type\":\"null\"}", branches[1].GetRawText());
        Assert.False(schema.TryGetProperty("type", out _));
    }

    [Fact]
    public void Build_NullableSchema_KeepsEveryRefResolvable_ForARecursiveType()
    {
        var schema = JsonNode.Parse(Build(typeof(TreeNode), isNullable: true).GetRawText())!;

        var refs = CollectRefs(schema).ToList();

        Assert.NotEmpty(refs);
        Assert.All(refs, x => Assert.Equal("object", ResolvePointer(schema, x)?["type"]?.GetValue<string>()));
    }

    [Fact]
    public void Build_AppliesXmlDocDescriptions_FromTheInferenceOptions()
    {
        var describer = new XmlDocPropertyDescriber(new XmlDocSummaryReader());

        var properties = Build(typeof(Gadget), isNullable: true, inferenceOptions: describer).GetProperty("anyOf")[0].GetProperty("properties");

        Assert.Equal("Display name of the gadget.", properties.GetProperty("name").GetProperty("description").GetString());
    }

    [Fact]
    public void Build_DoesNotRequireAProperty_MvcOmitsWhenNull()
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        var required = GetRequired(Build(typeof(OrderRecord), jsonOptions: jsonOptions));

        Assert.Contains("id", required);
        Assert.DoesNotContain("note", required);
    }

    [Fact]
    public void Build_RequiresNoProperty_WhenMvcOmitsDefaultValues()
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;

        Assert.Empty(GetRequired(Build(typeof(OrderRecord), jsonOptions: jsonOptions)));
    }

    [Fact]
    public void Build_DoesNotRequireAProperty_ItsOwnJsonIgnoreConditionOmits()
    {
        var required = GetRequired(Build(typeof(Shipment)));

        Assert.Contains("carrier", required);
        Assert.DoesNotContain("tracking", required);
    }

    [Fact]
    public void Build_DoesNotRequireAnOmittedProperty_OfANestedObjectInANullableSchema()
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        var schema = Build(typeof(OrderEnvelope), isNullable: true, jsonOptions: jsonOptions);

        var order = schema.GetProperty("anyOf")[0].GetProperty("properties").GetProperty("order");
        Assert.Contains("id", GetRequired(order));
        Assert.DoesNotContain("note", GetRequired(order));
    }

    [Theory]
    [InlineData("at")]
    [InlineData("maybeAt")]
    [InlineData("opensAt")]
    [InlineData("maybeOpensAt")]
    public void Build_ClaimsNoFormat_ForAValueMvcWritesWithoutAnOffset(string propertyName)
    {
        // MVC writes a DateTime of unspecified kind as "2026-09-28T00:00:00" and a TimeOnly as "13:45:00"; the
        // date-time and time formats require an offset, so a validating client would reject them.
        var property = Build(typeof(Schedule)).GetProperty("properties").GetProperty(propertyName);

        Assert.False(property.TryGetProperty("format", out _));
    }

    [Theory]
    [InlineData("stampedAt", "date-time")]
    [InlineData("day", "date")]
    [InlineData("id", "uuid")]
    public void Build_KeepsTheFormat_OfAValueMvcAlwaysWritesInIt(string propertyName, string format)
    {
        var property = Build(typeof(Schedule)).GetProperty("properties").GetProperty(propertyName);

        Assert.Equal(format, property.GetProperty("format").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_AcceptsAnyValue_WhenMvcPreservesReferences(bool isNullable)
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.Preserve;

        var schema = Build(typeof(Gadget), isNullable, jsonOptions);

        Assert.Equal("{}", schema.GetRawText());
    }

    [Fact]
    public void Build_LeavesTheMvcJsonOptionsWritable()
    {
        var jsonOptions = new JsonOptions();

        Build(typeof(Gadget), jsonOptions: jsonOptions);

        Assert.False(jsonOptions.JsonSerializerOptions.IsReadOnly);
    }

    private static JsonElement Build(Type type, bool isNullable = false, JsonOptions? jsonOptions = null, XmlDocPropertyDescriber? inferenceOptions = null)
    {
        var builder = new McpOutputSchemaBuilder(Microsoft.Extensions.Options.Options.Create(jsonOptions ?? new JsonOptions()));

        return builder.Build(type, isNullable, inferenceOptions?.SchemaCreateOptions);
    }

    private static List<string> GetRequired(JsonElement schema)
    {
        return schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];
    }

    private static IEnumerable<string> CollectRefs(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var (name, value) in jsonObject)
            {
                if (name == "$ref")
                {
                    yield return value!.GetValue<string>();
                }

                foreach (var reference in CollectRefs(value))
                {
                    yield return reference;
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var reference in jsonArray.SelectMany(CollectRefs))
            {
                yield return reference;
            }
        }
    }

    private static JsonNode? ResolvePointer(JsonNode root, string reference)
    {
        var current = root;
        foreach (var segment in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/").Replace("~0", "~");
            current = current is JsonArray jsonArray ? jsonArray[int.Parse(name)] : current?[name];
        }

        return current;
    }

    /// <summary>A device in the catalog.</summary>
    public sealed class Gadget
    {
        /// <summary>Display name of the gadget.</summary>
        public string Name { get; set; } = "";

        public GadgetKind Kind { get; set; }

        public int Weight { get; set; }
    }

    public enum GadgetKind
    {
        Small,
        Large
    }

    public sealed class Schedule
    {
        public DateTime At { get; set; }

        public DateTime? MaybeAt { get; set; }

        public TimeOnly OpensAt { get; set; }

        public TimeOnly? MaybeOpensAt { get; set; }

        public DateTimeOffset StampedAt { get; set; }

        public DateOnly Day { get; set; }

        public Guid Id { get; set; }
    }

    public sealed record OrderRecord(Guid Id, string? Note, int Quantity = 1);

    public sealed class OrderEnvelope
    {
        public required OrderRecord Order { get; init; }
    }

    public sealed class Shipment
    {
        public required string Carrier { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public required string? Tracking { get; init; }
    }

    public sealed class TreeNode
    {
        public string Name { get; set; } = "";

        public List<TreeNode> Children { get; set; } = [];
    }
}
