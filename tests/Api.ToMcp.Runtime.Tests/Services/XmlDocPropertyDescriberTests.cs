using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
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
        var node = InputSchema;
        foreach (var segment in path)
        {
            node = node.GetProperty(segment);
        }

        return node.TryGetProperty("description", out var description)
            ? description.GetString()
            : null;
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
