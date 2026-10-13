using System.Text.Json;
using Api.ToMcp.Runtime.Services;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpStructuredOutputTests
{
    [Fact]
    public void FromResult_ReturnsAnErrorResultUnchanged()
    {
        var result = CreateResult("HTTP 404: Not Found - {\"title\":\"Not Found\"}", isError: true);

        Assert.Same(result, McpStructuredOutput.FromResult(result));
    }

    [Fact]
    public void FromResult_ReturnsTheParsedObject_ForAJsonObjectBody()
    {
        var value = Assert.IsType<JsonElement>(McpStructuredOutput.FromResult(CreateResult("{\"name\":\"Laptop\"}")));

        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal("Laptop", value.GetProperty("name").GetString());
    }

    [Fact]
    public void FromResult_ReturnsTheParsedArray_ForAJsonArrayBody()
    {
        var value = Assert.IsType<JsonElement>(McpStructuredOutput.FromResult(CreateResult("[1,2]")));

        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal(2, value.GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void FromResult_ReturnsJsonNull_ForAnEmptySuccessBody(string body)
    {
        var value = Assert.IsType<JsonElement>(McpStructuredOutput.FromResult(CreateResult(body)));

        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    [Fact]
    public void FromResult_ReturnsJsonNull_ForASuccessWithoutContent()
    {
        var value = Assert.IsType<JsonElement>(McpStructuredOutput.FromResult(new CallToolResult()));

        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    [Fact]
    public void FromResult_ReturnsTheResultUnchanged_ForABodyThatIsNotJson()
    {
        var result = CreateResult("hello");

        Assert.Same(result, McpStructuredOutput.FromResult(result));
    }

    private static CallToolResult CreateResult(string text, bool isError = false)
    {
        return new CallToolResult
        {
            IsError = isError,
            Content = { new TextContentBlock { Text = text } }
        };
    }
}
