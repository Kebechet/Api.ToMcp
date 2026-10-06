using System.Globalization;
using Api.ToMcp.Runtime.Services;
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
        public List<Stage> Stages { get; set; } = [];
    }

    private sealed class Stage
    {
        public Guid PlanId { get; set; }
        public int? DayId { get; set; }
    }
}
