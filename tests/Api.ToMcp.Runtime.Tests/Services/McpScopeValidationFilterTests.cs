using Api.ToMcp.Runtime.Services;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpScopeValidationFilterTests
{
    [Fact]
    public async Task Create_ReturnsTheReason_AsAnErrorResult_WhenScopeValidationRefusesTheCall()
    {
        var handler = McpScopeValidationFilter.Create((_, _) => throw new McpScopeValidationException("Insufficient scope. Required: Write, Granted: Read"));

        var result = await handler(null!, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Equal("Insufficient scope. Required: Write, Granted: Read", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Create_LetsAnyOtherException_Through_ForTheSdkToHideItsMessage()
    {
        // An UnauthorizedAccessException from a file the tool reads names the file's path.
        var handler = McpScopeValidationFilter.Create((_, _) => throw new UnauthorizedAccessException(@"Access to the path 'C:\secrets\key.pem' is denied."));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await handler(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Create_ReturnsTheToolsResult_Unchanged()
    {
        var toolResult = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        var handler = McpScopeValidationFilter.Create((_, _) => ValueTask.FromResult(toolResult));

        Assert.Same(toolResult, await handler(null!, CancellationToken.None));
    }
}
