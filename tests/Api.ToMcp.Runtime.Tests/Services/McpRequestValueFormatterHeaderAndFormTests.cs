using System.ComponentModel;
using System.Globalization;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

/// <summary>
/// The header values and form fields a tool sends for <c>[FromHeader]</c> and <c>[FromForm]</c> parameters. See
/// https://github.com/Kebechet/Api.ToMcp/issues/49.
/// </summary>
public class McpRequestValueFormatterHeaderAndFormTests
{
    private enum Status
    {
        Open,
        Closed
    }

    [Fact]
    public void TryAddFormValue_WritesAValue_AsAQueryValueIsWritten()
    {
        var formParts = new List<string>();

        Assert.True(TryAddFormValue(formParts, "name", "a b&c=d+e"));
        Assert.True(TryAddFormValue(formParts, "at", new DateTime(2026, 9, 28, 14, 30, 0)));
        Assert.True(TryAddFormValue(formParts, "ids", new[] { 1, 2 }));
        Assert.True(TryAddFormValue(formParts, "none", null));

        Assert.Equal(new[] { "name=a%20b%26c%3Dd%2Be", "at=2026-09-28T14%3A30%3A00.0000000", "ids=1", "ids=2" }, formParts);
    }

    [Fact]
    public void TryAddFormValue_SendsThePropertiesMvcReadsFromTheForm_UnderTheParametersPrefix()
    {
        var formParts = new List<string>();

        var isAdded = TryAddFormValue(formParts, "review", new FormReview
        {
            Rating = 5,
            Comment = "Quiet",
            Tags = ["a", "b"],
            Author = "Ann",
            Page = 2,
            Tenant = "t1",
            Attachment = new FormFile(Stream.Null, 0, 0, "file", "file.txt")
        });

        Assert.True(isAdded);
        Assert.Equal(new[] { "review.Rating=5", "review.Comment=Quiet", "review.Tags=a", "review.Tags=b", "review.by=Ann" }, formParts);
    }

    [Fact]
    public void TryAddHeaderValue_AddsOneValuePerItem_AndQuotesAnItemWithAComma()
    {
        var headers = new List<KeyValuePair<string, string>>();

        var isAdded = McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Tags", new[] { "a,b", "c", null });

        Assert.True(isAdded);
        Assert.Equal(new KeyValuePair<string, string>[] { new("X-Tags", "\"a,b\""), new("X-Tags", "c") }, headers);
    }

    [Fact]
    public void TryAddHeaderValue_WritesAValueAsTheBinderReadsIt_AndAddsNothingForNull()
    {
        var headers = new List<KeyValuePair<string, string>>();

        McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Day", new DateOnly(2026, 9, 28));
        McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Status", Status.Closed);
        McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Text", "a, b\tc");
        McpRequestValueFormatter.TryAddHeaderValue(headers, "X-None", null);

        Assert.Equal(new KeyValuePair<string, string>[] { new("X-Day", "2026-09-28"), new("X-Status", "Closed"), new("X-Text", "a, b\tc") }, headers);
    }

    [Theory]
    [InlineData("a\r\nX-Injected: 1")]
    [InlineData("a\nb")]
    [InlineData("\0")]
    [InlineData("café")]
    public void TryAddHeaderValue_RefusesAValueAnHttpHeaderCannotCarry(string value)
    {
        var headers = new List<KeyValuePair<string, string>>();

        var isAdded = McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Tenant", new[] { "ok", value });
        var isSingleAdded = McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Tenant", value);

        Assert.False(isAdded);
        Assert.False(isSingleAdded);
        Assert.Empty(headers);
    }

    [Theory]
    [InlineData("\"q\"")]
    [InlineData("x,\"y")]
    [InlineData("a\"b")]
    [InlineData("")]
    [InlineData(" \t ")]
    public void TryAddHeaderValue_RefusesAnArrayItemMvcWouldReadBackOtherwise(string item)
    {
        // MVC splits the header at its commas outside quotes, drops an empty item and removes the quotes around one.
        var headers = new List<KeyValuePair<string, string>>();

        var isAdded = McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Tags", new[] { "ok", item });

        Assert.False(isAdded);
        Assert.Empty(headers);
    }

    [Fact]
    public void TryAddHeaderValue_DropsTheSpacesAndTabsAroundAValue_AsKestrelDoes()
    {
        var headers = new List<KeyValuePair<string, string>>();

        McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Name", "  padded\t ");
        McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Quoted", "\"kept\" as is");
        McpRequestValueFormatter.TryAddHeaderValue(headers, "X-Tags", new[] { " sp ", "a, b" });

        Assert.Equal(
            new KeyValuePair<string, string>[] { new("X-Name", "padded"), new("X-Quoted", "\"kept\" as is"), new("X-Tags", "sp"), new("X-Tags", "\"a, b\"") },
            headers);
    }

    [Theory]
    [InlineData("de-DE", "amount=1234%2C25", "ratio=-0%2C5", "day=28.09.2026", "delta=-12")]
    [InlineData("en-US", "amount=1234.25", "ratio=-0.5", "day=9%2F28%2F2026", "delta=-12")]
    public void TryAddFormValue_WritesNumbersAndDates_InTheCurrentCulture_AsMvcReadsAForm(string cultureName, string amount, string ratio, string day, string delta)
    {
        // MVC reads a form with the request's culture, unlike the query string, so 1234.25 would be 123425 under de-DE.
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultureName);
        try
        {
            var formParts = new List<string>();

            Assert.True(TryAddFormValue(formParts, "amount", 1234.25m));
            Assert.True(TryAddFormValue(formParts, "ratio", -0.5));
            Assert.True(TryAddFormValue(formParts, "day", new DateOnly(2026, 9, 28)));
            Assert.True(TryAddFormValue(formParts, "delta", -12));
            Assert.True(TryAddFormValue(formParts, "id", Guid.Empty));

            Assert.Equal(new[] { amount, ratio, day, delta, "id=00000000-0000-0000-0000-000000000000" }, formParts);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void TryAddFormValue_WritesANumber_ThatTheCurrentCultureParsesBack_InEveryCulture()
    {
        // Arabic cultures write the minus sign with a letter mark that the invariant text lacks and they require.
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
            {
                CultureInfo.CurrentCulture = culture;
                var formParts = new List<string>();

                Assert.True(TryAddFormValue(formParts, "a", -1234.25m));

                var text = Uri.UnescapeDataString(formParts[0]["a=".Length..]);
                Assert.True(
                    decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var value) && value == -1234.25m,
                    $"{culture.Name}: {text}");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("fa-IR", false, "0001-01-01")]
    [InlineData("fa-IR", false, "0622-03-21")]
    [InlineData("ar-SA", true, "1900-04-29")]
    [InlineData("ar-SA", true, "9999-12-31")]
    public void TryAddFormValue_ReturnsFalse_ForADateOutsideTheCalendarOfTheCurrentCulture(string cultureName, bool isUmAlQura, string date)
    {
        // ToString throws for it: the Persian calendar starts in 622, the Umm al-Qura one spans 1900 to 2077. A date
        // property left out is 0001-01-01, in an object or in a collection. ar-SA defaults to Umm al-Qura on Windows but
        // to the Gregorian calendar with ICU on Linux, so the test sets the calendar itself.
        var day = DateOnly.Parse(date, CultureInfo.InvariantCulture);
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(cultureName).Clone();
        if (isUmAlQura)
        {
            culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
        }

        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.False(TryAddFormValue([], "day", day));
            Assert.False(TryAddFormValue([], "form", new DatedForm { Amount = 1.5m, Day = day }));
            Assert.False(TryAddFormValue([], "days", new[] { new DateOnly(2026, 11, 13), day }));
            Assert.False(TryAddFormValue([], "forms", new[] { new DatedForm { Day = new DateOnly(2026, 11, 13) }, new DatedForm { Day = day } }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void TryAddFormValue_WritesTheFirstAndLastDateOfTheCalendar_ThatTheCurrentCultureParsesBack_InEveryCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var converter = TypeDescriptor.GetConverter(typeof(DateOnly));
        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
            {
                CultureInfo.CurrentCulture = culture;
                var calendar = culture.DateTimeFormat.Calendar;
                DateOnly[] days = [DateOnly.FromDateTime(calendar.MinSupportedDateTime), DateOnly.FromDateTime(calendar.MaxSupportedDateTime)];
                var formParts = new List<string>();

                Assert.True(TryAddFormValue(formParts, "days", days), culture.Name);

                var texts = formParts.Select(x => Uri.UnescapeDataString(x["days=".Length..])).ToList();
                Assert.Equal(days, texts.Select(x => (DateOnly)converter.ConvertFrom(null, culture, x)!));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("en-US", 1.5, true)]
    [InlineData("en-US", -12, true)]
    [InlineData("en-US", 12, false)]
    [InlineData("en-US", 2.0, false)]
    [InlineData("de-DE", 1.5, true)]
    [InlineData("ar-SA", 12, false)]
    [InlineData("", 1.5, true)]
    public void TryAddFormValue_TellsTheCulture_WhenItWritesANumberAnotherCultureMayReadOtherwise(string cultureName, double amount, bool isWrittenInTheCulture)
    {
        // Digits alone are the same number in every culture; a decimal separator or a sign is not.
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            CultureInfo? formCulture = null;

            Assert.True(McpRequestValueFormatter.TryAddFormValue([], "name", "Ann", ref formCulture));
            Assert.True(McpRequestValueFormatter.TryAddFormValue([], "amount", amount, ref formCulture));

            Assert.Equal(isWrittenInTheCulture ? CultureInfo.CurrentCulture : null, formCulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void TryAddFormValue_TellsTheCulture_WhenItWritesADateOrANumberInsideAnObjectOrACollection()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            CultureInfo? textOnly = null;
            CultureInfo? date = null;
            CultureInfo? nestedAmount = null;
            CultureInfo? amounts = null;
            CultureInfo? kept = CultureInfo.InvariantCulture;

            Assert.True(McpRequestValueFormatter.TryAddFormValue([], "review", new FormReview { Rating = 5, Comment = "1,5", Tags = ["a"] }, ref textOnly));
            Assert.True(McpRequestValueFormatter.TryAddFormValue([], "day", new DateOnly(2026, 11, 13), ref date));
            Assert.True(McpRequestValueFormatter.TryAddFormValue([], "form", new DatedForm { Amount = 1.5m, Day = new DateOnly(2026, 11, 13) }, ref nestedAmount));
            Assert.True(McpRequestValueFormatter.TryAddFormValue([], "amounts", new[] { 1m, 2.25m }, ref amounts));
            Assert.True(McpRequestValueFormatter.TryAddFormValue([], "ids", new[] { 1, 2 }, ref kept));

            Assert.Null(textOnly);
            Assert.Equal("de-DE", date?.Name);
            Assert.Equal("de-DE", nestedAmount?.Name);
            Assert.Equal("de-DE", amounts?.Name);
            Assert.Same(CultureInfo.InvariantCulture, kept);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void AddQueryValue_WritesNumbers_InTheInvariantCulture_WhateverTheCurrentOne()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var queryParts = new List<string>();

            McpRequestValueFormatter.AddQueryValue(queryParts, "amount", 1234.25m);

            Assert.Equal(new[] { "amount=1234.25" }, queryParts);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static bool TryAddFormValue(List<string> formParts, string name, object? value)
    {
        CultureInfo? formCulture = null;

        return McpRequestValueFormatter.TryAddFormValue(formParts, name, value, ref formCulture);
    }

    private sealed class DatedForm
    {
        public decimal Amount { get; set; }

        public DateOnly Day { get; set; }
    }

    private sealed class FormReview
    {
        public int Rating { get; set; }

        [FromForm]
        public string? Comment { get; set; }

        public List<string> Tags { get; set; } = [];

        [FromForm(Name = "by")]
        public string? Author { get; set; }

        [FromQuery]
        public int Page { get; set; }

        [FromHeader]
        public string? Tenant { get; set; }

        public IFormFile? Attachment { get; set; }
    }
}
