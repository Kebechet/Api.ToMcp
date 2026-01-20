using System.Net;
using System.Text.Json;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Http;
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

        Assert.Equal(expectedContent, result);
    }

    [Fact]
    public async Task GetAsync_ReturnsErrorJson_OnFailure()
    {
        SetupHttpResponse(HttpStatusCode.BadRequest, "Bad Request Error");

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        var errorResponse = JsonDocument.Parse(result);
        Assert.True(errorResponse.RootElement.GetProperty("error").GetBoolean());
        Assert.Equal(400, errorResponse.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Contains("400", errorResponse.RootElement.GetProperty("message").GetString());
        Assert.Equal("Bad Request Error", errorResponse.RootElement.GetProperty("body").GetString());
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

        Assert.Equal(expectedContent, result);
    }

    [Fact]
    public async Task PostAsync_ReturnsErrorJson_OnFailure()
    {
        SetupHttpResponse(HttpStatusCode.InternalServerError, "Internal Server Error");

        var invoker = CreateInvoker();
        var result = await invoker.PostAsync("/api/test", """{"data": "test"}""");

        var errorResponse = JsonDocument.Parse(result);
        Assert.True(errorResponse.RootElement.GetProperty("error").GetBoolean());
        Assert.Equal(500, errorResponse.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Contains("500", errorResponse.RootElement.GetProperty("message").GetString());
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
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Failed to parse Authorization header")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region HandleResponse Tests

    [Fact]
    public async Task HandleResponse_TruncatesContent_At1000Chars()
    {
        var longContent = new string('x', 2000);
        SetupHttpResponse(HttpStatusCode.BadRequest, longContent);

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        var errorResponse = JsonDocument.Parse(result);
        var body = errorResponse.RootElement.GetProperty("body").GetString();

        Assert.NotNull(body);
        Assert.Equal(1000, body.Length);
        Assert.Equal(new string('x', 1000), body);
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
    public async Task HandleResponse_ReturnsErrorJson_ForVariousStatusCodes(HttpStatusCode statusCode)
    {
        SetupHttpResponse(statusCode, "Error content");

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        var errorResponse = JsonDocument.Parse(result);
        Assert.True(errorResponse.RootElement.GetProperty("error").GetBoolean());
        Assert.Equal((int)statusCode, errorResponse.RootElement.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task HandleResponse_PreservesShortContent()
    {
        var shortContent = "Short error message";
        SetupHttpResponse(HttpStatusCode.BadRequest, shortContent);

        var invoker = CreateInvoker();
        var result = await invoker.GetAsync("/api/test");

        var errorResponse = JsonDocument.Parse(result);
        var body = errorResponse.RootElement.GetProperty("body").GetString();

        Assert.Equal(shortContent, body);
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

        Assert.Equal(expectedContent, result);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsErrorJson_OnFailure()
    {
        SetupHttpResponse(HttpStatusCode.NotFound, "Resource not found");

        var invoker = CreateInvoker();
        var result = await invoker.DeleteAsync("/api/test/999");

        var errorResponse = JsonDocument.Parse(result);
        Assert.True(errorResponse.RootElement.GetProperty("error").GetBoolean());
        Assert.Equal(404, errorResponse.RootElement.GetProperty("statusCode").GetInt32());
    }

    #endregion
}
