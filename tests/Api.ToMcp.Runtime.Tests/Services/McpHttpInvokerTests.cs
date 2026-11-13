using System.Net;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using Moq.Protected;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpHttpInvokerTests
{
    private const string BaseUrl = "https://localhost:5000";
    private const string LoopPreventionHeader = "X-MCP-Internal-Call";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

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

    private McpHttpInvoker CreateInvoker()
    {
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        return new McpHttpInvoker(
            httpClient,
            _baseUrlProviderMock.Object,
            _httpContextAccessorMock.Object,
            _loggerMock.Object,
            _scopeOptionsMock.Object);
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

    #region BuildUrl Base Url Tests

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    public async Task BuildUrl_ForwardsTheScheme_ToAnyProvidersHttpAddressOnLoopback_WhenTheRequestCameInOverHttps(string baseUrl)
    {
        // Decided from the address the invoker calls, so a provider that wraps the default one forwards it too.
        _baseUrlProviderMock.Setup(x => x.GetBaseUrl()).Returns(baseUrl);
        SetupHttpContextWithSchemeAndHost("https", "api.satis.fit:8443");

        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal(baseUrl + "/api/changelog", capturedRequest.RequestUri?.ToString());
        Assert.Equal(["https"], capturedRequest.Headers.GetValues(ForwardedProtoHeader));
    }

    [Fact]
    public async Task BuildUrl_UsesTheProvidersHttpAddressOffLoopbackAsGiven_WithoutTheScheme()
    {
        // The app trusts forwarded headers from loopback only, by default.
        _baseUrlProviderMock.Setup(x => x.GetBaseUrl()).Returns("http://api.internal:8080");
        SetupHttpContextWithSchemeAndHost("https", "api.satis.fit");

        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal("http://api.internal:8080/api/changelog", capturedRequest.RequestUri?.ToString());
        Assert.False(capturedRequest.Headers.Contains(ForwardedProtoHeader));
    }

    [Fact]
    public async Task BuildUrl_ForwardsTheScheme_UnderTheHeaderNameTheAppReads()
    {
        _baseUrlProviderMock.Setup(x => x.GetBaseUrl()).Returns("http://localhost:8080");
        SetupHttpContextWithSchemeAndHost("https", "api.satis.fit");

        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = new McpHttpInvoker(
            new HttpClient(_httpMessageHandlerMock.Object),
            _baseUrlProviderMock.Object,
            _httpContextAccessorMock.Object,
            _loggerMock.Object,
            _scopeOptionsMock.Object,
            inProcessPipeline: null,
            forwardedProtoHeaderName: "X-Forwarded-Scheme");
        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal(["https"], capturedRequest.Headers.GetValues("X-Forwarded-Scheme"));
        Assert.False(capturedRequest.Headers.Contains(ForwardedProtoHeader));
    }

    [Theory]
    [InlineData("internal.example:8443")]
    [InlineData("169.254.169.254")]
    public async Task BuildUrl_SendsNoRequestToTheHostAnHttpsRequestNames_ButToTheServersHttpBinding(string requestHost)
    {
        // The Host header is the client's to set: https://github.com/Kebechet/Api.ToMcp/issues/48
        var requests = new List<(string Url, string? ForwardedProto)>();
        SetupHttpResponse(HttpStatusCode.OK, "response", r => requests.Add((
            r.RequestUri!.ToString(),
            r.Headers.TryGetValues(ForwardedProtoHeader, out var values) ? string.Join(",", values) : null)));
        SetupHttpContextWithSchemeAndHost("https", requestHost, "Bearer caller-token");
        var invoker = CreateInvokerWithDefaultProvider(["http://+:8080"], configuredBaseUrl: null);

        await invoker.GetAsync("/api/changelog");

        Assert.Equal([("http://localhost:8080/api/changelog", "https")], requests);
        Assert.DoesNotContain(requests, x => x.Url.Contains(requestHost.Split(':')[0]));
    }

    [Fact]
    public async Task BuildUrl_ForwardsNoScheme_ForARequestThatCameInOverHttp()
    {
        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);
        SetupHttpContextWithSchemeAndHost("http", "localhost:8080");
        var invoker = CreateInvokerWithDefaultProvider(["http://+:8080"], configuredBaseUrl: null);

        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal("http://localhost:8080/api/changelog", capturedRequest.RequestUri?.ToString());
        Assert.False(capturedRequest.Headers.Contains(ForwardedProtoHeader));
    }

    [Theory]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("http://api.corp.example")]
    public async Task BuildUrl_SendsNothing_WhenAnHttpsRequestsOnlyAddressIsAnHttpBindingOffLoopback(string boundAddress)
    {
        // The caller's token would go in clear text off the machine.
        var requests = new List<string>();
        SetupHttpResponse(HttpStatusCode.OK, "response", r => requests.Add(r.RequestUri!.ToString()));
        SetupHttpContextWithSchemeAndHost("https", "api.example.com", "Bearer caller-token");
        var invoker = CreateInvokerWithDefaultProvider([boundAddress], configuredBaseUrl: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.GetAsync("/api/changelog"));

        Assert.Contains("McpTools:BaseUrl", exception.Message);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task BuildUrl_UsesTheConfiguredHttpBaseUrlAsGiven_WhenTheRequestCameInOverHttps()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["McpTools:BaseUrl"] = "http://localhost:8080" })
            .Build();
        SetupHttpContextWithSchemeAndHost("https", "api.satis.fit");
        var baseUrlProvider = new DefaultBaseUrlProvider(new Mock<IServer>().Object, configuration, _httpContextAccessorMock.Object);

        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = new McpHttpInvoker(
            new HttpClient(_httpMessageHandlerMock.Object),
            baseUrlProvider,
            _httpContextAccessorMock.Object,
            _loggerMock.Object,
            _scopeOptionsMock.Object);
        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal("http://localhost:8080/api/changelog", capturedRequest.RequestUri?.ToString());
        Assert.Equal(["https"], capturedRequest.Headers.GetValues(ForwardedProtoHeader));
    }

    [Fact]
    public async Task BuildUrl_UsesTheProvidersHttpsAddress()
    {
        _baseUrlProviderMock.Setup(x => x.GetBaseUrl()).Returns("https://localhost:5100");
        SetupHttpContextWithSchemeAndHost("https", "api.satis.fit");

        HttpRequestMessage? capturedRequest = null;
        SetupHttpResponse(HttpStatusCode.OK, "response", r => capturedRequest = r);

        var invoker = CreateInvoker();
        await invoker.GetAsync("/api/changelog");

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://localhost:5100/api/changelog", capturedRequest.RequestUri?.ToString());
        Assert.False(capturedRequest.Headers.Contains(ForwardedProtoHeader));
    }

    private McpHttpInvoker CreateInvokerWithDefaultProvider(string[] boundAddresses, string? configuredBaseUrl)
    {
        var addressesFeature = new ServerAddressesFeature();
        foreach (var address in boundAddresses)
        {
            addressesFeature.Addresses.Add(address);
        }

        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addressesFeature);
        var server = new Mock<IServer>();
        server.Setup(x => x.Features).Returns(features);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["McpTools:BaseUrl"] = configuredBaseUrl })
            .Build();

        return new McpHttpInvoker(
            new HttpClient(_httpMessageHandlerMock.Object),
            new DefaultBaseUrlProvider(server.Object, configuration, _httpContextAccessorMock.Object),
            _httpContextAccessorMock.Object,
            _loggerMock.Object,
            _scopeOptionsMock.Object);
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

    #endregion
}
