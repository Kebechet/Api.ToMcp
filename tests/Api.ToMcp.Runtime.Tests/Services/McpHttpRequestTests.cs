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
    public async Task SendAsync_CallsTheInvokersMethodForEachHttpMethod_WhenTheRequestHasNoHeadersAndNoForm()
    {
        var invoker = new Mock<IMcpHttpInvoker>(MockBehavior.Strict);
        var get = new CallToolResult();
        var post = new CallToolResult();
        var put = new CallToolResult();
        var patch = new CallToolResult();
        var delete = new CallToolResult();
        invoker.Setup(x => x.GetAsync("/api/x", It.IsAny<CancellationToken>())).ReturnsAsync(get);
        invoker.Setup(x => x.PostAsync("/api/x", "{\"a\":1}", It.IsAny<CancellationToken>())).ReturnsAsync(post);
        invoker.Setup(x => x.PutAsync("/api/x", "{\"a\":2}", It.IsAny<CancellationToken>())).ReturnsAsync(put);
        invoker.Setup(x => x.PatchAsync("/api/x", "{\"a\":3}", It.IsAny<CancellationToken>())).ReturnsAsync(patch);
        invoker.Setup(x => x.DeleteAsync("/api/x", It.IsAny<CancellationToken>())).ReturnsAsync(delete);

        Assert.Same(get, await new McpHttpRequest(HttpMethod.Get, "/api/x").SendAsync(invoker.Object));
        Assert.Same(post, await new McpHttpRequest(HttpMethod.Post, "/api/x") { JsonBody = "{\"a\":1}" }.SendAsync(invoker.Object));
        Assert.Same(put, await new McpHttpRequest(HttpMethod.Put, "/api/x") { JsonBody = "{\"a\":2}" }.SendAsync(invoker.Object));
        Assert.Same(patch, await new McpHttpRequest(HttpMethod.Patch, "/api/x") { JsonBody = "{\"a\":3}" }.SendAsync(invoker.Object));
        Assert.Same(delete, await new McpHttpRequest(HttpMethod.Delete, "/api/x").SendAsync(invoker.Object));
    }

    [Theory]
    [InlineData("GET", null, true)]
    [InlineData("POST", "{}", true)]
    [InlineData("PUT", null, true)]
    [InlineData("DELETE", null, true)]
    [InlineData("GET", "{}", false)]
    [InlineData("HEAD", null, false)]
    public async Task SendAsync_ReturnsAnErrorWithoutCallingTheInvoker_WhenItCannotSendTheRequest(string method, string? jsonBody, bool hasHeaders)
    {
        // An invoker without IMcpHttpRequestInvoker would drop the headers, a GET's body or the method, so the call is refused.
        var invoker = new StubInvoker();
        var request = new McpHttpRequest(new HttpMethod(method), "/api/x")
        {
            JsonBody = jsonBody,
            Headers = hasHeaders ? [new("X-Tenant", "t1")] : []
        };

        var result = await request.SendAsync(invoker);

        Assert.True(result.IsError);
        Assert.Equal(0, invoker.CallCount);
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
