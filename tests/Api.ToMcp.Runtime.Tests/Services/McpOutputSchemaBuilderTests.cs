using System.ComponentModel;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_AppliesXmlDocDescriptions_ToAPropertyThatAcceptsAnyValue(bool isNullable)
    {
        var describer = new XmlDocPropertyDescriber(new XmlDocSummaryReader());

        var schema = Build(typeof(Reading), isNullable, inferenceOptions: describer);

        var properties = (isNullable ? schema.GetProperty("anyOf")[0] : schema).GetProperty("properties");
        Assert.Equal("""{"description":"Payload as the sensor sent it."}""", properties.GetProperty("payload").GetRawText());
        Assert.Equal("""{"description":"Calibration data in the shape the sensor uses."}""", properties.GetProperty("calibration").GetRawText());
    }

    [Theory]
    [InlineData("total", false)]
    [InlineData("discount", false)]
    [InlineData("shipping", false)]
    [InlineData("codes", false)]
    [InlineData("total", true)]
    public void Build_AcceptsAnyValue_ForAPropertyWrittenByItsOwnConverter(string propertyName, bool isNullable)
    {
        // The exporter describes such a property by its type's shape, while MVC writes what the converter writes.
        var schema = Build(typeof(Invoice), isNullable);

        var properties = (isNullable ? schema.GetProperty("anyOf")[0] : schema).GetProperty("properties");
        Assert.Equal(JsonValueKind.True, properties.GetProperty(propertyName).ValueKind);
    }

    [Fact]
    public void Build_KeepsTheSchema_OfAPropertyWithoutAConverterOfItsOwn_OrWithOneTheExporterUnderstands()
    {
        var properties = Build(typeof(Invoice)).GetProperty("properties");

        Assert.Equal("object", properties.GetProperty("subtotal").GetProperty("type").GetString());
        Assert.Equal("""["object","null"]""", properties.GetProperty("deposit").GetProperty("type").GetRawText());
        Assert.Equal(["Small", "Large"], properties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["small", "large"], properties.GetProperty("size").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(JsonValueKind.True, properties.GetProperty("issuedAt").ValueKind);
    }

    [Fact]
    public void Build_KeepsTheDescription_OfAPropertyWrittenByItsOwnConverter()
    {
        var describer = new XmlDocPropertyDescriber(new XmlDocSummaryReader());

        var properties = Build(typeof(Invoice), inferenceOptions: describer).GetProperty("properties");

        Assert.Equal("""{"description":"Amount due as the till prints it."}""", properties.GetProperty("total").GetRawText());
        Assert.Equal("""{"description":"Tax as the till prints it."}""", properties.GetProperty("tax").GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_KeepsEveryRefResolvable_WhenItsTargetLiesInsideAConvertedProperty(bool isNullable)
    {
        // The exporter writes a node it meets again as a $ref to its first place, here inside a converted property,
        // whose subtree the any-value schema replaces.
        var schema = JsonNode.Parse(Build(typeof(RefInvoice), isNullable).GetRawText())!;
        var properties = (isNullable ? schema["anyOf"]![0]! : schema)["properties"]!;

        var refs = CollectRefs(schema).ToList();

        Assert.All(refs, x => Assert.NotNull(ResolvePointer(schema, x)));
        Assert.Equal(JsonValueKind.True, properties["first"]!.GetValueKind());
        Assert.Equal(JsonValueKind.True, properties["payments"]!.GetValueKind());
        Assert.Equal(["amount", "currency"], ResolveRefs(schema, properties["second"]!["properties"]!["amount"]!)["properties"]!.AsObject().Select(x => x.Key));
        Assert.Equal(["amount", "currency"], ResolveRefs(schema, properties["refunds"]!["items"]!)["properties"]!.AsObject().Select(x => x.Key));
    }

    [Fact]
    public void Build_KeepsEveryRefResolvable_ForARecursiveTypeFirstMetInsideAConvertedProperty()
    {
        var schema = JsonNode.Parse(Build(typeof(BranchPair)).GetRawText())!;

        // The plain branch's children were a $ref into the converted branch; they hold a copy now, and refer to it.
        var refs = CollectRefs(schema).ToList();

        Assert.NotEmpty(refs);
        Assert.All(refs, x => Assert.NotNull(ResolvePointer(schema, x)));
        var plainChildren = ResolveRefs(schema, schema["properties"]!["plain"]!["properties"]!["children"]!);
        Assert.Equal("array", plainChildren["type"]!.GetValue<string>());
        Assert.Equal(["name", "children"], ResolveRefs(schema, plainChildren["items"]!)["properties"]!.AsObject().Select(x => x.Key));
    }

    [Fact]
    public void ConvertedProperties_AreWrittenAsTheirConverterWritesThem_NotInTheShapeOfTheirType()
    {
        // Why such a property gets a schema that accepts any value: MVC writes the converter's string.
        var invoice = new Invoice { Total = new Money { Amount = 12.5m, Currency = "EUR" } };

        var body = JsonNode.Parse(JsonSerializer.Serialize(invoice, new JsonOptions().JsonSerializerOptions))!;

        Assert.Equal("12.5 EUR", body["total"]!.GetValue<string>());
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

    [Theory]
    [InlineData(typeof(CycleOrder))]
    [InlineData(typeof(CycleCustomer))]
    [InlineData(typeof(List<CycleOrder>))]
    [InlineData(typeof(TreeNode))]
    [InlineData(typeof(Employee))]
    [InlineData(typeof(Box))]
    [InlineData(typeof(Entry))]
    [InlineData(typeof(Asset))]
    [InlineData(typeof(Knot))]
    [InlineData(typeof(Envelope))]
    [InlineData(typeof(Dictionary<string, CycleOrder>))]
    public void Build_AcceptsAnyValue_WhenMvcIgnoresCycles_InATypeThatCanContainItself(Type type)
    {
        // MVC writes null where an object would contain itself, and no schema of the type can say where.
        Assert.Equal("{}", Build(type, jsonOptions: IgnoringCycles()).GetRawText());
        Assert.Equal("{}", Build(type, isNullable: true, jsonOptions: IgnoringCycles()).GetRawText());
    }

    [Theory]
    [InlineData(typeof(Gadget), "object")]
    [InlineData(typeof(List<Gadget>), "array")]
    [InlineData(typeof(Dictionary<string, Gadget>), "object")]
    [InlineData(typeof(GadgetPair), "object")]
    [InlineData(typeof(OrderEnvelope), "object")]
    [InlineData(typeof(Schedule), "object")]
    [InlineData(typeof(Holder), "object")]
    [InlineData(typeof(Author), "object")]
    public void Build_KeepsTheTypedSchema_WhenMvcIgnoresCycles_ButTheTypeCannotContainItself(Type type, string expectedType)
    {
        Assert.Equal(expectedType, Build(type, jsonOptions: IgnoringCycles()).GetProperty("type").GetString());
    }

    [Fact]
    public void Build_KeepsTheTypedSchema_WhenMvcIgnoresCycles_ForAPolymorphicTypeWhoseDerivedTypesOnlyHoldEachOther()
    {
        // A listing holds its seller, both written as a resource, but a listing is never its own seller.
        var schema = Build(typeof(Resource), jsonOptions: IgnoringCycles());

        Assert.NotEqual("{}", schema.GetRawText());
    }

    [Fact]
    public void IgnoredCycles_WriteNull_WhereTheTypedSchemaPromisesAnObject()
    {
        // Why a type that can contain itself gets no typed schema: the order inside its own customer's orders is null.
        var order = new CycleOrder { Id = 1, Customer = new CycleCustomer { Name = "Ada" } };
        order.Customer.Orders.Add(order);

        var body = JsonSerializer.Serialize(order, IgnoringCycles().JsonSerializerOptions);

        Assert.Equal("{\"id\":1,\"customer\":{\"name\":\"Ada\",\"orders\":[null]}}", body);
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

    private static JsonOptions IgnoringCycles()
    {
        var jsonOptions = new JsonOptions();
        jsonOptions.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;

        return jsonOptions;
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

    /// <summary>
    /// The node itself, or the node its <c>$ref</c> points at, followed until it is not a reference.
    /// </summary>
    private static JsonNode ResolveRefs(JsonNode root, JsonNode node)
    {
        while (node is JsonObject jsonObject && jsonObject["$ref"] is JsonValue reference)
        {
            node = ResolvePointer(root, reference.GetValue<string>())!;
        }

        return node;
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

    public sealed class Invoice
    {
        /// <summary>Amount due as the till prints it.</summary>
        [JsonConverter(typeof(MoneyConverter))]
        public Money Total { get; set; } = new();

        /// <summary>XML summary that must lose to the attribute.</summary>
        [Description("Tax as the till prints it.")]
        [JsonConverter(typeof(MoneyConverter))]
        public Money Tax { get; set; } = new();

        [JsonConverter(typeof(MoneyConverter))]
        public Money? Discount { get; set; }

        [JsonConverter(typeof(ParcelFeeConverter))]
        public ParcelFee? Shipping { get; set; }

        [JsonConverter(typeof(CommaSeparatedConverter))]
        public List<string> Codes { get; set; } = [];

        public Money Subtotal { get; set; } = new();

        public ParcelFee? Deposit { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public GadgetKind Kind { get; set; }

        [JsonConverter(typeof(CamelCaseEnumConverter))]
        public GadgetKind Size { get; set; }

        [JsonConverter(typeof(UnixTimeConverter))]
        public DateTime IssuedAt { get; set; }
    }

    public sealed class RefInvoice
    {
        [JsonConverter(typeof(LineConverter))]
        public Line First { get; set; } = new();

        public Line Second { get; set; } = new();

        [JsonConverter(typeof(MoneyListConverter))]
        public List<Money> Payments { get; set; } = [];

        public List<Money> Refunds { get; set; } = [];
    }

    public sealed class Line
    {
        public Money Amount { get; set; } = new();

        public string Sku { get; set; } = "";
    }

    public sealed class LineConverter : JsonConverter<Line>
    {
        public override Line Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Line { Sku = reader.GetString()! };
        }

        public override void Write(Utf8JsonWriter writer, Line value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Sku);
        }
    }

    public sealed class MoneyListConverter : JsonConverter<List<Money>>
    {
        public override List<Money> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Skip();
            return [];
        }

        public override void Write(Utf8JsonWriter writer, List<Money> value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value.Count);
        }
    }

    public sealed class BranchPair
    {
        [JsonConverter(typeof(BranchConverter))]
        public Branch Converted { get; set; } = new();

        public Branch Plain { get; set; } = new();
    }

    public sealed class Branch
    {
        public string Name { get; set; } = "";

        public List<Branch> Children { get; set; } = [];
    }

    public sealed class BranchConverter : JsonConverter<Branch>
    {
        public override Branch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Branch { Name = reader.GetString()! };
        }

        public override void Write(Utf8JsonWriter writer, Branch value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Name);
        }
    }

    public sealed class Money
    {
        public decimal Amount { get; set; }

        public string Currency { get; set; } = "";
    }

    public readonly record struct ParcelFee(decimal Amount);

    public sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parts = reader.GetString()!.Split(' ');
            return new Money { Amount = decimal.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Currency = parts[1] };
        }

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
        {
            writer.WriteStringValue($"{value.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {value.Currency}");
        }
    }

    public sealed class ParcelFeeConverter : JsonConverter<ParcelFee>
    {
        public override ParcelFee Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new ParcelFee(reader.GetDecimal());
        }

        public override void Write(Utf8JsonWriter writer, ParcelFee value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value.Amount);
        }
    }

    public sealed class CommaSeparatedConverter : JsonConverter<List<string>>
    {
        public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return [.. reader.GetString()!.Split(',')];
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(string.Join(',', value));
        }
    }

    public sealed class CamelCaseEnumConverter : JsonStringEnumConverter
    {
        public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase)
        {
        }
    }

    public sealed class UnixTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return DateTime.UnixEpoch.AddSeconds(reader.GetInt64());
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((long)(value - DateTime.UnixEpoch).TotalSeconds);
        }
    }

    public sealed class Reading
    {
        /// <summary>Payload as the sensor sent it.</summary>
        public JsonElement Payload { get; set; }

        /// <summary>Calibration data in the shape the sensor uses.</summary>
        public object? Calibration { get; set; }
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

    public sealed class CycleOrder
    {
        public int Id { get; set; }

        public CycleCustomer Customer { get; set; } = null!;
    }

    public sealed class CycleCustomer
    {
        public string Name { get; set; } = "";

        public List<CycleOrder> Orders { get; set; } = [];
    }

    public class PersonBase
    {
        public string Name { get; set; } = "";
    }

    public sealed class Employee : PersonBase
    {
        public PersonBase Manager { get; set; } = null!;
    }

    public interface IContent
    {
        string Label { get; }
    }

    public sealed class Box : IContent
    {
        public string Label { get; set; } = "";

        public IContent Inside { get; set; } = null!;
    }

    [JsonDerivedType(typeof(Folder), "folder")]
    [JsonDerivedType(typeof(FileEntry), "file")]
    public abstract class Entry
    {
        public string Name { get; set; } = "";
    }

    public sealed class Folder : Entry
    {
        public List<Entry> Entries { get; set; } = [];
    }

    public sealed class FileEntry : Entry
    {
        public long Size { get; set; }
    }

    public interface IContainer;

    /// <summary>Only its derived type can hold itself, through an interface the base does not implement.</summary>
    [JsonDerivedType(typeof(Crate), "crate")]
    public abstract class Asset
    {
        public string Name { get; set; } = "";
    }

    public sealed class Crate : Asset, IContainer
    {
        public IContainer Parent { get; set; } = null!;
    }

    [JsonDerivedType(typeof(Listing), "listing")]
    [JsonDerivedType(typeof(Seller), "seller")]
    public abstract class Resource
    {
        public int Id { get; set; }
    }

    public sealed class Listing : Resource
    {
        public Seller Seller { get; set; } = null!;
    }

    public sealed class Seller : Resource
    {
        public string Name { get; set; } = "";
    }

    /// <summary>Contains itself only through a nullable struct.</summary>
    public sealed class Knot
    {
        public Loop? Loop { get; set; }
    }

    public struct Loop
    {
        public Knot Inner { get; set; }
    }

    /// <summary>Reaches a type that can contain itself only through a nullable struct.</summary>
    public sealed class Envelope
    {
        public Slot? Slot { get; set; }
    }

    public struct Slot
    {
        public TreeNode Node { get; set; }
    }

    /// <summary>The way an EF Core entity usually avoids a cycle: its back reference is never written.</summary>
    public sealed class Author
    {
        public string Name { get; set; } = "";

        public List<Book> Books { get; set; } = [];
    }

    public sealed class Book
    {
        public string Title { get; set; } = "";

        [JsonIgnore]
        public Author Author { get; set; } = null!;
    }

    public sealed class GadgetPair
    {
        public Gadget Left { get; set; } = new();

        public Gadget Right { get; set; } = new();
    }

    public sealed class Holder
    {
        public string Name { get; set; } = "";

        public object Anything { get; set; } = new();
    }
}
