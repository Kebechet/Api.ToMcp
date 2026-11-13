using System.Globalization;
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

    [Fact]
    public async Task SendAsync_NamesTheCultureTheFormIsWrittenIn_InAcceptLanguage()
    {
        // MVC reads a form in the culture UseRequestLocalization picks for the tool's request, and the tool writes it in
        // the MCP request's: without the header, an app whose default is de-DE would read 1.5 written in en-US as 15.
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x") { FormBody = "amount=1.5", Headers = [new("X-Tenant", "t1")] };

        var result = await SendUnderCultureAsync(request, invoker, "en-US");

        Assert.NotEqual(true, result.IsError);
        var sent = Assert.Single(invoker.Requests);
        Assert.Equal([new("X-Tenant", "t1"), new("Accept-Language", "en-US")], sent.Headers);
        Assert.Equal("amount=1.5", sent.FormBody);
        Assert.Equal("/api/x", sent.Route);
    }

    [Theory]
    [InlineData("GET", null)]
    [InlineData("POST", "{\"amount\":1.5}")]
    public async Task SendAsync_NamesNoCulture_ForARequestWithoutAForm(string method, string? jsonBody)
    {
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(new HttpMethod(method), "/api/x") { JsonBody = jsonBody, Headers = [new("X-Tenant", "t1")] };

        await SendUnderCultureAsync(request, invoker, "en-US");

        Assert.Same(request, Assert.Single(invoker.Requests));
    }

    [Fact]
    public async Task SendAsync_NamesNoCulture_ForAFormWrittenInTheInvariantCulture()
    {
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x") { FormBody = "amount=1.5" };

        await SendUnderCultureAsync(request, invoker, "");

        Assert.Same(request, Assert.Single(invoker.Requests));
    }

    [Theory]
    [InlineData("/api/x", "de-DE")]
    [InlineData("/api/x", " DE-de ")]
    [InlineData("/api/x?culture=de-DE", null)]
    [InlineData("/api/x?ui-culture=de-de", null)]
    [InlineData("/api/x?culture=de-DE&ui-culture=en-US", null)]
    public async Task SendAsync_SendsTheForm_WhenTheRequestNamesTheCultureTheFormIsWrittenIn(string route, string? language)
    {
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Post, route)
        {
            FormBody = "amount=1%2C5",
            Headers = language is null ? [] : [new("Accept-Language", language)]
        };

        var result = await SendUnderCultureAsync(request, invoker, "de-DE");

        Assert.NotEqual(true, result.IsError);
        var sent = Assert.Single(invoker.Requests);
        Assert.Equal([new("Accept-Language", language ?? "de-DE")], sent.Headers);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE, en;q=0.5")]
    [InlineData("de")]
    public async Task SendAsync_RefusesAnAcceptLanguageArgumentNamingAnotherCulture_WithAnErrorResult(string language)
    {
        // The app would read the form in the culture the AI named, if it supports that one, or in its default.
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x")
        {
            FormBody = "amount=1%2C25",
            Headers = [new("accept-language", language)],
            HeaderArguments = new Dictionary<string, string> { ["Accept-Language"] = "language" }
        };

        var result = await SendUnderCultureAsync(request, invoker, "de-DE");

        Assert.True(result.IsError);
        Assert.Equal(
            "The argument 'language' is sent as the request header 'Accept-Language', which picks the culture the app reads the tool's form in, " +
            "and the tool writes the form's numbers and dates in the culture of the MCP request, 'de-DE'. Call the tool without 'language', or with 'de-DE'.",
            ((TextContentBlock)Assert.Single(result.Content)).Text);
        Assert.Empty(invoker.Requests);
    }

    [Fact]
    public async Task SendAsync_RefusesTwoAcceptLanguageValues_EvenOfTheFormsCulture()
    {
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x")
        {
            FormBody = "amount=1%2C25",
            Headers = [new("Accept-Language", "de-DE"), new("Accept-Language", "en-US")]
        };

        var result = await SendUnderCultureAsync(request, invoker, "de-DE");

        Assert.True(result.IsError);
        Assert.StartsWith("The tool's request carries the header 'Accept-Language', which picks the culture", ((TextContentBlock)result.Content[0]).Text);
        Assert.Empty(invoker.Requests);
    }

    [Theory]
    [InlineData("/api/x?culture=en-US", "culture")]
    [InlineData("/api/x?Culture=en-US", "culture")]
    [InlineData("/api/x?ui-culture=en-US", "ui-culture")]
    [InlineData("/api/x?culture=en-US&ui-culture=de-DE", "culture")]
    [InlineData("/api/x?culture=de-DE&culture=en-US", "culture")]
    public async Task SendAsync_RefusesACultureQueryValueNamingAnotherCulture_WithAnErrorResult(string route, string key)
    {
        // UseRequestLocalization picks a culture from the query before Accept-Language, and from ui-culture when the
        // query has no culture.
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Post, route) { FormBody = "amount=1%2C25" };

        var result = await SendUnderCultureAsync(request, invoker, "de-DE");

        Assert.True(result.IsError);
        Assert.Equal(
            $"The query value '{key}' of the tool's request picks the culture the app reads the tool's form in, and the tool writes the form's " +
            $"numbers and dates in the culture of the MCP request, 'de-DE'. Call the tool without the argument that fills '{key}', or with 'de-DE'.",
            ((TextContentBlock)Assert.Single(result.Content)).Text);
        Assert.Empty(invoker.Requests);
    }

    [Fact]
    public async Task SendAsync_RefusesAnyCulture_ForAFormWrittenInTheInvariantCulture()
    {
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x?culture=en-US") { FormBody = "amount=1.5" };

        var result = await SendUnderCultureAsync(request, invoker, "");

        Assert.True(result.IsError);
        Assert.EndsWith("in the culture of the MCP request, the invariant culture. Call the tool without the argument that fills 'culture'.", ((TextContentBlock)result.Content[0]).Text);
        Assert.Empty(invoker.Requests);
    }

    [Fact]
    public async Task SendAsync_SendsAnAcceptLanguageArgument_AsItIs_WhenTheRequestHasNoForm()
    {
        var invoker = new RecordingRequestInvoker();
        var request = new McpHttpRequest(HttpMethod.Get, "/api/x?culture=fr-FR") { Headers = [new("Accept-Language", "en-US")] };

        var result = await SendUnderCultureAsync(request, invoker, "de-DE");

        Assert.NotEqual(true, result.IsError);
        Assert.Same(request, Assert.Single(invoker.Requests));
    }

    private static async Task<CallToolResult> SendUnderCultureAsync(McpHttpRequest request, IMcpHttpInvoker invoker, string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            return await request.SendAsync(invoker);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private sealed class RecordingRequestInvoker : IMcpHttpRequestInvoker
    {
        public List<McpHttpRequest> Requests { get; } = [];

        public Task<CallToolResult> SendAsync(McpHttpRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new CallToolResult());
        }

        public Task<CallToolResult> GetAsync(string route, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default) => Task.CompletedTask;
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
