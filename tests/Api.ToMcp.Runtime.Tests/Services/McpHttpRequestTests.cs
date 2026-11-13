using Api.ToMcp.Abstractions.Scopes;
using ModelContextProtocol.Protocol;
using Moq;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

/// <summary>
/// A tool's request with headers or a form, sent through an invoker that may implement only <see cref="IMcpHttpInvoker"/>,
/// as a hand-written one or a test stub does. See https://github.com/Kebechet/Api.ToMcp/issues/49.
/// </summary>
public class McpHttpRequestTests
{
    [Fact]
    public async Task SendAsync_SendsTheRequestThroughTheInterfaceThatSendsHeaders_WhenTheInvokerImplementsIt()
    {
        var invoker = new Mock<IMcpHttpRequestInvoker>();
        var expected = new CallToolResult();
        var request = new McpHttpRequest(HttpMethod.Get, "/api/x") { Headers = [new("X-Tenant", "t1")] };
        invoker.Setup(x => x.SendAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await request.SendAsync(invoker.Object);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task SendAsync_CallsTheInvokersMethod_WhenTheRequestHasNoHeadersAndNoForm()
    {
        var invoker = new Mock<IMcpHttpInvoker>(MockBehavior.Strict);
        var expected = new CallToolResult();
        invoker.Setup(x => x.PostAsync("/api/x", "{}", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await new McpHttpRequest(HttpMethod.Post, "/api/x") { JsonBody = "{}" }.SendAsync(invoker.Object);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task SendAsync_ReturnsAnErrorNamingTheInterface_WhenTheInvokerCannotSendTheHeaders()
    {
        var invoker = new StubInvoker();

        var result = await new McpHttpRequest(HttpMethod.Post, "/api/x") { FormBody = "a=1" }.SendAsync(invoker);

        Assert.True(result.IsError);
        Assert.Equal(
            $"The tool sends a POST request with headers or a body that the registered IMcpHttpInvoker, {typeof(StubInvoker).FullName}, cannot send: implement IMcpHttpRequestInvoker as well.",
            ((TextContentBlock)result.Content[0]).Text);
        Assert.Equal(0, invoker.CallCount);
    }

    private sealed class StubInvoker : IMcpHttpInvoker
    {
        public int CallCount { get; private set; }

        public Task<CallToolResult> GetAsync(string route, CancellationToken ct = default) => Called();
        public Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default) => Called();
        public Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default) => Called();
        public Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default) => Called();
        public Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default) => Called();
        public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default) => Task.CompletedTask;

        private Task<CallToolResult> Called()
        {
            CallCount++;
            return Task.FromResult(new CallToolResult());
        }
    }
}
