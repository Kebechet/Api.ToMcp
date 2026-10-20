using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Api.ToMcp.Runtime.Services;
using ModelContextProtocol.Server;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class XmlDocPropertyDescriberTests
{
    private static readonly JsonElement InputSchema = CreateInputSchema();

    [Fact]
    public void DescribeProperty_FillsAMissingDescription_FromTheXmlSummary()
    {
        Assert.Equal("Customer placing the order.", GetDescription("properties", "order", "properties", "customer"));
    }

    [Fact]
    public void DescribeProperty_LetsAnExplicitDescriptionAttributeWin()
    {
        Assert.Equal("Attribute wins.", GetDescription("properties", "order", "properties", "priority"));
        Assert.Equal("Attribute wins over any value.", GetDescription("properties", "order", "properties", "tag"));
    }

    [Theory]
    [InlineData("filter", "Free-form filter; any JSON the endpoint understands.")]
    [InlineData("optionalFilter", "Filter the caller may leave out.")]
    [InlineData("payload", "Value stored with the order as sent.")]
    [InlineData("metadata", "Extra data kept with the order.")]
    [InlineData("settings", "Settings as a JSON tree.")]
    [InlineData("snapshot", "Order as the shop exported it.")]
    [InlineData("total", "Total as the till printed it.")]
    public void DescribeProperty_DescribesAPropertyWhoseSchemaAcceptsAnyValue(string propertyName, string summary)
    {
        // The exporter gives these types the boolean schema true, which cannot hold a description.
        var schema = GetSchema("properties", "order", "properties", propertyName);

        Assert.Equal($$"""{"description":"{{summary}}"}""", schema.GetRawText());
    }

    [Fact]
    public void DescribeProperty_LeavesAnUndocumentedPropertyThatAcceptsAnyValue_AsTheBooleanSchema()
    {
        Assert.Equal(JsonValueKind.True, GetSchema("properties", "order", "properties", "undocumentedPayload").ValueKind);
    }

    [Fact]
    public void DescribeProperty_PrefersThePropertySummary_OverADescriptionOnThePropertysType()
    {
        // The SDK copies a [Description] on the property's type onto the property; the property's own docs are more specific.
        Assert.Equal("Where to send the invoice.", GetDescription("properties", "order", "properties", "billTo"));
        Assert.Equal("A postal address.", GetDescription("properties", "order", "properties", "returnTo"));
    }

    [Fact]
    public void DescribeProperty_LetsADescriptionOnARecordParameterWin()
    {
        Assert.Equal("Code as printed on the coupon.", GetDescription("properties", "order", "properties", "coupon", "properties", "code"));
    }

    [Fact]
    public void DescribeProperty_DescribesNestedObjectAndArrayItemProperties()
    {
        Assert.Equal("Street and number.", GetDescription("properties", "order", "properties", "shipTo", "properties", "street"));
        Assert.Equal("Catalogue id of the product.", GetDescription("properties", "order", "properties", "lines", "items", "properties", "productId"));
    }

    [Fact]
    public void DescribeProperty_LeavesParameterNodesAndUndocumentedPropertiesAlone()
    {
        Assert.Equal("Order to create.", GetDescription("properties", "order"));
        // Named like the documented OrderRequest.Customer: a tool parameter described from a same-named property, as a
        // ParameterDescriptionProvider could do, would take that property's summary.
        Assert.Null(GetDescription("properties", "customer"));
        Assert.Null(GetDescription("properties", "order", "properties", "undocumented"));
        Assert.False(InputSchema.GetProperty("properties").TryGetProperty("cancellationToken", out _));
    }

    private static JsonElement CreateInputSchema()
    {
        var describer = new XmlDocPropertyDescriber(new XmlDocSummaryReader());
        var createOrder = typeof(XmlDocPropertyDescriberTests).GetMethod(nameof(CreateOrder), BindingFlags.NonPublic | BindingFlags.Static)!;

        var tool = McpServerTool.Create(createOrder, options: new McpServerToolCreateOptions { SchemaCreateOptions = describer.SchemaCreateOptions });

        return tool.ProtocolTool.InputSchema;
    }

    private static string? GetDescription(params string[] path)
    {
        return GetSchema(path).TryGetProperty("description", out var description)
            ? description.GetString()
            : null;
    }

    private static JsonElement GetSchema(params string[] path)
    {
        var node = InputSchema;
        foreach (var segment in path)
        {
            node = node.GetProperty(segment);
        }

        return node;
    }

    [Description("Creates an order.")]
    private static string CreateOrder([Description("Order to create.")] OrderRequest order, string customer, CancellationToken cancellationToken)
    {
        return $"{order.Customer}:{customer}";
    }

    public sealed class OrderRequest
    {
        /// <summary>Customer placing the order.</summary>
        public string? Customer { get; set; }

        /// <summary>XML summary that must lose to the attribute.</summary>
        [Description("Attribute wins.")]
        public int Priority { get; set; }

        /// <summary>Where to ship.</summary>
        public Address? ShipTo { get; set; }

        /// <summary>Lines of the order.</summary>
        public List<OrderLine> Lines { get; set; } = [];

        public string? Undocumented { get; set; }

        /// <summary>Where to send the invoice.</summary>
        public PostalAddress? BillTo { get; set; }

        public PostalAddress? ReturnTo { get; set; }

        /// <summary>Promotion applied to the order.</summary>
        public Coupon? Coupon { get; set; }

        /// <summary>Free-form filter; any JSON the endpoint understands.</summary>
        public JsonElement Filter { get; set; }

        /// <summary>Filter the caller may leave out.</summary>
        public JsonElement? OptionalFilter { get; set; }

        /// <summary>Value stored with the order as sent.</summary>
        public object Payload { get; set; } = new();

        /// <summary>Extra data kept with the order.</summary>
        public object? Metadata { get; set; }

        /// <summary>Settings as a JSON tree.</summary>
        public JsonNode? Settings { get; set; }

        /// <summary>Order as the shop exported it.</summary>
        public JsonDocument? Snapshot { get; set; }

        /// <summary>Total as the till printed it.</summary>
        public Money Total { get; set; }

        public object? UndocumentedPayload { get; set; }

        /// <summary>XML summary that must lose to the attribute.</summary>
        [Description("Attribute wins over any value.")]
        public object? Tag { get; set; }
    }

    /// <summary>Written by its own converter, whose output the schema exporter cannot know.</summary>
    [JsonConverter(typeof(MoneyConverter))]
    public readonly record struct Money(decimal Amount, string Currency);

    public sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parts = reader.GetString()!.Split(' ');
            return new Money(decimal.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1]);
        }

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
        {
            writer.WriteStringValue($"{value.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {value.Currency}");
        }
    }

    [Description("A postal address.")]
    public sealed class PostalAddress
    {
        public string? City { get; set; }
    }

    /// <param name="Code">XML text that must lose to the attribute on the parameter.</param>
    public sealed record Coupon([Description("Code as printed on the coupon.")] string Code);

    public sealed class Address
    {
        /// <summary>Street and number.</summary>
        public string? Street { get; set; }
    }

    public sealed class OrderLine
    {
        /// <summary>Catalogue id of the product.</summary>
        public Guid ProductId { get; set; }
    }
}
