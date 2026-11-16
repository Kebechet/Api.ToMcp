using Api.ToMcp.Runtime.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpToolCallNameTests
{
    [Fact]
    public async Task Create_NamesTheCalledTool_InsideTheCall()
    {
        await using var server = CreateServer();
        string? nameInside = null;
        var handler = McpToolCallName.Create((_, _) =>
        {
            nameInside = McpToolCallName.Current;
            return ValueTask.FromResult(new CallToolResult());
        });

        await handler(CreateContext(server, "products_get"), CancellationToken.None);

        Assert.Equal("products_get", nameInside);
    }

    [Fact]
    public async Task Create_EndsTheName_WithTheCall()
    {
        await using var server = CreateServer();
        var handler = McpToolCallName.Create((_, _) => ValueTask.FromResult(new CallToolResult()));

        await handler(CreateContext(server, "products_get"), CancellationToken.None);

        Assert.Null(McpToolCallName.Current);
    }

    [Fact]
    public async Task Create_NamesTheTool_ToWorkTheCallStarted_OnlyWhileTheCallRuns()
    {
        await using var server = CreateServer();
        var hasCallEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?>? nameAfterTheCall = null;
        var handler = McpToolCallName.Create(async (_, _) =>
        {
            var nameDuringTheCall = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            nameAfterTheCall = Task.Run(async () =>
            {
                nameDuringTheCall.SetResult(McpToolCallName.Current);
                await hasCallEnded.Task;
                return McpToolCallName.Current;
            });

            return new CallToolResult { Content = [new TextContentBlock { Text = (await nameDuringTheCall.Task)! }] };
        });

        var result = await handler(CreateContext(server, "products_get"), CancellationToken.None);
        hasCallEnded.SetResult();

        Assert.Equal("products_get", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Null(await nameAfterTheCall!);
    }

    [Fact]
    public async Task Create_EndsTheName_ForWorkTheCallStarted_WhenTheCallThrows()
    {
        await using var server = CreateServer();
        var hasCallEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?>? nameAfterTheCall = null;
        var handler = McpToolCallName.Create((_, _) =>
        {
            nameAfterTheCall = Task.Run(async () =>
            {
                await hasCallEnded.Task;
                return McpToolCallName.Current;
            });

            throw new InvalidOperationException("The tool failed.");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler(CreateContext(server, "products_get"), CancellationToken.None));
        hasCallEnded.SetResult();

        Assert.Null(await nameAfterTheCall!);
    }

    [Fact]
    public async Task Create_NamesEachCallsOwnTool_WhileCallsOfOtherToolsRun()
    {
        // The first call reads its name only once the second has set its own.
        await using var server = CreateServer();
        var isSecondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = McpToolCallName.Create(async (context, _) =>
        {
            if (context.Params!.Name == "products_get")
            {
                await isSecondInside.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                isSecondInside.SetResult();
            }

            return new CallToolResult { Content = [new TextContentBlock { Text = McpToolCallName.Current! }] };
        });

        var first = handler(CreateContext(server, "products_get"), CancellationToken.None).AsTask();
        var second = handler(CreateContext(server, "orders_create"), CancellationToken.None).AsTask();
        var results = await Task.WhenAll(first, second);

        Assert.Equal("products_get", Assert.IsType<TextContentBlock>(Assert.Single(results[0].Content)).Text);
        Assert.Equal("orders_create", Assert.IsType<TextContentBlock>(Assert.Single(results[1].Content)).Text);
    }

    [Fact]
    public void Current_IsNull_OutsideAToolCall()
    {
        Assert.Null(McpToolCallName.Current);
    }

    private static McpServer CreateServer()
    {
        return McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
    }

    private static RequestContext<CallToolRequestParams> CreateContext(McpServer server, string toolName)
    {
        return new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = toolName });
    }
}
