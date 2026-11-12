using System.Security.Claims;
using System.Text.Encodings.Web;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
using Api.ToMcp.Tests.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AddMcpTools_RegistersNothingThatADevelopmentHostCannotCreate(bool hasAuthentication, bool isSignInDiscoverable)
    {
        // In Development the host validates every registration when it builds its provider; an app without
        // AddAuthentication has no UrlEncoder, which the MCP SDK's authentication handler needs.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IParityService, ParityService>();
        if (hasAuthentication)
        {
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SignedInHandler>(AppScheme, null);
        }

        AddMcpTools(builder.Services, isSignInDiscoverable ? EnableSignInDiscovery : null);

        await using var app = builder.Build();

        Assert.True(app.Environment.IsDevelopment());
    }

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
    public void AddMcpTools_ListsAScopeNamedForSeveralFlags_Once()
    {
        using var provider = BuildProvider(options =>
        {
            EnableSignInDiscovery(options);
            options.ScopeNames[McpScope.Read] = "api";
            options.ScopeNames[McpScope.Write] = "api";
            options.ScopeNames[McpScope.Delete] = "api";
        }, toolNames: [ScopedTools.ReadTool, ScopedTools.WriteTool, ScopedTools.ReadDeleteTool]);

        var metadata = GetSchemeMetadata(provider);

        Assert.Equal(["api"], metadata.ScopesSupported);
    }

    [Fact]
    public async Task AddMcpTools_AddsNoScheme_WhenTheAppHasNoSchemeOfItsOwn_SoItCannotBecomeTheAppsDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IParityService, ParityService>();
        services.AddAuthentication();
        AddMcpTools(services, EnableSignInDiscovery);
        using var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var allSchemes = await schemes.GetAllSchemesAsync();
        var defaultScheme = await schemes.GetDefaultAuthenticateSchemeAsync();

        Assert.Empty(allSchemes);
        Assert.Null(defaultScheme);
    }

    [Fact]
    public async Task MapMcpEndpoint_SignsTheCallerInThroughTheSdksScheme_AndAppliesTheAppsPolicies()
    {
        var endpoints = await MapMcpEndpointAsync(EnableSignInDiscovery, allowAnonymous: false);

        Assert.All(endpoints, endpoint =>
        {
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.Equal([McpAuthenticationDefaults.AuthenticationScheme], policy.AuthenticationSchemes);
            Assert.Contains(policy.Requirements, x => x is DenyAnonymousAuthorizationRequirement);
            Assert.Contains(policy.Requirements, x => x is McpAppPoliciesRequirement);
            // RequireAuthorization adds a bare [Authorize] with the policy; it names no policy, role or scheme of its own.
            Assert.All(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), x => Assert.True(x.Policy is null && x.Roles is null && x.AuthenticationSchemes is null));
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        });
    }

    [Fact]
    public async Task MapMcpEndpoint_SignsTheCallerInThroughTheSdksScheme_AndLetsAnonymousCallersThrough_WhenAllowAnonymousIsOn()
    {
        var endpoints = await MapMcpEndpointAsync(EnableSignInDiscovery, allowAnonymous: true);

        Assert.All(endpoints, endpoint =>
        {
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.Equal([McpAuthenticationDefaults.AuthenticationScheme], policy.AuthenticationSchemes);
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapMcpEndpoint_AddsNoAuthorization_WithoutResourceMetadata(bool allowAnonymous)
    {
        // Without authorization metadata of its own, the endpoint is left to the fallback policy, as before.
        var endpoints = await MapMcpEndpointAsync(configureScopes: null, allowAnonymous);

        Assert.All(endpoints, endpoint =>
        {
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.Equal(allowAnonymous, endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>().Count > 0);
        });
    }

    [Fact]
    public async Task StartupCheck_ReportsAnError_WhenTheAppHasNoSchemeOfItsOwn()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new ListLoggerProvider(logs)));
        services.AddSingleton<IParityService, ParityService>();
        services.AddAuthentication();
        AddMcpTools(services, EnableSignInDiscovery);
        using var provider = services.BuildServiceProvider();

        await ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider).StartAsync(CancellationToken.None);

        var error = Assert.Single(logs, x => x.Level == LogLevel.Error);
        Assert.Contains("registered no authentication scheme", error.Message);
    }

    [Theory]
    [InlineData("Auth0", null, "'Bearer', which is not registered")]
    [InlineData(AppScheme, "", "ForwardAuthenticate of the MCP SDK's scheme 'McpAuth' is null")]
    [InlineData(AppScheme, "Missing", "'Missing', which is not registered")]
    public async Task StartupCheck_ReportsAnError_WhenTheSchemeTheSdksSchemeForwardsToIsMissing(string appScheme, string? forwardAuthenticate, string expectedText)
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new ListLoggerProvider(logs)));
        services.AddSingleton<IParityService, ParityService>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SignedInHandler>(appScheme, null);
        AddMcpTools(services, EnableSignInDiscovery);
        if (forwardAuthenticate is not null)
        {
            services.Configure<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme, options =>
                options.ForwardAuthenticate = forwardAuthenticate.Length == 0 ? null : forwardAuthenticate);
        }

        using var provider = services.BuildServiceProvider();

        await ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider).StartAsync(CancellationToken.None);

        var error = Assert.Single(logs, x => x.Level == LogLevel.Error);
        Assert.Contains(expectedText, error.Message);
    }

    [Fact]
    public async Task StartupCheck_ReportsNoError_WhenTheSchemeTheSdksSchemeForwardsToIsRegistered()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(EnableSignInDiscovery, logs: logs);

        await ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider).StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logs, x => x.Level == LogLevel.Error);
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

    private static async Task<List<Endpoint>> MapMcpEndpointAsync(Action<McpScopeOptions>? configureScopes, bool allowAnonymous)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IParityService, ParityService>();
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SignedInHandler>(AppScheme, null);
        builder.Services.AddAuthorization();
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
