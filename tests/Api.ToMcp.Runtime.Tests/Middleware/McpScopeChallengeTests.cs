using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
using Api.ToMcp.Tests.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Middleware;

public class McpScopeChallengeTests
{
    private const string ResourceMetadataUri = "https://mcp.example.com/.well-known/oauth-protected-resource/mcp";

    private readonly List<(LogLevel Level, string Message)> _logs = [];

    [Fact]
    public async Task ChallengeRefusedToolCalls_Answers403InsufficientScope_NamingEveryScopeTheToolRequires()
    {
        var context = CreateToolCall(ScopedTools.ReadDeleteTool, grantedScope: "mcp:read");

        var isServed = await RunAsync(context);

        Assert.False(isServed);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"mcp:read mcp:delete\", resource_metadata=\"{ResourceMetadataUri}\", error_description=\"Insufficient scope. Required: Read, Delete, Granted: Read\"",
            context.Response.Headers.WWWAuthenticate.ToString());
        var warning = Assert.Single(_logs, x => x.Level == LogLevel.Warning);
        Assert.Contains(ScopedTools.ReadDeleteTool, warning.Message);
        Assert.Contains("403", warning.Message);
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_Answers403_WhenTheTokenHasNoScopeClaim()
    {
        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: null);

        var isServed = await RunAsync(context);

        Assert.False(isServed);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"mcp:write\", resource_metadata=\"{ResourceMetadataUri}\", error_description=\"Claim 'scope' not found. Required scope: Write\"",
            context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_Answers401_WithTheScopes_WhenTheCallerIsNotSignedIn()
    {
        var context = CreateToolCall(ScopedTools.WriteTool);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());

        var isServed = await RunAsync(context);

        Assert.False(isServed);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{ResourceMetadataUri}\", scope=\"mcp:write\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_PassesAnAllowedCall_ToTheEndpoint_WithItsBodyIntact()
    {
        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: "mcp:read mcp:write");
        var body = ToolCallBody(ScopedTools.WriteTool);

        var servedBody = await ServeAsync(context);

        Assert.Equal(body, servedBody);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"missing\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + ScopedTools.UnscopedTool + "\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":")]
    [InlineData("not json")]
    public async Task ChallengeRefusedToolCalls_LeavesAnyOtherRequest_ToTheSdk_WithItsBodyIntact(string body)
    {
        var context = CreateRequest(body, grantedScope: null);

        var servedBody = await ServeAsync(context);

        Assert.Equal(body, servedBody);
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChallengeRefusedToolCalls_LeavesACallLargerThanTheLimit_ToTheSdk_WithItsWholeBody(bool isLengthKnown)
    {
        var body = ToolCallBody(ScopedTools.WriteTool, padding: McpScopeChallenge.MaxToolCallBodyBytes);
        var context = CreateRequest(body, grantedScope: "mcp:read");
        context.Request.ContentLength = isLengthKnown ? Encoding.UTF8.GetByteCount(body) : null;

        var servedBody = await ServeAsync(context);

        Assert.Equal(body, servedBody);
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_ReadsACallUpToTheLimit()
    {
        var body = ToolCallBody(ScopedTools.WriteTool, padding: McpScopeChallenge.MaxToolCallBodyBytes - 200);
        var context = CreateRequest(body, grantedScope: "mcp:read");

        var isServed = await RunAsync(context);

        Assert.True(Encoding.UTF8.GetByteCount(body) <= McpScopeChallenge.MaxToolCallBodyBytes);
        Assert.False(isServed);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task ChallengeRefusedToolCalls_LeavesABodyThatIsNotJson_ToTheSdk(string? contentType)
    {
        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: "mcp:read");
        context.Request.ContentType = contentType;

        var isServed = await RunAsync(context);

        Assert.True(isServed);
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_ReadsTheBodyInTheCharsetItsContentTypeNames()
    {
        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: "mcp:read");
        context.Request.ContentType = "application/json; charset=utf-16";
        context.Request.Body = new MemoryStream(Encoding.Unicode.GetBytes(ToolCallBody(ScopedTools.WriteTool)));

        var isServed = await RunAsync(context);

        Assert.False(isServed);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_LeavesAGetRequest_ToTheSdk()
    {
        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: "mcp:read");
        context.Request.Method = HttpMethods.Get;

        var isServed = await RunAsync(context);

        Assert.True(isServed);
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_LeavesTheCall_ToTheToolsOwnCheck_WhenScopeValidationIsOff()
    {
        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: "mcp:read");

        var isServed = await RunAsync(context, options => options.ClaimToScopeMapper = null);

        Assert.True(isServed);
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_LeavesTheCall_ToTheToolsOwnCheck_WhenAScopeItRequiresHasNoName()
    {
        // A challenge naming fewer scopes than the call needs would send the client to sign in for too little.
        var context = CreateToolCall(ScopedTools.UnnamedBitTool, grantedScope: "mcp:read");

        var isServed = await RunAsync(context);

        Assert.True(isServed);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(null, "/tenant")]
    [InlineData("/oauth/resource", "")]
    [InlineData("oauth/resource", "/tenant")]
    [InlineData("https://metadata.example.com/prm", "/tenant")]
    public async Task ChallengeRefusedToolCalls_PointsToTheMetadata_AsTheSdksHandlerDoesInItsOwnChallenge(string? configuredUri, string pathBase)
    {
        void Configure(McpAuthenticationOptions options)
        {
            if (configuredUri is not null)
            {
                options.ResourceMetadataUri = new Uri(configuredUri, UriKind.RelativeOrAbsolute);
            }
        }

        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: "mcp:read");
        context.Request.PathBase = pathBase;

        await RunAsync(context, configureAuthentication: Configure);
        var sdkChallenge = await GetSdkChallengeAsync(pathBase, Configure);

        Assert.Equal(GetResourceMetadata(sdkChallenge), GetResourceMetadata(context.Response.Headers.WWWAuthenticate.ToString()));
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_NamesTheScopesTheCallerHas_AlongWithTheOnesTheToolRequires()
    {
        // A 2025-11-25 client asks for the challenge's scopes alone, and would lose mcp:write otherwise.
        var context = CreateToolCall(ScopedTools.ReadDeleteTool, grantedScope: "mcp:write");

        await RunAsync(context);

        Assert.Contains("scope=\"mcp:read mcp:write mcp:delete\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_NamesAScopeNamedForSeveralFlags_Once()
    {
        var context = CreateToolCall(ScopedTools.ReadDeleteTool, grantedScope: "other");

        await RunAsync(context, options =>
        {
            options.ScopeNames[McpScope.Read] = "api";
            options.ScopeNames[McpScope.Write] = "api";
            options.ScopeNames[McpScope.Delete] = "api";
        });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("scope=\"api\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task ChallengeRefusedToolCalls_LeavesOutOfTheErrorDescription_TheCharactersRfc6750DoesNotAllow()
    {
        var context = CreateToolCall(ScopedTools.WriteTool, grantedScope: null);

        await RunAsync(context, options => options.ClaimName = "my\"cl\\aimé");

        Assert.EndsWith("error_description=\"Claim 'myclaim' not found. Required scope: Write\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    /// <summary>
    /// Runs the request through the challenge; whether it reached the endpoint behind it.
    /// </summary>
    private async Task<bool> RunAsync(
        HttpContext context,
        Action<McpScopeOptions>? configureScopes = null,
        Action<McpAuthenticationOptions>? configureAuthentication = null)
    {
        var isServed = false;
        var challenge = CreateChallenge(configureScopes, configureAuthentication);

        await challenge.ChallengeRefusedToolCalls(_ =>
        {
            isServed = true;
            return Task.CompletedTask;
        })(context);

        return isServed;
    }

    /// <summary>
    /// The <c>WWW-Authenticate</c> header of the MCP SDK's own <c>401</c> challenge for a request to the MCP endpoint.
    /// </summary>
    private static async Task<string> GetSdkChallengeAsync(string pathBase, Action<McpAuthenticationOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddMcp(configure);
        await using var provider = services.BuildServiceProvider();
        var context = CreateToolCall(ScopedTools.WriteTool);
        context.Request.PathBase = pathBase;
        context.RequestServices = provider;

        await context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        return context.Response.Headers.WWWAuthenticate.ToString();
    }

    private static string GetResourceMetadata(string challenge)
    {
        var match = Regex.Match(challenge, "resource_metadata=\"([^\"]*)\"");
        Assert.True(match.Success, challenge);
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Runs the request through the challenge to an endpoint that reads the body, as the SDK does; the body it read.
    /// </summary>
    private async Task<string> ServeAsync(HttpContext context)
    {
        string? servedBody = null;
        var challenge = CreateChallenge(configureScopes: null, configureAuthentication: null);

        await challenge.ChallengeRefusedToolCalls(async x =>
        {
            using var reader = new StreamReader(x.Request.Body, Encoding.UTF8, leaveOpen: true);
            servedBody = await reader.ReadToEndAsync();
        })(context);

        Assert.NotNull(servedBody);
        return servedBody;
    }

    private McpScopeChallenge CreateChallenge(Action<McpScopeOptions>? configureScopes, Action<McpAuthenticationOptions>? configureAuthentication)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new ListLoggerProvider(_logs)));
        services.Configure<McpScopeOptions>(options =>
        {
            options.ClaimToScopeMapper = ReadScopes;
            options.ScopeNames[McpScope.Read] = "mcp:read";
            options.ScopeNames[McpScope.Write] = "mcp:write";
            options.ScopeNames[McpScope.Delete] = "mcp:delete";
            options.ResourceMetadata = new ProtectedResourceMetadata { AuthorizationServers = ["https://login.example.com"] };
            configureScopes?.Invoke(options);
        });
        services.Configure<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme, options => configureAuthentication?.Invoke(options));
        foreach (var toolName in new[] { ScopedTools.ReadTool, ScopedTools.WriteTool, ScopedTools.ReadDeleteTool, ScopedTools.UnnamedBitTool, ScopedTools.UnscopedTool })
        {
            services.AddSingleton(ScopedTools.Create(toolName));
        }

        services.AddSingleton<McpToolScopes>();
        services.AddSingleton<McpScopeChallenge>();

        return services.BuildServiceProvider().GetRequiredService<McpScopeChallenge>();
    }

    private static McpScope ReadScopes(string claimValue)
    {
        return claimValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(McpScope.None, (granted, scope) => granted | scope switch
            {
                "mcp:read" => McpScope.Read,
                "mcp:write" => McpScope.Write,
                "mcp:delete" => McpScope.Delete,
                _ => McpScope.None
            });
    }

    private static DefaultHttpContext CreateToolCall(string toolName, string? grantedScope = "")
    {
        return CreateRequest(ToolCallBody(toolName), grantedScope);
    }

    /// <summary>
    /// A <c>tools/call</c> request, with an argument of <paramref name="padding"/> characters to make it large.
    /// </summary>
    private static string ToolCallBody(string toolName, int padding = 0)
    {
        var arguments = padding > 0 ? $"{{\"note\":\"{new string('x', padding)}\"}}" : "{}";
        return $"{{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{{\"name\":\"{toolName}\",\"arguments\":{arguments}}}}}";
    }

    /// <summary>
    /// A POST to the MCP endpoint by a signed-in caller whose <c>scope</c> claim is <paramref name="grantedScope"/>, or
    /// who has no such claim when it is <see langword="null"/>.
    /// </summary>
    private static DefaultHttpContext CreateRequest(string body, string? grantedScope)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("mcp.example.com");
        context.Request.Path = "/mcp";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var claims = grantedScope is null ? [] : new[] { new Claim("scope", grantedScope) };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));

        return context;
    }
}
