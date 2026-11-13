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
    public void AddFormValue_WritesAValue_AsAQueryValueIsWritten()
    {
        var formParts = new List<string>();

        McpRequestValueFormatter.AddFormValue(formParts, "name", "a b&c=d+e");
        McpRequestValueFormatter.AddFormValue(formParts, "day", new DateOnly(2026, 9, 28));
        McpRequestValueFormatter.AddFormValue(formParts, "ids", new[] { 1, 2 });
        McpRequestValueFormatter.AddFormValue(formParts, "none", null);

        Assert.Equal(new[] { "name=a%20b%26c%3Dd%2Be", "day=2026-09-28", "ids=1", "ids=2" }, formParts);
    }

    [Fact]
    public void AddFormValue_SendsThePropertiesMvcReadsFromTheForm_UnderTheParametersPrefix()
    {
        var formParts = new List<string>();

        McpRequestValueFormatter.AddFormValue(formParts, "review", new FormReview
        {
            Rating = 5,
            Comment = "Quiet",
            Tags = ["a", "b"],
            Author = "Ann",
            Page = 2,
            Tenant = "t1",
            Attachment = new FormFile(Stream.Null, 0, 0, "file", "file.txt")
        });

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

        Assert.False(isAdded);
        Assert.Empty(headers);
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
