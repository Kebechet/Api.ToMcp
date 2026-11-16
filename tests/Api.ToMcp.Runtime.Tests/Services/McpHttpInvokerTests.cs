using System.Globalization;
using System.Net;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using Moq.Protected;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

/// <summary>
/// The request the invoker builds and sends through its client, and the result it makes of the response. The client here
/// is a mock that records each request and answers it, in place of the handlers <c>AddMcpTools</c> gives it, which serve
/// the request in process.
/// </summary>
public class McpHttpInvokerTests
{
    private const string BaseUrl = "https://localhost:5000";
    private const string LoopPreventionHeader = "X-MCP-Internal-Call";

    private readonly Mock<ISelfBaseUrlProvider> _baseUrlProviderMock;
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private readonly Mock<ILogger<McpHttpInvoker>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<IOptions<McpScopeOptions>> _scopeOptionsMock;

    public McpHttpInvokerTests()
    {
        _baseUrlProviderMock = new Mock<ISelfBaseUrlProvider>();
        _baseUrlProviderMock.Setup(x => x.GetBaseUrl()).Returns(BaseUrl);

        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _loggerMock = new Mock<ILogger<McpHttpInvoker>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();

        _scopeOptionsMock = new Mock<IOptions<McpScopeOptions>>();
        _scopeOptionsMock.Setup(x => x.Value).Returns(new McpScopeOptions());
    }

    private McpHttpInvoker CreateInvoker(IEnumerable<string>? proxyHeaderNames = null)
    {
        // The mocked handler stands for the chain AddMcpTools builds, whose in-process handler the check finds, so every
        // request goes through the client.
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var pipeline = new InProcessApiPipeline(new DefaultHttpContextFactory(services), NullLogger<InProcessApiPipeline>.Instance, InProcessRequestDiagnostics.Create(services));
        return new McpHttpInvoker(
            new HttpClient(_httpMessageHandlerMock.Object),
            _baseUrlProviderMock.Object,
            _httpContextAccessorMock.Object,
            _loggerMock.Object,
            _scopeOptionsMock.Object,
            pipeline,
            new InProcessApiHandlerCheck(() => true, () => new InProcessApiHandler(pipeline)),
            proxyHeaderNames);
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string content, Action<HttpRequestMessage>? requestCallback = null)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => requestCallback?.Invoke(request))
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
    }

    private void SetupHttpContext(string? authorizationHeader)
    {
        var httpContext = new DefaultHttpContext();
        if (authorizationHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authorizationHeader;
        }
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
    }

    private void SetupHttpContextWithSchemeAndHost(string scheme, string host, string? authorizationHeader = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host);
        if (authorizationHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authorizationHeader;
        }
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
    }

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    #region GetAsync Tests

    [Fact]
    public async Task GetAsync_BuildsCorrectUrl_WithLeadingSlash()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.Equal($"{BaseUrl}/api/test", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetAsync_BuildsCorrectUrl_WithoutLeadingSlash()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.GetAsync("api/test");

        Assert.NotNull(capturedRequest);
        Assert.Equal($"{BaseUrl}/api/test", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetAsync_AddsLoopPreventionHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains(LoopPreventionHeader));
        Assert.Equal("true", capturedRequest.Headers.GetValues(LoopPreventionHeader).First());
    }

    [Fact]
    public async Task GetAsync_ForwardsAuthorizationHeader_WhenPresent()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Bearer test-token-12345");

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("test-token-12345", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetAsync_NoAuthHeader_WhenHttpContextNull()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns((HttpContext?)null);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Headers.Authorization);
    }

    [Fact]
    public async Task GetAsync_NoAuthHeader_WhenAuthorizationEmpty()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext(null);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Headers.Authorization);
    }

    [Fact]
    public async Task GetAsync_ReturnsContent_OnSuccess()
    {
        var expectedContent = """{"data": "test value"}""";
        SetupHttpResponse(HttpStatusCode.OK, expectedContent);

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(expectedContent, Text(result));
    }

    [Fact]
    public async Task GetAsync_ReturnsIsError_OnFailure()
    {
        SetupHttpResponse(HttpStatusCode.BadRequest, "Bad Request Error");

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        Assert.True(result.IsError);
        Assert.Contains("400", Text(result));
        Assert.Contains("Bad Request Error", Text(result));
    }

    [Fact]
    public async Task GetAsync_RespectsCancellationToken()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());

        var invoker = CreateInvoker();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.GetAsync("/api/test", cts.Token));
    }

    [Fact]
    public async Task GetAsync_UsesHttpGetMethod()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest.Method);
    }

    #endregion

    #region PostAsync Tests

    [Fact]
    public async Task PostAsync_SendsJsonBody_WhenProvided()
    {
        string? capturedContent = null;
        string? capturedMediaType = null;
        string? capturedCharSet = null;

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                if (request.Content is not null)
                {
                    capturedContent = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    capturedMediaType = request.Content.Headers.ContentType?.MediaType;
                    capturedCharSet = request.Content.Headers.ContentType?.CharSet;
                }
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("response")
            });

        var jsonBody = """{"name": "test"}""";
        var invoker = CreateInvoker();
        await invoker.PostAsync("/api/test", jsonBody);

        Assert.NotNull(capturedContent);
        Assert.Equal(jsonBody, capturedContent);
        Assert.Equal("application/json", capturedMediaType);
        Assert.Equal("utf-8", capturedCharSet);
    }

    [Fact]
    public async Task PostAsync_NoBody_WhenJsonBodyNull()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PostAsync("/api/test", null);

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Content);
    }

    [Fact]
    public async Task PostAsync_AddsLoopPreventionHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PostAsync("/api/test", null);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains(LoopPreventionHeader));
        Assert.Equal("true", capturedRequest.Headers.GetValues(LoopPreventionHeader).First());
    }

    [Fact]
    public async Task PostAsync_ForwardsAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Bearer post-token-xyz");

        var invoker = CreateInvoker();
        await invoker.PostAsync("/api/test", null);

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("post-token-xyz", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task PostAsync_ReturnsContent_OnSuccess()
    {
        var expectedContent = """{"id": 123, "created": true}""";
        SetupHttpResponse(HttpStatusCode.Created, expectedContent);

        var invoker = CreateInvoker();
        var result = await invoker.PostAsync("/api/test", """{"name": "new"}""");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(expectedContent, Text(result));
    }

    [Fact]
    public async Task PostAsync_ReturnsIsError_OnFailure()
    {
        SetupHttpResponse(HttpStatusCode.InternalServerError, "Internal Server Error");

        var invoker = CreateInvoker();
        var result = await invoker.PostAsync("/api/test", """{"data": "test"}""");

        Assert.True(result.IsError);
        Assert.Contains("500", Text(result));
    }

    [Fact]
    public async Task PostAsync_UsesHttpPostMethod()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PostAsync("/api/test", null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
    }

    [Fact]
    public async Task PostAsync_BuildsCorrectUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PostAsync("api/items", null);

        Assert.NotNull(capturedRequest);
        Assert.Equal($"{BaseUrl}/api/items", capturedRequest.RequestUri?.ToString());
    }

    #endregion

    #region ConfigureRequest Tests

    [Fact]
    public async Task ConfigureRequest_ParsesValidBearerToken()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test");

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ConfigureRequest_ParsesBasicAuth()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Basic dXNlcjpwYXNz");

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Basic", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("dXNlcjpwYXNz", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ConfigureRequest_LogsWarning_OnInvalidAuthHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = ","; // invalid format - comma is a header delimiter, not a valid scheme
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Failed to parse incoming Authorization header")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region HandleResponse Tests

    [Fact]
    public async Task HandleResponse_TruncatesBody_At1000Chars()
    {
        var longContent = new string('x', 2000);
        SetupHttpResponse(HttpStatusCode.BadRequest, longContent);

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        Assert.True(result.IsError);
        Assert.Contains(new string('x', 1000), Text(result));
        Assert.DoesNotContain(new string('x', 1001), Text(result));
    }

    [Fact]
    public async Task HandleResponse_LogsWarning_OnFailure()
    {
        SetupHttpResponse(HttpStatusCode.NotFound, "Resource not found");

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("MCP HTTP call failed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleResponse_LogsTruncatedContent_OnFailure()
    {
        var longContent = new string('y', 1000);
        SetupHttpResponse(HttpStatusCode.InternalServerError, longContent);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("500")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleResponse_DoesNotLog_OnSuccess()
    {
        SetupHttpResponse(HttpStatusCode.OK, "Success response");

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/test");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HandleResponse_ReturnsIsError_ForVariousStatusCodes(HttpStatusCode statusCode)
    {
        SetupHttpResponse(statusCode, "Error content");

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        Assert.True(result.IsError);
        Assert.Contains(((int)statusCode).ToString(), Text(result));
    }

    [Fact]
    public async Task HandleResponse_PreservesShortBody()
    {
        var shortContent = "Short error message";
        SetupHttpResponse(HttpStatusCode.BadRequest, shortContent);

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        Assert.Contains(shortContent, Text(result));
    }

    #endregion

    #region PutAsync Tests

    [Fact]
    public async Task PutAsync_UsesHttpPutMethod()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PutAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Put, capturedRequest.Method);
    }

    [Fact]
    public async Task PutAsync_SendsJsonBody_WhenProvided()
    {
        string? capturedContent = null;
        string? capturedMediaType = null;

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                if (request.Content is not null)
                {
                    capturedContent = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    capturedMediaType = request.Content.Headers.ContentType?.MediaType;
                }
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("response")
            });

        var jsonBody = """{"name": "updated"}""";
        var invoker = CreateInvoker();
        await invoker.PutAsync("/api/test/1", jsonBody);

        Assert.NotNull(capturedContent);
        Assert.Equal(jsonBody, capturedContent);
        Assert.Equal("application/json", capturedMediaType);
    }

    [Fact]
    public async Task PutAsync_NoBody_WhenJsonBodyNull()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PutAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Content);
    }

    [Fact]
    public async Task PutAsync_AddsLoopPreventionHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PutAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains(LoopPreventionHeader));
        Assert.Equal("true", capturedRequest.Headers.GetValues(LoopPreventionHeader).First());
    }

    [Fact]
    public async Task PutAsync_BuildsCorrectUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PutAsync("api/items/123", null);

        Assert.NotNull(capturedRequest);
        Assert.Equal($"{BaseUrl}/api/items/123", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task PutAsync_ForwardsAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Bearer put-token-xyz");

        var invoker = CreateInvoker();
        await invoker.PutAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("put-token-xyz", capturedRequest.Headers.Authorization.Parameter);
    }

    #endregion

    #region PatchAsync Tests

    [Fact]
    public async Task PatchAsync_UsesHttpPatchMethod()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PatchAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Patch, capturedRequest.Method);
    }

    [Fact]
    public async Task PatchAsync_SendsJsonBody_WhenProvided()
    {
        string? capturedContent = null;
        string? capturedMediaType = null;

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                if (request.Content is not null)
                {
                    capturedContent = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    capturedMediaType = request.Content.Headers.ContentType?.MediaType;
                }
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("response")
            });

        var jsonBody = """{"name": "patched"}""";
        var invoker = CreateInvoker();
        await invoker.PatchAsync("/api/test/1", jsonBody);

        Assert.NotNull(capturedContent);
        Assert.Equal(jsonBody, capturedContent);
        Assert.Equal("application/json", capturedMediaType);
    }

    [Fact]
    public async Task PatchAsync_NoBody_WhenJsonBodyNull()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PatchAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Content);
    }

    [Fact]
    public async Task PatchAsync_AddsLoopPreventionHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PatchAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains(LoopPreventionHeader));
        Assert.Equal("true", capturedRequest.Headers.GetValues(LoopPreventionHeader).First());
    }

    [Fact]
    public async Task PatchAsync_BuildsCorrectUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.PatchAsync("api/items/456", null);

        Assert.NotNull(capturedRequest);
        Assert.Equal($"{BaseUrl}/api/items/456", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task PatchAsync_ForwardsAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Bearer patch-token-xyz");

        var invoker = CreateInvoker();
        await invoker.PatchAsync("/api/test/1", null);

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("patch-token-xyz", capturedRequest.Headers.Authorization.Parameter);
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_UsesHttpDeleteMethod()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.DeleteAsync("/api/test/1");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Delete, capturedRequest.Method);
    }

    [Fact]
    public async Task DeleteAsync_HasNoBody()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.DeleteAsync("/api/test/1");

        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Content);
    }

    [Fact]
    public async Task DeleteAsync_AddsLoopPreventionHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.DeleteAsync("/api/test/1");

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains(LoopPreventionHeader));
        Assert.Equal("true", capturedRequest.Headers.GetValues(LoopPreventionHeader).First());
    }

    [Fact]
    public async Task DeleteAsync_BuildsCorrectUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.DeleteAsync("api/items/789");

        Assert.NotNull(capturedRequest);
        Assert.Equal($"{BaseUrl}/api/items/789", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task DeleteAsync_ForwardsAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Bearer delete-token-xyz");

        var invoker = CreateInvoker();
        await invoker.DeleteAsync("/api/test/1");

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("delete-token-xyz", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsContent_OnSuccess()
    {
        var expectedContent = """{"deleted": true}""";
        SetupHttpResponse(HttpStatusCode.OK, expectedContent);

        var invoker = CreateInvoker();
        var result = await invoker.DeleteAsync("/api/test/1");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(expectedContent, Text(result));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsIsError_OnFailure()
    {
        SetupHttpResponse(HttpStatusCode.NotFound, "Resource not found");

        var invoker = CreateInvoker();
        var result = await invoker.DeleteAsync("/api/test/999");

        Assert.True(result.IsError);
        Assert.Contains("404", Text(result));
    }

    #endregion

    #region BuildUrl Tests

    [Fact]
    public async Task BuildUrl_NamesTheRequestAtTheAddressTheMcpRequestCameTo_NotAtTheProviders()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContextWithSchemeAndHost("https", "api.satis.fit:8443");

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.satis.fit:8443/api/changelog", capturedRequest.RequestUri?.ToString());
        _baseUrlProviderMock.Verify(x => x.GetBaseUrl(), Times.Never);
    }

    [Fact]
    public async Task BuildUrl_NamesTheRequestAtTheProvidersAddress_WhenTheMcpRequestHasNoHost()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContext("Bearer test-token");

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal($"{BaseUrl}/api/changelog", capturedRequest.RequestUri?.ToString());
    }

    #endregion

    #region SendAsync Tests

    [Fact]
    public async Task SendAsync_SendsTheHeadersAndTheForm_NextToTheInvokersOwnHeaders()
    {
        SetupHttpContext("Bearer caller-token");
        string? tenants = null;
        string? authorization = null;
        string? internalCall = null;
        string? contentType = null;
        string? form = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", request =>
        {
            tenants = string.Join("|", request.Headers.GetValues("X-Tenant"));
            authorization = request.Headers.Authorization?.ToString();
            internalCall = string.Join("|", request.Headers.GetValues(LoopPreventionHeader));
            contentType = request.Content?.Headers.ContentType?.ToString();
            form = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        var result = await CreateInvoker().SendAsync(new McpHttpRequest(HttpMethod.Put, "/api/reviews")
        {
            Headers = [new("X-Tenant", "t1"), new("X-Tenant", "t2")],
            FormBody = "rating=5&comment=Fast%20and%20quiet"
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("t1|t2", tenants);
        Assert.Equal("Bearer caller-token", authorization);
        Assert.Equal("true", internalCall);
        Assert.Equal("application/x-www-form-urlencoded; charset=utf-8", contentType);
        Assert.Equal("rating=5&comment=Fast%20and%20quiet", form);
    }

    [Fact]
    public async Task SendAsync_SendsAHeaderNextToAJsonBody()
    {
        string? key = null;
        string? body = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", request =>
        {
            key = string.Join("|", request.Headers.GetValues("Idempotency-Key"));
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        await CreateInvoker().SendAsync(new McpHttpRequest(HttpMethod.Post, "/api/reviews")
        {
            JsonBody = "{\"rating\":5}",
            Headers = [new("Idempotency-Key", "k-1")]
        });

        Assert.Equal("k-1", key);
        Assert.Equal("{\"rating\":5}", body);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("cookie")]
    [InlineData("HOST")]
    [InlineData("x-mcp-internal-call")]
    [InlineData("Transfer-Encoding")]
    [InlineData("traceparent")]
    [InlineData("Content-Type")]
    [InlineData("Content-Language")]
    [InlineData("X Tenant")]
    public async Task SendAsync_RefusesAHeaderTheToolsRequestNeverTakesFromATool(string name)
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var request = new McpHttpRequest(HttpMethod.Get, "/api/x") { Headers = [new(name, "value")] };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateInvoker().SendAsync(request));
        Assert.Null(capturedRequest);
    }

    [Fact]
    public async Task SendAsync_DropsTheSpacesAndTabsAroundAHeaderValue_AsKestrelDoes()
    {
        string? name = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", request => name = string.Join("|", request.Headers.GetValues("X-Name")));

        await CreateInvoker().SendAsync(new McpHttpRequest(HttpMethod.Get, "/api/x") { Headers = [new("X-Name", "  padded\t ")] });

        Assert.Equal("padded", name);
    }

    [Theory]
    [InlineData("X-MS-CLIENT-PRINCIPAL-ID")]
    [InlineData("x-forwarded-for")]
    [InlineData("X-HTTP-Method-Override")]
    [InlineData("Connection")]
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Auth-Request-User")]
    public async Task SendAsync_RefusesAHeaderAFrontEndOrAMiddlewareTrusts(string name)
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var request = new McpHttpRequest(HttpMethod.Get, "/api/x") { Headers = [new(name, "value")] };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateInvoker().SendAsync(request));
        Assert.Null(capturedRequest);
    }

    [Fact]
    public async Task SendAsync_RefusesAHeaderValueAnHttpHeaderCannotCarry()
    {
        var request = new McpHttpRequest(HttpMethod.Get, "/api/x") { Headers = [new("X-Tenant", "a\r\nX-Injected: 1")] };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateInvoker().SendAsync(request));
    }

    [Fact]
    public async Task SendAsync_RefusesARequestWithAJsonBodyAndAForm()
    {
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x") { JsonBody = "{}", FormBody = "a=1" };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateInvoker().SendAsync(request));
    }

    [Theory]
    [InlineData("X-Azure-ClientIP")]
    [InlineData("x-gateway-cert")]
    public async Task SendAsync_ReturnsAnErrorResultNamingTheArgument_ForAHeaderTheAppTrustsFromItsProxy(string headerName)
    {
        // UseForwardedHeaders would take the client's address from the AI's value, and UseCertificateForwarding the
        // client certificate, which certificate authentication signs the user in with.
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        var invoker = CreateInvokerTrustingProxyHeaders(
            new ForwardedHeadersOptions { ForwardedForHeaderName = "X-Azure-ClientIP" },
            new CertificateForwardingOptions { CertificateHeader = "X-Gateway-Cert" });
        var request = new McpHttpRequest(HttpMethod.Get, "/api/x")
        {
            Headers = [new("X-Tenant", "t1"), new(headerName, "value")],
            HeaderArguments = new Dictionary<string, string> { ["X-Tenant"] = "tenant", [headerName] = "client" }
        };

        var result = await invoker.SendAsync(request);

        Assert.True(result.IsError);
        Assert.Equal(
            $"The argument 'client' is sent as the request header '{headerName}', which the app trusts from its proxy: its ForwardedHeadersOptions " +
            "or CertificateForwardingOptions name it, and its value would become the client's address, scheme, host or certificate. Call the tool without 'client'.",
            ((TextContentBlock)Assert.Single(result.Content)).Text);
        Assert.Null(capturedRequest);
    }

    [Fact]
    public async Task SendAsync_NamesTheHeaderTheAppTrustsFromItsProxy_WhenTheRequestNamesNoArgumentForIt()
    {
        var invoker = CreateInvokerTrustingProxyHeaders(new ForwardedHeadersOptions { ForwardedHostHeaderName = "X-Host" }, new CertificateForwardingOptions());

        var result = await invoker.SendAsync(new McpHttpRequest(HttpMethod.Get, "/api/x") { Headers = [new("X-Host", "evil.example")] });

        Assert.True(result.IsError);
        Assert.StartsWith("The tool's request would carry the header 'X-Host', which the app trusts from its proxy:", ((TextContentBlock)Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void GetProxyHeaderNames_HoldsEveryHeaderNameTheForwardingOptionsGive()
    {
        var forwardedHeaders = new ForwardedHeadersOptions
        {
            ForwardedForHeaderName = "A-For",
            ForwardedHostHeaderName = "A-Host",
            ForwardedProtoHeaderName = "A-Proto",
            ForwardedPrefixHeaderName = "A-Prefix",
            OriginalForHeaderName = "B-For",
            OriginalHostHeaderName = "B-Host",
            OriginalProtoHeaderName = "B-Proto",
            OriginalPrefixHeaderName = "B-Prefix"
        };

        var names = ToolRequestHeaders.GetProxyHeaderNames(forwardedHeaders, new CertificateForwardingOptions { CertificateHeader = "C-Cert" });

        Assert.Equal(
            ["A-For", "A-Host", "A-Prefix", "A-Proto", "B-For", "B-Host", "B-Prefix", "B-Proto", "C-Cert"],
            names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SendAsync_NamesTheCultureOfTheForm_WhenTheMcpRequestRanRequestLocalization()
    {
        // MVC reads a form in the culture UseRequestLocalization picks for the tool's request, and the tool writes it in
        // the MCP request's: without the header, an app whose default is de-DE would read 1.5 written in en-US as 15.
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "en-US");

        var result = await CreateInvoker().SendAsync(new McpHttpRequest(HttpMethod.Post, "/api/x")
        {
            FormBody = "amount=1.5",
            FormCulture = CultureInfo.GetCultureInfo("en-US"),
            Headers = [new("X-Tenant", "t1")]
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal([("X-Tenant", "t1"), ("Accept-Language", "en-US")], Assert.Single(sent).Headers.Where(x => x.Item1 != LoopPreventionHeader));
    }

    [Fact]
    public async Task SendAsync_NamesTheMcpRequestsCulture_ForAFormOfTextAlone_SoEveryFormRequestOfTheToolRunsInIt()
    {
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "de-DE");

        await CreateInvoker().SendAsync(new McpHttpRequest(HttpMethod.Post, "/api/x") { FormBody = "name=Ann" });

        Assert.Contains(("Accept-Language", "de-DE"), Assert.Single(sent).Headers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("de-DE")]
    public async Task SendAsync_NamesNoCulture_WhenTheMcpRequestRanNoRequestLocalization(string? language)
    {
        // The app then reads the form in the server's culture, which the tool wrote it in, whatever the request names.
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: null);
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x?culture=fr-FR")
        {
            FormBody = "amount=1.5",
            FormCulture = CultureInfo.GetCultureInfo("en-US"),
            Headers = language is null ? [] : [new("Accept-Language", language)]
        };

        var result = await CreateInvoker().SendAsync(request);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(language is null ? [] : [("Accept-Language", language)], Assert.Single(sent).Headers.Where(x => x.Item1 != LoopPreventionHeader));
    }

    [Fact]
    public async Task SendAsync_NamesNoCulture_ForARequestWithoutAForm()
    {
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "en-US");

        await CreateInvoker().SendAsync(new McpHttpRequest(HttpMethod.Post, "/api/x") { JsonBody = "{\"amount\":1.5}" });

        Assert.DoesNotContain(Assert.Single(sent).Headers, x => x.Item1 == "Accept-Language");
    }

    [Theory]
    [InlineData("/api/x", "de-DE")]
    [InlineData("/api/x", " DE-de ")]
    [InlineData("/api/x?culture=de-DE", null)]
    [InlineData("/api/x?ui-culture=de-de", null)]
    [InlineData("/api/x?culture=de-DE&ui-culture=en-US", null)]
    public async Task SendAsync_SendsTheForm_WhenTheRequestNamesTheCultureTheFormIsWrittenIn(string route, string? language)
    {
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "de-DE");
        var request = new McpHttpRequest(HttpMethod.Post, route)
        {
            FormBody = "amount=1%2C5",
            FormCulture = CultureInfo.GetCultureInfo("de-DE"),
            Headers = language is null ? [] : [new("Accept-Language", language)]
        };

        var result = await CreateInvoker().SendAsync(request);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal([("Accept-Language", language?.Trim() ?? "de-DE")], Assert.Single(sent).Headers.Where(x => x.Item1 != LoopPreventionHeader));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE, en;q=0.5")]
    [InlineData("de")]
    public async Task SendAsync_RefusesAnAcceptLanguageArgumentNamingAnotherCultureThanTheForms_WithAnErrorResult(string language)
    {
        // The app would read the form in the culture the AI named, if it supports that one, or in its default.
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "de-DE");
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x")
        {
            FormBody = "amount=1%2C25",
            FormCulture = CultureInfo.GetCultureInfo("de-DE"),
            Headers = [new("accept-language", language)],
            HeaderArguments = new Dictionary<string, string> { ["Accept-Language"] = "language" }
        };

        var result = await CreateInvoker().SendAsync(request);

        Assert.True(result.IsError);
        Assert.Equal(
            "The argument 'language' is sent as the request header 'Accept-Language', which picks the culture the app reads the tool's form in, " +
            "and the tool writes the form's numbers and dates in the culture of the MCP request, 'de-DE'. The tool sends 'language' only with the " +
            "value 'de-DE': another culture needs an MCP request that runs in it, as one whose own Accept-Language header asks for it does.",
            Text(result));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task SendAsync_RefusesTwoAcceptLanguageValues_EvenOfTheFormsCulture()
    {
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "de-DE");
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x")
        {
            FormBody = "amount=1%2C25",
            FormCulture = CultureInfo.GetCultureInfo("de-DE"),
            Headers = [new("Accept-Language", "de-DE"), new("Accept-Language", "en-US")]
        };

        var result = await CreateInvoker().SendAsync(request);

        Assert.True(result.IsError);
        Assert.StartsWith("The tool's request carries the header 'Accept-Language', which picks the culture", Text(result));
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData("/api/x?culture=en-US", "culture")]
    [InlineData("/api/x?Culture=en-US", "culture")]
    [InlineData("/api/x?ui-culture=en-US", "ui-culture")]
    [InlineData("/api/x?culture=en-US&ui-culture=de-DE", "culture")]
    [InlineData("/api/x?culture=de-DE&culture=en-US", "culture")]
    public async Task SendAsync_RefusesACultureQueryValueNamingAnotherCultureThanTheForms_WithAnErrorResult(string route, string key)
    {
        // UseRequestLocalization picks a culture from the query before Accept-Language, and from ui-culture when the
        // query has no culture.
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "de-DE");
        var request = new McpHttpRequest(HttpMethod.Post, route) { FormBody = "amount=1%2C25", FormCulture = CultureInfo.GetCultureInfo("de-DE") };

        var result = await CreateInvoker().SendAsync(request);

        Assert.True(result.IsError);
        Assert.Equal(
            $"The query value '{key}' of the tool's request picks the culture the app reads the tool's form in, and the tool writes the form's " +
            $"numbers and dates in the culture of the MCP request, 'de-DE'. The tool sends the argument that fills '{key}' only with the value " +
            "'de-DE': another culture needs an MCP request that runs in it, as one whose own Accept-Language header asks for it does.",
            Text(result));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task SendAsync_SendsACultureArgument_ForAFormOfTextAlone()
    {
        // A form without numbers and dates reads the same in every culture, so the AI's culture is the action's own data.
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "de-DE");
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x?culture=fr-FR")
        {
            FormBody = "text=hello",
            Headers = [new("Accept-Language", "en-US")]
        };

        var result = await CreateInvoker().SendAsync(request);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal([("Accept-Language", "en-US")], Assert.Single(sent).Headers.Where(x => x.Item1 != LoopPreventionHeader));
    }

    [Fact]
    public async Task SendAsync_RefusesAnyCulture_ForAFormWrittenInTheInvariantCulture_UnderRequestLocalization()
    {
        var sent = CaptureRequests();
        SetupMcpRequest(localizedCulture: "");
        var request = new McpHttpRequest(HttpMethod.Post, "/api/x?culture=en-US") { FormBody = "amount=1.5", FormCulture = CultureInfo.InvariantCulture };

        var result = await CreateInvoker().SendAsync(request);

        Assert.True(result.IsError);
        Assert.EndsWith(
            "in the culture of the MCP request, the invariant culture. The tool cannot send the argument that fills 'culture' with a value, as no culture name stands for that culture.",
            Text(result));
        Assert.Empty(sent);
    }

    /// <summary>
    /// The MCP request the tool call runs in, with the culture <c>UseRequestLocalization</c> picked for it, or without
    /// that middleware when <paramref name="localizedCulture"/> is null.
    /// </summary>
    private void SetupMcpRequest(string? localizedCulture)
    {
        var httpContext = new DefaultHttpContext();
        if (localizedCulture is not null)
        {
            httpContext.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(new RequestCulture(localizedCulture), provider: null));
        }

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
    }

    /// <summary>
    /// The requests the invoker sends, each with its headers as they go out.
    /// </summary>
    private List<(string Route, List<(string, string)> Headers)> CaptureRequests()
    {
        var sent = new List<(string Route, List<(string, string)> Headers)>();
        SetupHttpResponse(HttpStatusCode.OK, "response", request => sent.Add((
            request.RequestUri!.PathAndQuery,
            request.Headers.SelectMany(x => x.Value.Select(value => (x.Key, value))).ToList())));

        return sent;
    }

    private McpHttpInvoker CreateInvokerTrustingProxyHeaders(ForwardedHeadersOptions forwardedHeaders, CertificateForwardingOptions certificateForwarding)
    {
        return CreateInvoker(ToolRequestHeaders.GetProxyHeaderNames(forwardedHeaders, certificateForwarding));
    }

    #endregion
}
