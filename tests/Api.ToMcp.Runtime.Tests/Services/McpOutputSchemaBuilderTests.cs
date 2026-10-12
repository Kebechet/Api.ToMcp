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

    public sealed class TreeNode
    {
        public string Name { get; set; } = "";

        public List<TreeNode> Children { get; set; } = [];
    }
}
