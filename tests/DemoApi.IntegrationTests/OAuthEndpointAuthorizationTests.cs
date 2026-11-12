using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using Xunit;
using static DemoApi.IntegrationTests.OAuthTestApp;

namespace DemoApi.IntegrationTests;

/// <summary>
/// Who the MCP endpoint lets in with OAuth sign-in discovery on: never a caller the app's own authorization refuses, never
/// a caller who is not signed in unless the endpoint allows anonymous callers, and always through the bearer token. See
/// <see cref="OAuthTestApp"/> for the app.
/// </summary>
public sealed class OAuthEndpointAuthorizationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAppsFallbackPolicy_StillRefusesASignedInCallerItDoesNotAllow(bool isSignInDiscoverable)
    {
        await using var app = await StartAsync(isSignInDiscoverable, configureAuthorization: RequireMemberRoleByFallback);

        using var response = await PostAsync(app.Urls.Single() + "/mcp", ToolsList, ReadToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAppsFallbackPolicy_LetsInACallerItAllows(bool isSignInDiscoverable)
    {
        await using var app = await StartAsync(isSignInDiscoverable, configureAuthorization: RequireMemberRoleByFallback);
        await using var client = await ConnectAsync(app.Urls.Single(), MemberToken);

        var result = await client.CallToolAsync("Products_GetById", new Dictionary<string, object?> { ["id"] = LaptopId });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task ADefaultPolicyStricterThanTheFallback_IsRequiredToo()
    {
        await using var app = await StartAsync(configureAuthorization: options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(MemberRole).Build();
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        var mcpUrl = app.Urls.Single() + "/mcp";

        using var refused = await PostAsync(mcpUrl, ToolsList, ReadToken);
        using var allowed = await PostAsync(mcpUrl, ToolsList, MemberToken);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task TheFallbackPolicyOfACustomPolicyProvider_IsAskedForOnEachRequest()
    {
        var policyProvider = new SwitchableFallbackProvider();
        await using var app = await StartAsync(configureServices: services => services.AddSingleton<IAuthorizationPolicyProvider>(policyProvider));
        var mcpUrl = app.Urls.Single() + "/mcp";

        using var beforeSwitch = await PostAsync(mcpUrl, ToolsList, ReadToken);
        policyProvider.RequiresMemberRole = true;
        using var afterSwitch = await PostAsync(mcpUrl, ToolsList, ReadToken);
        using var member = await PostAsync(mcpUrl, ToolsList, MemberToken);

        Assert.Equal(HttpStatusCode.OK, beforeSwitch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, afterSwitch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFallbackPolicyTiedToItsOwnScheme_IsMetOnlyThroughThatScheme(bool isSignInDiscoverable)
    {
        await using var app = await StartAsync(
            isSignInDiscoverable,
            configureAuthentication: AddBearerAndApiKey,
            configureAuthorization: options => options.FallbackPolicy = new AuthorizationPolicyBuilder(ApiKeyHandler.SchemeName).RequireAuthenticatedUser().Build());
        var mcpUrl = app.Urls.Single() + "/mcp";
        var apiKey = new Dictionary<string, string> { [ApiKeyHandler.HeaderName] = ApiKeyHandler.Key };

        using var tokenOnly = await PostAsync(mcpUrl, ToolsList, ReadToken);
        using var tokenAndKey = await PostAsync(mcpUrl, ToolsList, ReadToken, headers: apiKey);

        Assert.True(
            tokenOnly.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A bearer token alone got {(int)tokenOnly.StatusCode}.");
        Assert.Equal(HttpStatusCode.OK, tokenAndKey.StatusCode);
    }

    [Fact]
    public async Task PoliciesThatAllowAnonymousCallers_DoNotTurnSignInOff()
    {
        await using var app = await StartAsync(configureAuthorization: options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
        });
        var address = app.Urls.Single();

        using var response = await PostAsync(address + "/mcp", ToolsList, authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{address}{MetadataPath}\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ACallerWhoIsNotSignedIn_GetsTheSdksChallenge_WhenThePoliciesNameTheirOwnSchemes()
    {
        await using var app = await StartAsync(
            configureAuthentication: AddBearerAndApiKey,
            configureAuthorization: options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder(TokenHandler.SchemeName).RequireAuthenticatedUser().Build();
                options.FallbackPolicy = new AuthorizationPolicyBuilder(ApiKeyHandler.SchemeName).RequireAuthenticatedUser().Build();
            });
        var address = app.Urls.Single();

        using var response = await PostAsync(address + "/mcp", ToolsList, authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains($"resource_metadata=\"{address}{MetadataPath}\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAppWhoseDefaultSchemeIsNotTheBearerScheme_StillReadsTheBearerToken(bool allowAnonymous)
    {
        await using var app = await StartAsync(allowAnonymous: allowAnonymous, configureAuthentication: services => AddBearerAndApiKey(services, ApiKeyHandler.SchemeName));

        using var response = await PostToolCallAsync(app.Urls.Single() + "/mcp", "Products_GetAll", ReadToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Contains("Laptop", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithAnonymousAccess_ACallerWithoutAToken_IsNotChallengedForAToolList()
    {
        await using var app = await StartAsync(allowAnonymous: true, configureAuthentication: services => AddBearerAndApiKey(services, ApiKeyHandler.SchemeName));

        using var response = await PostAsync(app.Urls.Single() + "/mcp", ToolsList, authorization: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task WithoutTheSchemeTheSdksSchemeForwardsTo_TheStartupLogSaysWhich_AndOnlyTheMcpEndpointFails()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        await using var app = await StartAsync(
            configureAuthentication: services => services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TokenHandler>("Auth0", null),
            logs: logs);
        var address = app.Urls.Single();
        using var client = new HttpClient();

        using var api = await client.GetAsync(address + "/api/products");
        using var mcp = await PostAsync(address + "/mcp", ToolsList, ReadToken);

        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, mcp.StatusCode);
        string error;
        lock (logs)
        {
            error = Assert.Single(logs, x => x.Level == LogLevel.Error && x.Message.Contains("ForwardAuthenticate")).Message;
        }

        Assert.Contains("'Bearer'", error);
    }

    [Fact]
    public async Task PointingForwardAuthenticateToTheAppsScheme_ReadsTheToken()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        await using var app = await StartAsync(
            configureAuthentication: services => services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TokenHandler>("Auth0", null),
            configureServices: services => services.Configure<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme, options => options.ForwardAuthenticate = "Auth0"),
            logs: logs);

        using var response = await PostAsync(app.Urls.Single() + "/mcp", ToolsList, ReadToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        lock (logs)
        {
            Assert.DoesNotContain(logs, x => x.Level >= LogLevel.Error);
        }
    }

    [Fact]
    public async Task WithoutAnySchemeOfItsOwn_TheAppStillServesItsApi_AndTheStartupLogSaysWhyTheMcpEndpointFails()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        await using var app = await StartAsync(configureAuthentication: services => services.AddAuthentication(), logs: logs);
        var address = app.Urls.Single();
        using var client = new HttpClient();

        using var api = await client.GetAsync(address + "/api/products");
        using var mcp = await PostAsync(address + "/mcp", ToolsList, ReadToken);

        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, mcp.StatusCode);
        lock (logs)
        {
            Assert.Contains(logs, x => x.Level == LogLevel.Error && x.Message.Contains("registered no authentication scheme"));
        }
    }

    [Theory]
    [InlineData(HttpServerSessionMode.Stateful, true, true)]
    [InlineData(HttpServerSessionMode.StatefulForInitializeClients, true, true)]
    [InlineData(HttpServerSessionMode.Stateless, true, false)]
    [InlineData(HttpServerSessionMode.Stateful, false, false)]
    public async Task MapMcpEndpoint_Warns_WhenAnonymousCallersMeetStatefulSessions(HttpServerSessionMode sessionMode, bool allowAnonymous, bool isWarned)
    {
        var logs = new List<(LogLevel Level, string Message)>();
        await using var app = await StartAsync(
            allowAnonymous: allowAnonymous,
            configureServices: services => services.Configure<HttpServerTransportOptions>(options => options.SessionMode = sessionMode),
            logs: logs);

        bool hasWarning;
        lock (logs)
        {
            hasWarning = logs.Any(x => x.Level == LogLevel.Warning && x.Message.Contains("ties a stateful session to the user who started it"));
        }

        Assert.Equal(isWarned, hasWarning);
    }

    private static void RequireMemberRoleByFallback(AuthorizationOptions options)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(MemberRole).Build();
    }

    /// <summary>
    /// The bearer scheme and an API key scheme, with <paramref name="defaultScheme"/> as the app's default.
    /// </summary>
    private static void AddBearerAndApiKey(IServiceCollection services, string? defaultScheme)
    {
        var authentication = defaultScheme is null ? services.AddAuthentication() : services.AddAuthentication(defaultScheme);
        authentication
            .AddScheme<AuthenticationSchemeOptions, TokenHandler>(TokenHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(ApiKeyHandler.SchemeName, null);
    }

    private static void AddBearerAndApiKey(IServiceCollection services)
    {
        AddBearerAndApiKey(services, defaultScheme: TokenHandler.SchemeName);
    }

    /// <summary>
    /// A policy provider whose fallback policy changes while the app runs, as one read from a database might.
    /// </summary>
    private sealed class SwitchableFallbackProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider _inner = new(Options.Create(new AuthorizationOptions()));

        public bool RequiresMemberRole { get; set; }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _inner.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => _inner.GetPolicyAsync(policyName);

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        {
            return Task.FromResult<AuthorizationPolicy?>(RequiresMemberRole
                ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(MemberRole).Build()
                : null);
        }
    }
}
