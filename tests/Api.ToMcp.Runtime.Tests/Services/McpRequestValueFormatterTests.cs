using System.ComponentModel;
using System.Globalization;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpRequestValueFormatterTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("cs-CZ")]
    [InlineData("de-DE")]
    public void Format_UsesInvariantIsoValues_RegardlessOfCulture(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultureName);
        try
        {
            Assert.Equal("2026-09-28", McpRequestValueFormatter.Format(new DateOnly(2026, 9, 28)));
            Assert.Equal("2026-09-28T14:30:00.0000000", McpRequestValueFormatter.Format(new DateTime(2026, 9, 28, 14, 30, 0)));
            Assert.Equal("2026-09-28T14:30:00.0000000+02:00", McpRequestValueFormatter.Format(new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeSpan.FromHours(2))));
            Assert.Equal("14:30:00.0000000", McpRequestValueFormatter.Format(new TimeOnly(14, 30)));
            Assert.Equal("1.5", McpRequestValueFormatter.Format(1.5m));
            Assert.Equal("2.25", McpRequestValueFormatter.Format(2.25d));
            Assert.Equal("1234567", McpRequestValueFormatter.Format(1234567));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Format_WritesBooleansAndEnumsAsTheBinderReadsThem()
    {
        Assert.Equal("true", McpRequestValueFormatter.Format(true));
        Assert.Equal("false", McpRequestValueFormatter.Format(false));
        Assert.Equal("Wednesday", McpRequestValueFormatter.Format(DayOfWeek.Wednesday));
    }

    [Fact]
    public void Format_ReturnsEmptyString_ForNull()
    {
        Assert.Equal(string.Empty, McpRequestValueFormatter.Format(null));
    }

    [Fact]
    public void AddQueryValue_SkipsNull()
    {
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "category", null);

        Assert.Empty(queryParts);
    }

    [Fact]
    public void AddQueryValue_EscapesTheFormattedValue()
    {
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "search", "a&b c");

        Assert.Equal(new[] { "search=a%26b%20c" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_RepeatsTheKey_ForEachCollectionItem()
    {
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "ids", new List<int> { 1, 2 });

        Assert.Equal(new[] { "ids=1", "ids=2" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_AddsEachPropertyOfAnObject_UnderTheParameterName_AndSkipsNulls()
    {
        var queryParts = new List<string>();
        var filter = new SearchFilter
        {
            City = "Brno",
            From = new DateOnly(2026, 9, 28),
            Day = DayOfWeek.Monday,
            Statuses = ["Open", "Closed"]
        };

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", filter);

        Assert.Equal(
            new[] { "filter.City=Brno", "filter.From=2026-09-28", "filter.Day=Monday", "filter.Statuses=Open", "filter.Statuses=Closed" },
            queryParts);
    }

    [Fact]
    public void AddQueryValue_PrefixesNestedObjectProperties()
    {
        var queryParts = new List<string>();
        var filter = new StageFilter
        {
            CurrentDate = new DateOnly(2026, 9, 28),
            Stage = new Stage { PlanId = Guid.Parse("0189dfd3-5bd1-a760-bed1-d1d5751e4e8c"), DayId = 3 }
        };

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", filter);

        Assert.Equal(
            new[] { "filter.CurrentDate=2026-09-28", "filter.Stage.PlanId=0189dfd3-5bd1-a760-bed1-d1d5751e4e8c", "filter.Stage.DayId=3" },
            queryParts);
    }

    [Fact]
    public void AddQueryValue_IndexesCollectionsOfObjects()
    {
        var queryParts = new List<string>();
        var filter = new StagesFilter
        {
            Stages = [new Stage { PlanId = Guid.Empty, DayId = 1 }, new Stage { PlanId = Guid.Empty, DayId = 2 }]
        };

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", filter);

        Assert.Equal(
            new[]
            {
                "filter.Stages%5B0%5D.PlanId=00000000-0000-0000-0000-000000000000",
                "filter.Stages%5B0%5D.DayId=1",
                "filter.Stages%5B1%5D.PlanId=00000000-0000-0000-0000-000000000000",
                "filter.Stages%5B1%5D.DayId=2"
            },
            queryParts);
    }

    [Fact]
    public void AddQueryValue_IndexesOnlyTheObjectsItSends_SoANullItemLeavesNoGap()
    {
        // The binder reads Stages[0], Stages[1], ... and stops at the first missing index.
        var queryParts = new List<string>();
        var filter = new StagesFilter
        {
            Stages = [null, new Stage { PlanId = Guid.Empty, DayId = 1 }, null, new Stage { PlanId = Guid.Empty, DayId = 2 }]
        };

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", filter);

        Assert.Equal(
            new[]
            {
                "filter.Stages%5B0%5D.PlanId=00000000-0000-0000-0000-000000000000",
                "filter.Stages%5B0%5D.DayId=1",
                "filter.Stages%5B1%5D.PlanId=00000000-0000-0000-0000-000000000000",
                "filter.Stages%5B1%5D.DayId=2"
            },
            queryParts);
    }

    [Fact]
    public void AddQueryValue_SendsATypeWithAStringConverter_AsOneValueWrittenByTheConverter()
    {
        // The binder treats such a type as simple and reads it back through the converter; the record's own
        // ToString() ("OrderId { Value = ... }") would not parse.
        var queryParts = new List<string>();
        var orderId = new OrderId(Guid.Parse("0189dfd3-5bd1-a760-bed1-d1d5751e4e8c"));

        McpRequestValueFormatter.AddQueryValue(queryParts, "orderId", orderId);
        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", new OrderFilter { Order = orderId });

        Assert.Equal(
            new[] { "orderId=0189dfd3-5bd1-a760-bed1-d1d5751e4e8c", "filter.Order=0189dfd3-5bd1-a760-bed1-d1d5751e4e8c" },
            queryParts);
    }

    [Fact]
    public void AddQueryValue_SendsATypeWithTryParse_AsOneValue()
    {
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "sku", new Sku("AB-12"));

        Assert.Equal(new[] { "sku=AB-12" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_SendsNumbersThatAreNotPrimitives()
    {
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "signed", Int128.MaxValue);
        McpRequestValueFormatter.AddQueryValue(queryParts, "unsigned", UInt128.MaxValue);
        McpRequestValueFormatter.AddQueryValue(queryParts, "half", (Half)1.5);

        Assert.Equal(
            new[]
            {
                "signed=170141183460469231731687303715884105727",
                "unsigned=340282366920938463463374607431768211455",
                "half=1.5"
            },
            queryParts);
    }

    [Fact]
    public void AddQueryValue_KeysAnAliasedProperty_ByTheNameItsBindingAttributeGives()
    {
        // The binder reads such a property under its BinderModelName; under the CLR name it would stay at its default.
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", new AliasedFilter { Query = "squat", Page = 2 });

        Assert.Equal(new[] { "filter.q=squat", "filter.Page=2" }, queryParts);
    }

    [Fact]
    public void Format_WritesATypeThroughItsOwnConverter_BeforeItsIFormattable()
    {
        // The binder reads the value back through the converter, so the converter's text is the one that round-trips.
        var wireId = new WireId(5);

        Assert.Equal("5", McpRequestValueFormatter.Format(wireId));
    }

    [Fact]
    public void AddQueryValue_IndexesNestedCollections_AndRepeatsTheKeyForTheirValues()
    {
        var queryParts = new List<string>();
        var groups = new List<List<int>> { new() { 1, 2 }, new() { 3 } };

        McpRequestValueFormatter.AddQueryValue(queryParts, "groups", groups);

        Assert.Equal(new[] { "groups%5B0%5D=1", "groups%5B0%5D=2", "groups%5B1%5D=3" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_Throws_ForACollectionThatContainsItself()
    {
        var items = new List<object>();
        items.Add(items);

        Assert.Throws<InvalidOperationException>(() => McpRequestValueFormatter.AddQueryValue([], "items", items));
    }

    private sealed class SearchFilter
    {
        public string? City { get; set; }
        public string? Country { get; set; }
        public DateOnly? From { get; set; }
        public DayOfWeek Day { get; set; }
        public List<string> Statuses { get; set; } = [];
    }

    private sealed class StageFilter
    {
        public DateOnly CurrentDate { get; set; }
        public Stage? Stage { get; set; }
    }

    private sealed class StagesFilter
    {
        public List<Stage?> Stages { get; set; } = [];
    }

    private sealed class Stage
    {
        public Guid PlanId { get; set; }
        public int? DayId { get; set; }
    }

    private sealed class OrderFilter
    {
        public OrderId? Order { get; set; }
    }

    [TypeConverter(typeof(OrderIdConverter))]
    private readonly record struct OrderId(Guid Value);

    private sealed class OrderIdConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            return value is string text
                ? new OrderId(Guid.Parse(text))
                : base.ConvertFrom(context, culture, value);
        }

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            return value is OrderId orderId && destinationType == typeof(string)
                ? orderId.Value.ToString()
                : base.ConvertTo(context, culture, value, destinationType);
        }
    }

    private sealed class AliasedFilter
    {
        [FromQuery(Name = "q")]
        public string? Query { get; set; }

        [FromQuery]
        public int Page { get; set; }
    }

    [TypeConverter(typeof(WireIdConverter))]
    private readonly record struct WireId(int Value) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            return $"WireId-{Value}";
        }
    }

    private sealed class WireIdConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            return value is WireId wireId && destinationType == typeof(string)
                ? wireId.Value.ToString(CultureInfo.InvariantCulture)
                : base.ConvertTo(context, culture, value, destinationType);
        }
    }

    private readonly record struct Sku(string Code)
    {
        public static bool TryParse(string? text, IFormatProvider? provider, out Sku sku)
        {
            sku = new Sku(text ?? string.Empty);
            return text is not null;
        }

        public override string ToString()
        {
            return Code;
        }
    }
}
