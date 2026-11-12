using System.Security.Claims;
using System.Text.Encodings.Web;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
using Api.ToMcp.Tests.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Extensions;

/// <summary>
/// What <c>AddMcpTools</c> and <c>MapMcpEndpoint</c> set up for OAuth sign-in discovery, and that they set up nothing
/// while <see cref="McpScopeOptions.ResourceMetadata"/> is unset.
/// </summary>
public class McpSignInDiscoveryTests
{
    private const string AppScheme = "Bearer";
    private const string AuthorizationServer = "https://login.example.com";

    [Fact]
    public async Task AddMcpTools_AddsNoScheme_WithoutResourceMetadata()
    {
        using var provider = BuildProvider(configureScopes: null);
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var allSchemes = await schemes.GetAllSchemesAsync();
        var defaultScheme = await schemes.GetDefaultAuthenticateSchemeAsync();

        Assert.Equal([AppScheme], allSchemes.Select(x => x.Name));
        Assert.Equal(AppScheme, defaultScheme?.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddMcpTools_AddsTheSdksScheme_AndKeepsTheAppsOnlySchemeAsItsDefault(bool isAddedBeforeAuthentication)
    {
        using var provider = BuildProvider(EnableSignInDiscovery, isAddedBeforeAuthentication: isAddedBeforeAuthentication);
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var allSchemes = await schemes.GetAllSchemesAsync();
        var defaultAuthenticateScheme = await schemes.GetDefaultAuthenticateSchemeAsync();
        var defaultChallengeScheme = await schemes.GetDefaultChallengeSchemeAsync();

        Assert.Equal([AppScheme, McpAuthenticationDefaults.AuthenticationScheme], allSchemes.Select(x => x.Name));
        Assert.Equal(typeof(McpAuthenticationHandler), allSchemes.Last().HandlerType);
        Assert.Equal(AppScheme, defaultAuthenticateScheme?.Name);
        Assert.Equal(AppScheme, defaultChallengeScheme?.Name);
    }

    [Fact]
    public async Task AddMcpTools_LeavesTheAppsOwnDefaultScheme_AsItIs()
    {
        using var provider = BuildProvider(EnableSignInDiscovery, defaultScheme: "Other");
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var defaultScheme = await schemes.GetDefaultAuthenticateSchemeAsync();

        Assert.Equal("Other", defaultScheme?.Name);
    }

    [Fact]
    public void AddMcpTools_HandsTheSdksSchemeTheMetadata_ListingTheNamedScopesTheToolsRequire()
    {
        // The tools require Read and Write; Delete has a name but no tool requires it.
        using var provider = BuildProvider(EnableSignInDiscovery, toolNames: [ScopedTools.ReadTool, ScopedTools.WriteTool, ScopedTools.UnscopedTool]);
        var appMetadata = provider.GetRequiredService<IOptions<McpScopeOptions>>().Value.ResourceMetadata!;

        var metadata = GetSchemeMetadata(provider);

        Assert.Equal(["mcp:read", "mcp:write"], metadata.ScopesSupported);
        Assert.Equal([AuthorizationServer], metadata.AuthorizationServers);
        Assert.Equal("Demo", metadata.ResourceName);
        Assert.Null(metadata.Resource);
        Assert.NotSame(appMetadata, metadata);
        Assert.Empty(appMetadata.ScopesSupported);
    }

    [Fact]
    public void AddMcpTools_KeepsTheScopesTheAppLists()
    {
        using var provider = BuildProvider(options =>
        {
            EnableSignInDiscovery(options);
            options.ResourceMetadata!.ScopesSupported = ["api"];
        });

        var metadata = GetSchemeMetadata(provider);

        Assert.Equal(["api"], metadata.ScopesSupported);
    }

    [Fact]
    public async Task MapMcpEndpoint_RequiresASignedInCaller_ThroughTheSdksScheme()
    {
        var endpoints = await MapMcpEndpointAsync(EnableSignInDiscovery, allowAnonymous: false);

        Assert.All(endpoints, endpoint =>
        {
            var authorizeData = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(McpAuthenticationDefaults.AuthenticationScheme, authorizeData.AuthenticationSchemes);
            Assert.Null(authorizeData.Policy);
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        });
    }

    [Fact]
    public async Task MapMcpEndpoint_KeepsRequiringTheAppsFallbackPolicy_WhichItsOwnRequirementWouldEnd()
    {
        var fallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole("mcp-user").Build();

        var endpoints = await MapMcpEndpointAsync(EnableSignInDiscovery, allowAnonymous: false, fallbackPolicy);

        Assert.All(endpoints, endpoint =>
        {
            Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Same(fallbackPolicy, Assert.Single(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapMcpEndpoint_LeavesTheEndpointAnonymous_WhenAllowAnonymousIsOn(bool hasFallbackPolicy)
    {
        var fallbackPolicy = hasFallbackPolicy ? new AuthorizationPolicyBuilder().RequireRole("mcp-user").Build() : null;

        var endpoints = await MapMcpEndpointAsync(EnableSignInDiscovery, allowAnonymous: true, fallbackPolicy);

        Assert.All(endpoints, endpoint =>
        {
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapMcpEndpoint_AddsNoAuthorization_WithoutResourceMetadata(bool hasFallbackPolicy)
    {
        // Without authorization metadata of its own, the endpoint is left to the fallback policy, as before.
        var fallbackPolicy = hasFallbackPolicy ? new AuthorizationPolicyBuilder().RequireRole("mcp-user").Build() : null;

        var endpoints = await MapMcpEndpointAsync(configureScopes: null, allowAnonymous: false, fallbackPolicy);

        Assert.All(endpoints, endpoint =>
        {
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        });
    }

    [Fact]
    public async Task StartupCheck_Warns_WhenTheMetadataListsNoAuthorizationServer()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(options =>
        {
            EnableSignInDiscovery(options);
            options.ResourceMetadata!.AuthorizationServers.Clear();
        }, logs: logs);

        await ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider).StartAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning);
        Assert.Contains("ResourceMetadata.AuthorizationServers", warning.Message);
    }

    [Fact]
    public async Task StartupCheck_Warns_WhenAScopeTheToolsRequireHasNoName()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(options =>
        {
            EnableSignInDiscovery(options);
            options.ScopeNames.Remove(McpScope.Write);
        }, toolNames: [ScopedTools.ReadTool, ScopedTools.WriteTool], logs: logs);

        await ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider).StartAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning);
        Assert.Contains("require the scope Write", warning.Message);
        Assert.Contains("McpScopeOptions.ScopeNames", warning.Message);
    }

    [Fact]
    public async Task StartupCheck_StaysSilent_WhenTheAppListsTheScopes_AndScopeValidationIsOff()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(options =>
        {
            EnableSignInDiscovery(options);
            options.ScopeNames.Clear();
            options.ResourceMetadata!.ScopesSupported = ["api"];
        }, toolNames: [ScopedTools.ReadTool], logs: logs);

        await ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider).StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logs, x => x.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartupCheck_StaysSilent_WhenEveryScopeTheToolsRequireHasAName()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(options =>
        {
            EnableSignInDiscovery(options);
            options.ClaimToScopeMapper = _ => McpScope.All;
        }, toolNames: [ScopedTools.ReadTool, ScopedTools.WriteTool], logs: logs);

        await ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider).StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logs, x => x.Level == LogLevel.Warning);
    }

    private static void EnableSignInDiscovery(McpScopeOptions options)
    {
        options.ArePropertyDescriptionsFromXmlDocs = false;
        options.ScopeNames[McpScope.Read] = "mcp:read";
        options.ScopeNames[McpScope.Write] = "mcp:write";
        options.ScopeNames[McpScope.Delete] = "mcp:delete";
        options.ResourceMetadata = new ProtectedResourceMetadata
        {
            AuthorizationServers = [AuthorizationServer],
            ResourceName = "Demo"
        };
    }

    private static ProtectedResourceMetadata GetSchemeMetadata(IServiceProvider provider)
    {
        return provider.GetRequiredService<IOptionsMonitor<McpAuthenticationOptions>>()
            .Get(McpAuthenticationDefaults.AuthenticationScheme)
            .ResourceMetadata!;
    }

    /// <summary>
    /// An app with one authentication scheme of its own, <see cref="AppScheme"/>, and the scoped tools named.
    /// </summary>
    private static ServiceProvider BuildProvider(
        Action<McpScopeOptions>? configureScopes,
        string[]? toolNames = null,
        bool isAddedBeforeAuthentication = false,
        string? defaultScheme = null,
        List<(LogLevel Level, string Message)>? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.AddProvider(new ListLoggerProvider(logs));
            }
        });
        services.AddSingleton<IParityService, ParityService>();
        if (isAddedBeforeAuthentication)
        {
            AddMcpTools(services, configureScopes);
        }

        var authentication = defaultScheme is null ? services.AddAuthentication() : services.AddAuthentication(defaultScheme);
        authentication.AddScheme<AuthenticationSchemeOptions, SignedInHandler>(AppScheme, null);
        if (defaultScheme is not null)
        {
            authentication.AddScheme<AuthenticationSchemeOptions, SignedInHandler>(defaultScheme, null);
        }

        if (!isAddedBeforeAuthentication)
        {
            AddMcpTools(services, configureScopes);
        }

        foreach (var toolName in toolNames ?? [])
        {
            services.AddSingleton(ScopedTools.Create(toolName));
        }

        return services.BuildServiceProvider();
    }

    private static void AddMcpTools(IServiceCollection services, Action<McpScopeOptions>? configureScopes)
    {
        services.AddMcpTools(typeof(ScopedTools).Assembly, options =>
        {
            options.ArePropertyDescriptionsFromXmlDocs = false;
            configureScopes?.Invoke(options);
        });
    }

    private static async Task<List<Endpoint>> MapMcpEndpointAsync(
        Action<McpScopeOptions>? configureScopes,
        bool allowAnonymous,
        AuthorizationPolicy? fallbackPolicy = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IParityService, ParityService>();
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SignedInHandler>(AppScheme, null);
        builder.Services.AddAuthorization(options => options.FallbackPolicy = fallbackPolicy);
        AddMcpTools(builder.Services, configureScopes);
        await using var app = builder.Build();

        app.MapMcpEndpoint(allowAnonymous: allowAnonymous);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).ToList();
        Assert.NotEmpty(endpoints);
        return endpoints;
    }

    private sealed class SignedInHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public SignedInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
