using System.ComponentModel;
using System.Globalization;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
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
    public void AddQueryValue_IndexesOnlyTheItemsThatSendSomething_SoAnEmptyItemLeavesNoGap()
    {
        // An object with only null properties, or an empty nested collection, adds no key; numbering it anyway
        // would start the next item at [1], and the binder would never reach it.
        var queryParts = new List<string>();
        var labels = new List<Label> { new() { Text = null }, new() { Text = "legs" } };
        var groups = new List<List<int>> { new(), new() { 3 } };

        McpRequestValueFormatter.AddQueryValue(queryParts, "labels", labels);
        McpRequestValueFormatter.AddQueryValue(queryParts, "groups", groups);

        Assert.Equal(new[] { "labels%5B0%5D.Text=legs", "groups%5B0%5D=3" }, queryParts);
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

    public static TheoryData<object, string> IdsReadThroughATryParseTheyDoNotDeclarePublicly => new()
    {
        { new TicketId(5), "id=5" },
        { new SeatId(7), "id=7" },
        { new GateId(3), "id=3" },
    };

    [Theory]
    [MemberData(nameof(IdsReadThroughATryParseTheyDoNotDeclarePublicly))]
    public void AddQueryValue_SendsATypeMvcReadsThroughATryParseItDoesNotDeclarePublicly_AsOneValue(object id, string expectedQueryPart)
    {
        // MVC also finds an explicit IParsable<T>.TryParse, one a base class declares, and one with a body that a single
        // interface offers; expanded into id.Value=5 instead, such an id binds nothing.
        Assert.False(new EmptyModelMetadataProvider().GetMetadataForType(id.GetType()).IsComplexType);
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "id", id);

        Assert.Equal(new[] { expectedQueryPart }, queryParts);
    }

    [Fact]
    public void AddQueryValue_ExpandsATypeWhoseTryParseTwoInterfacesOffer_AsMvcTakesNeither()
    {
        Assert.True(new EmptyModelMetadataProvider().GetMetadataForType(typeof(DockId)).IsComplexType);
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "id", new DockId(4));

        Assert.Equal(new[] { "id.Value=4" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_SendsAByteArray_AsOneBase64Value()
    {
        // MVC's ByteArrayModelBinder reads one Base64 value; one key per byte would not bind.
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "data", new byte[] { 1, 2, 255 });

        Assert.Equal(new[] { "data=AQL%2F" }, queryParts);
        Assert.Equal("AQL/", McpRequestValueFormatter.Format(new byte[] { 1, 2, 255 }));
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
    public void AddQueryValue_LeavesOutAPropertyWhoseGetterIsNotPublic()
    {
        // MVC binds only the properties with a public getter, as the generator sends them; a value under the prefix that
        // it never binds would still make it bind the parameter under the prefix.
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", new PrivateGetterFilter { City = "Brno", Secret = "s1", Code = "c1" });

        Assert.Equal(new[] { "filter.City=Brno" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_KeysAPropertyARecordsConstructorSets_ByTheNameItsConstructorParameterBindsUnder()
    {
        // MVC binds such a property through the constructor parameter, with the parameter's binding attributes, and
        // ignores the property's: ?filter.c= reaches City of the first record and ?filter.City= that of the second.
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", new ParameterNamedRecord("Brno", 3));
        McpRequestValueFormatter.AddQueryValue(queryParts, "other", new PropertyNamedRecord("Praha", 4));

        Assert.Equal(new[] { "filter.c=Brno", "filter.Page=3", "other.City=Praha", "other.Page=4" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_KeysAPropertyOfARecordMvcDoesNotBindThroughItsConstructor_ByThePropertysAttribute()
    {
        // With two public constructors MVC binds the record's properties, as it would a class's.
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "filter", new TwoConstructorRecord("Brno", 3));

        Assert.Equal(new[] { "filter.c=Brno", "filter.Page=3" }, queryParts);
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
    public void AddQueryValue_SendsAnAcyclicObject_NestedAsDeepAsMvcBinds()
    {
        // MVC binds models up to MvcOptions.MaxModelBindingRecursionDepth (32 by default); a graph that deep is not a cycle.
        var queryParts = new List<string>();
        var root = new Node { Level = 0 };
        var deepest = root;
        foreach (var level in Enumerable.Range(1, 12))
        {
            deepest.Children = [new Node { Level = level }];
            deepest = deepest.Children.Single();
        }

        McpRequestValueFormatter.AddQueryValue(queryParts, "tree", root);

        Assert.Equal(13, queryParts.Count);
        Assert.Equal($"tree{string.Concat(Enumerable.Repeat(".Children%5B0%5D", 12))}.Level=12", queryParts.Last());
    }

    [Fact]
    public void AddQueryValue_Throws_ForACollectionThatContainsItself()
    {
        var items = new List<object>();
        items.Add(items);

        Assert.Throws<InvalidOperationException>(() => McpRequestValueFormatter.AddQueryValue([], "items", items));
    }

    [Fact]
    public void AddQueryValue_LeavesOutAPropertyMvcBindsFromAHeaderAFormFieldAServiceOrTheBody()
    {
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "f", new SourcedFilter { City = "Brno", Tenant = "t1", Comment = "c1", Clock = TimeProvider.System, Payload = "p1", Internal = "i1", Required = "r1" });
        McpRequestValueFormatter.AddQueryValue(queryParts, "n", new NeverBoundFilter { City = "Praha" });
        McpRequestValueFormatter.AddQueryValue(queryParts, "k", new KeyedServiceRecord("Ostrava", TimeProvider.System));

        Assert.Equal(new[] { "f.City=Brno", "f.Required=r1", "k.City=Ostrava" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_LeavesOutAPropertyMvcBindsByItsType_WithoutReadingIt()
    {
        // Reading a CancellationToken's WaitHandle creates an event nothing disposes, once per call.
        using var cancellation = new CancellationTokenSource();
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "f", new TypeBoundFilter
        {
            City = "Brno",
            Token = cancellation.Token,
            File = new UnreadableFormFile(),
            Files = [new UnreadableFormFile()],
            Collection = new FormFileCollection { new UnreadableFormFile() },
            Form = new FormCollection(new Dictionary<string, StringValues> { ["a"] = "b" })
        });

        Assert.Equal(new[] { "f.City=Brno" }, queryParts);
    }

    [Fact]
    public void AddQueryValue_SendsNothing_ForAValueMvcBindsByItsType()
    {
        using var cancellation = new CancellationTokenSource();
        var queryParts = new List<string>();

        McpRequestValueFormatter.AddQueryValue(queryParts, "token", cancellation.Token);
        McpRequestValueFormatter.AddQueryValue(queryParts, "file", new UnreadableFormFile());
        McpRequestValueFormatter.AddQueryValue(queryParts, "f", new LooseFilter { City = "Brno", Anything = cancellation.Token });

        Assert.Equal(new[] { "f.City=Brno" }, queryParts);
    }

    private sealed class SourcedFilter
    {
        public string? City { get; set; }

        [FromHeader]
        public string? Tenant { get; set; }

        [FromForm]
        public string? Comment { get; set; }

        [FromServices]
        public TimeProvider? Clock { get; set; }

        [FromBody]
        public string? Payload { get; set; }

        [BindNever]
        public string? Internal { get; set; }

        [BindRequired]
        public string? Required { get; set; }
    }

    [BindNever]
    private sealed class NeverBoundFilter
    {
        public string? City { get; set; }
    }

    private sealed record KeyedServiceRecord(string? City, [FromKeyedServices("clock")] TimeProvider? Clock);

    private sealed class TypeBoundFilter
    {
        public string? City { get; set; }

        public CancellationToken Token { get; set; }

        public IFormFile? File { get; set; }

        public List<IFormFile> Files { get; set; } = [];

        public IFormFileCollection? Collection { get; set; }

        public IFormCollection? Form { get; set; }
    }

    private sealed class LooseFilter
    {
        public string? City { get; set; }

        public object? Anything { get; set; }
    }

    /// <summary>
    /// Throws on every member, so a test fails if the formatter reads into it.
    /// </summary>
    private sealed class UnreadableFormFile : IFormFile
    {
        public string ContentType => throw new InvalidOperationException();
        public string ContentDisposition => throw new InvalidOperationException();
        public IHeaderDictionary Headers => throw new InvalidOperationException();
        public long Length => throw new InvalidOperationException();
        public string Name => throw new InvalidOperationException();
        public string FileName => throw new InvalidOperationException();
        public void CopyTo(Stream target) => throw new InvalidOperationException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Stream OpenReadStream() => throw new InvalidOperationException();
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

    private sealed class Node
    {
        public int Level { get; set; }
        public List<Node> Children { get; set; } = [];
    }

    private sealed class Label
    {
        public string? Text { get; set; }
    }

    private sealed class PrivateGetterFilter
    {
        public string? City { get; set; }

        public string? Secret { private get; set; }

        public string? Code { internal get; set; }
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

    private readonly record struct TicketId(int Value) : IParsable<TicketId>
    {
        static TicketId IParsable<TicketId>.Parse(string s, IFormatProvider? provider)
        {
            return new TicketId(int.Parse(s, provider));
        }

        static bool IParsable<TicketId>.TryParse(string? s, IFormatProvider? provider, out TicketId result)
        {
            var isParsed = int.TryParse(s, NumberStyles.Integer, provider, out var value);
            result = new TicketId(value);
            return isParsed;
        }

        public override string ToString()
        {
            return Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private abstract record IntId<TSelf>(int Value) where TSelf : IntId<TSelf>
    {
        public static bool TryParse(string? text, out TSelf? id)
        {
            id = null;
            return false;
        }

        public sealed override string ToString()
        {
            return Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed record SeatId(int Value) : IntId<SeatId>(Value);

    private interface INumberedId<TSelf> where TSelf : INumberedId<TSelf>
    {
        static virtual bool TryParse(string? text, out TSelf? id)
        {
            id = default;
            return false;
        }
    }

    private interface ICodedId<TSelf> where TSelf : ICodedId<TSelf>
    {
        static virtual bool TryParse(string? text, out TSelf? id)
        {
            id = default;
            return false;
        }
    }

    private readonly record struct GateId(int Value) : INumberedId<GateId>
    {
        public override string ToString()
        {
            return Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private readonly record struct DockId(int Value) : INumberedId<DockId>, ICodedId<DockId>;

    private sealed record ParameterNamedRecord([FromQuery(Name = "c")] string? City, int Page);

    private sealed record PropertyNamedRecord([property: FromQuery(Name = "c")] string? City, int Page);

    private sealed record TwoConstructorRecord([property: FromQuery(Name = "c")] string? City, int Page)
    {
        public TwoConstructorRecord()
            : this(null, 0)
        {
        }
    }
}
