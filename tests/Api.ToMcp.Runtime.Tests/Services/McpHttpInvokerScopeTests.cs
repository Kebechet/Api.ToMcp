using System.Security.Claims;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpHttpInvokerScopeTests
{
    private const string BaseUrl = "https://localhost:5000";

    private readonly Mock<ISelfBaseUrlProvider> _baseUrlProviderMock;
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private readonly Mock<ILogger<McpHttpInvoker>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;

    public McpHttpInvokerScopeTests()
    {
        _baseUrlProviderMock = new Mock<ISelfBaseUrlProvider>();
        _baseUrlProviderMock.Setup(x => x.GetBaseUrl()).Returns(BaseUrl);

        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _loggerMock = new Mock<ILogger<McpHttpInvoker>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
    }

    private McpHttpInvoker CreateInvoker(McpScopeOptions options)
    {
        var scopeOptionsMock = new Mock<IOptions<McpScopeOptions>>();
        scopeOptionsMock.Setup(x => x.Value).Returns(options);

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        return new McpHttpInvoker(
            httpClient,
            _baseUrlProviderMock.Object,
            _httpContextAccessorMock.Object,
            _loggerMock.Object,
            scopeOptionsMock.Object);
    }

    private void SetupAuthenticatedUser(string claimName, string claimValue)
    {
        var claims = new[] { new Claim(claimName, claimValue) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
    }

    private void SetupUnauthenticatedUser()
    {
        var httpContext = new DefaultHttpContext();
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
    }

    private void SetupAuthenticatedUserWithoutClaim()
    {
        var identity = new ClaimsIdentity(Array.Empty<Claim>(), "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
    }

    #region BeforeInvokeAsync - No Scope Mapper Configured

    [Fact]
    public async Task BeforeInvokeAsync_NoScopeMapper_DoesNotThrow()
    {
        var options = new McpScopeOptions { ClaimToScopeMapper = null };
        var invoker = CreateInvoker(options);

        var exception = await Record.ExceptionAsync(() =>
            invoker.BeforeInvokeAsync(McpScope.Delete));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(McpScope.Read)]
    [InlineData(McpScope.Write)]
    [InlineData(McpScope.Delete)]
    [InlineData(McpScope.All)]
    public async Task BeforeInvokeAsync_NoScopeMapper_AllowsAllScopes(McpScope scope)
    {
        var options = new McpScopeOptions { ClaimToScopeMapper = null };
        var invoker = CreateInvoker(options);

        var exception = await Record.ExceptionAsync(() =>
            invoker.BeforeInvokeAsync(scope));

        Assert.Null(exception);
    }

    #endregion

    #region BeforeInvokeAsync - User Not Authenticated

    [Fact]
    public async Task BeforeInvokeAsync_UserNotAuthenticated_ThrowsUnauthorizedAccessException()
    {
        SetupUnauthenticatedUser();

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = _ => McpScope.Read
        };
        var invoker = CreateInvoker(options);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Read));

        Assert.Contains("not authenticated", exception.Message);
    }

    [Fact]
    public async Task BeforeInvokeAsync_HttpContextNull_ThrowsUnauthorizedAccessException()
    {
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns((HttpContext?)null);

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = _ => McpScope.Read
        };
        var invoker = CreateInvoker(options);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Read));

        Assert.Contains("not authenticated", exception.Message);
    }

    #endregion

    #region BeforeInvokeAsync - Claim Not Found

    [Fact]
    public async Task BeforeInvokeAsync_ClaimNotFound_ThrowsUnauthorizedAccessException()
    {
        SetupAuthenticatedUserWithoutClaim();

        var options = new McpScopeOptions
        {
            ClaimName = "permissions",
            ClaimToScopeMapper = _ => McpScope.Read
        };
        var invoker = CreateInvoker(options);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Read));

        Assert.Contains("permissions", exception.Message);
        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public async Task BeforeInvokeAsync_WrongClaimName_ThrowsUnauthorizedAccessException()
    {
        SetupAuthenticatedUser("scope", "read write");

        var options = new McpScopeOptions
        {
            ClaimName = "permissions",
            ClaimToScopeMapper = _ => McpScope.Read
        };
        var invoker = CreateInvoker(options);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Read));

        Assert.Contains("permissions", exception.Message);
    }

    #endregion

    #region BeforeInvokeAsync - Scope Validation Success

    [Fact]
    public async Task BeforeInvokeAsync_SufficientScope_DoesNotThrow()
    {
        SetupAuthenticatedUser("scope", "read write delete");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = claimValue =>
            {
                var scope = McpScope.None;
                if (claimValue.Contains("read")) scope |= McpScope.Read;
                if (claimValue.Contains("write")) scope |= McpScope.Write;
                if (claimValue.Contains("delete")) scope |= McpScope.Delete;
                return scope;
            }
        };
        var invoker = CreateInvoker(options);

        var exception = await Record.ExceptionAsync(() =>
            invoker.BeforeInvokeAsync(McpScope.Write));

        Assert.Null(exception);
    }

    [Fact]
    public async Task BeforeInvokeAsync_ReadScope_AllowsReadOperation()
    {
        SetupAuthenticatedUser("permissions", "mcp:read");

        var options = new McpScopeOptions
        {
            ClaimName = "permissions",
            ClaimToScopeMapper = claimValue =>
                claimValue.Contains("read") ? McpScope.Read : McpScope.None
        };
        var invoker = CreateInvoker(options);

        var exception = await Record.ExceptionAsync(() =>
            invoker.BeforeInvokeAsync(McpScope.Read));

        Assert.Null(exception);
    }

    [Fact]
    public async Task BeforeInvokeAsync_AllScope_AllowsAnyOperation()
    {
        SetupAuthenticatedUser("scope", "admin");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = claimValue =>
                claimValue == "admin" ? McpScope.All : McpScope.None
        };
        var invoker = CreateInvoker(options);

        await invoker.BeforeInvokeAsync(McpScope.Read);
        await invoker.BeforeInvokeAsync(McpScope.Write);
        await invoker.BeforeInvokeAsync(McpScope.Delete);
    }

    #endregion

    #region BeforeInvokeAsync - Scope Validation Failure

    [Fact]
    public async Task BeforeInvokeAsync_InsufficientScope_ThrowsUnauthorizedAccessException()
    {
        SetupAuthenticatedUser("scope", "read");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = claimValue =>
                claimValue.Contains("read") ? McpScope.Read : McpScope.None
        };
        var invoker = CreateInvoker(options);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Write));

        Assert.Contains("Insufficient scope", exception.Message);
        Assert.Contains("Write", exception.Message);
        Assert.Contains("Read", exception.Message);
    }

    [Fact]
    public async Task BeforeInvokeAsync_ReadOnlyScope_BlocksDeleteOperation()
    {
        SetupAuthenticatedUser("scope", "read");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = claimValue =>
                claimValue.Contains("read") ? McpScope.Read : McpScope.None
        };
        var invoker = CreateInvoker(options);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Delete));

        Assert.Contains("Delete", exception.Message);
    }

    [Fact]
    public async Task BeforeInvokeAsync_ReadWriteScope_BlocksDeleteOperation()
    {
        SetupAuthenticatedUser("scope", "read write");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = claimValue =>
            {
                var scope = McpScope.None;
                if (claimValue.Contains("read")) scope |= McpScope.Read;
                if (claimValue.Contains("write")) scope |= McpScope.Write;
                return scope;
            }
        };
        var invoker = CreateInvoker(options);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Delete));

        Assert.Contains("Delete", exception.Message);
    }

    [Fact]
    public async Task BeforeInvokeAsync_NoScope_BlocksAllOperations()
    {
        SetupAuthenticatedUser("scope", "none");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = _ => McpScope.None
        };
        var invoker = CreateInvoker(options);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Read));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Write));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Delete));
    }

    #endregion

    #region BeforeInvokeAsync - Custom Claim Names

    [Theory]
    [InlineData("scope")]
    [InlineData("permissions")]
    [InlineData("roles")]
    [InlineData("mcp_scope")]
    [InlineData("custom-claim")]
    public async Task BeforeInvokeAsync_CustomClaimName_ReadsCorrectClaim(string claimName)
    {
        SetupAuthenticatedUser(claimName, "read write delete");

        var options = new McpScopeOptions
        {
            ClaimName = claimName,
            ClaimToScopeMapper = _ => McpScope.All
        };
        var invoker = CreateInvoker(options);

        var exception = await Record.ExceptionAsync(() =>
            invoker.BeforeInvokeAsync(McpScope.Delete));

        Assert.Null(exception);
    }

    #endregion

    #region BeforeInvokeAsync - Mapper Function Behavior

    [Fact]
    public async Task BeforeInvokeAsync_MapperReceivesClaimValue()
    {
        const string expectedClaimValue = "mcp:read mcp:write";
        string? receivedClaimValue = null;

        SetupAuthenticatedUser("permissions", expectedClaimValue);

        var options = new McpScopeOptions
        {
            ClaimName = "permissions",
            ClaimToScopeMapper = claimValue =>
            {
                receivedClaimValue = claimValue;
                return McpScope.All;
            }
        };
        var invoker = CreateInvoker(options);

        await invoker.BeforeInvokeAsync(McpScope.Read);

        Assert.Equal(expectedClaimValue, receivedClaimValue);
    }

    [Fact]
    public async Task BeforeInvokeAsync_MapperCalledForEachValidation()
    {
        var callCount = 0;
        SetupAuthenticatedUser("scope", "all");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = _ =>
            {
                callCount++;
                return McpScope.All;
            }
        };
        var invoker = CreateInvoker(options);

        await invoker.BeforeInvokeAsync(McpScope.Read);
        await invoker.BeforeInvokeAsync(McpScope.Write);
        await invoker.BeforeInvokeAsync(McpScope.Delete);

        Assert.Equal(3, callCount);
    }

    #endregion

    #region BeforeInvokeAsync - Logging

    [Fact]
    public async Task BeforeInvokeAsync_LogsDebug_OnSuccessfulValidation()
    {
        SetupAuthenticatedUser("scope", "read");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = _ => McpScope.Read
        };
        var invoker = CreateInvoker(options);

        await invoker.BeforeInvokeAsync(McpScope.Read);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Scope validation passed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Integration-like Tests

    [Fact]
    public async Task BeforeInvokeAsync_TypicalJwtClaimScenario()
    {
        SetupAuthenticatedUser("scope", "openid profile mcp:read mcp:write");

        var options = new McpScopeOptions
        {
            ClaimName = "scope",
            ClaimToScopeMapper = claimValue =>
            {
                var scope = McpScope.None;
                if (claimValue.Contains("mcp:read")) scope |= McpScope.Read;
                if (claimValue.Contains("mcp:write")) scope |= McpScope.Write;
                if (claimValue.Contains("mcp:delete")) scope |= McpScope.Delete;
                return scope;
            }
        };
        var invoker = CreateInvoker(options);

        await invoker.BeforeInvokeAsync(McpScope.Read);
        await invoker.BeforeInvokeAsync(McpScope.Write);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Delete));
    }

    [Fact]
    public async Task BeforeInvokeAsync_SpaceSeparatedPermissions()
    {
        SetupAuthenticatedUser("permissions", "read write");

        var options = new McpScopeOptions
        {
            ClaimName = "permissions",
            ClaimToScopeMapper = claimValue =>
            {
                var permissions = claimValue.Split(' ');
                var scope = McpScope.None;
                foreach (var perm in permissions)
                {
                    scope |= perm switch
                    {
                        "read" => McpScope.Read,
                        "write" => McpScope.Write,
                        "delete" => McpScope.Delete,
                        _ => McpScope.None
                    };
                }
                return scope;
            }
        };
        var invoker = CreateInvoker(options);

        await invoker.BeforeInvokeAsync(McpScope.Read);
        await invoker.BeforeInvokeAsync(McpScope.Write);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invoker.BeforeInvokeAsync(McpScope.Delete));
    }

    #endregion
}
