using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// OAuth sign-in discovery on Kestrel, with the demo's generated tools and an authentication scheme named <c>Bearer</c>
/// that reads two fixed tokens: one that grants <c>mcp:read</c>, one that grants <c>mcp:read mcp:write</c>. The demo's
/// tools require Read, Write and Delete. The invoker's HTTP client refuses every request, so a tool call that succeeds
/// was served in process.
/// </summary>
public sealed class OAuthSignInDiscoveryTests
{
    private const string ReadToken = "Bearer read-token";
    private const string WriteToken = "Bearer write-token";

    /// <summary>
    /// Grants <c>mcp:read</c> to a user in <see cref="MemberRole"/>, the role the fallback policy of some tests requires.
    /// </summary>
    private const string MemberToken = "Bearer member-token";
    private const string MemberRole = "mcp-user";
    private const string AuthorizationServer = "https://login.example.com";
    private const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";

    // Seeded in ProductsController.
    private const string LaptopId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    [Fact]
    public async Task ARequestWithoutAToken_GetsA401_ThatPointsToTheMetadata()
    {
        await using var app = await StartAppAsync();
        var address = app.Urls.Single();

        using var response = await PostToolCallAsync(address, "Products_GetAll", authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{address}{MetadataPath}\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task TheMetadata_NamesTheResource_TheAuthorizationServer_AndTheScopesTheToolsRequire()
    {
        await using var app = await StartAppAsync();
        var address = app.Urls.Single();
        using var client = new HttpClient();

        using var response = await client.GetAsync(address + MetadataPath);
        var metadata = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(address + "/mcp", metadata["resource"]!.GetValue<string>());
        Assert.Equal([AuthorizationServer], metadata["authorization_servers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["mcp:read", "mcp:write", "mcp:delete"], metadata["scopes_supported"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("Demo API", metadata["resource_name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ReadToken)]
    [InlineData(WriteToken)]
    public async Task ASignedInToolCall_IsServedInProcess(string token)
    {
        await using var app = await StartAppAsync();
        await using var client = await ConnectAsync(app.Urls.Single(), token);

        var result = await client.CallToolAsync("Products_GetById", new Dictionary<string, object?> { ["id"] = LaptopId });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task AToolCallWithTooLittleScope_GetsA403InsufficientScope_NamingTheScopesItRequiresAndTheCallerHas()
    {
        await using var app = await StartAppAsync();
        var address = app.Urls.Single();

        using var response = await PostToolCallAsync(address, "Weather_Create", ReadToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"mcp:read mcp:write\", resource_metadata=\"{address}{MetadataPath}\", error_description=\"Insufficient scope. Required: Write, Granted: Read\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AToolCallWithTheScopeItRequires_IsServed()
    {
        await using var app = await StartAppAsync();
        await using var client = await ConnectAsync(app.Urls.Single(), WriteToken);

        var result = await client.CallToolAsync(
            "Weather_Create",
            new Dictionary<string, object?> { ["request"] = new JsonObject { ["city"] = "Brno", ["date"] = "2026-11-12T00:00:00", ["temperatureC"] = 21, ["summary"] = "Mild" } });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Brno", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task WithAnonymousAccess_AClientListsTheToolsBeforeItSignsIn_AndIsAskedToSignInWhenItCallsOne()
    {
        await using var app = await StartAppAsync(allowAnonymous: true);
        var address = app.Urls.Single();
        await using var client = await ConnectAsync(address, authorization: null);

        var tools = await client.ListToolsAsync();
        using var response = await PostToolCallAsync(address, "Products_GetById", authorization: null);

        Assert.Contains(tools, x => x.Name == "Products_GetById");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{address}{MetadataPath}\", scope=\"mcp:read\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAppsFallbackPolicy_StillRefusesASignedInCallerItDoesNotAllow(bool isSignInDiscoverable)
    {
        await using var app = await StartAppAsync(isSignInDiscoverable, requiresMemberRole: true);

        using var response = await PostToolCallAsync(app.Urls.Single(), "Products_GetAll", ReadToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAppsFallbackPolicy_LetsInACallerItAllows(bool isSignInDiscoverable)
    {
        await using var app = await StartAppAsync(isSignInDiscoverable, requiresMemberRole: true);
        await using var client = await ConnectAsync(app.Urls.Single(), MemberToken);

        var result = await client.CallToolAsync("Products_GetById", new Dictionary<string, object?> { ["id"] = LaptopId });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task WithTheAppsFallbackPolicy_ARequestWithoutAToken_StillGetsTheChallengeThatPointsToTheMetadata()
    {
        await using var app = await StartAppAsync(requiresMemberRole: true);
        var address = app.Urls.Single();

        using var response = await PostToolCallAsync(address, "Products_GetAll", authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{address}{MetadataPath}\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task WithoutResourceMetadata_TheEndpointServesAnyone_TheMetadataIsNotFound_AndARefusedScopeIsAnErrorResult()
    {
        await using var app = await StartAppAsync(isSignInDiscoverable: false);
        var address = app.Urls.Single();
        using var httpClient = new HttpClient();
        await using var anonymousClient = await ConnectAsync(address, authorization: null);
        await using var readClient = await ConnectAsync(address, ReadToken);

        var tools = await anonymousClient.ListToolsAsync();
        using var metadataResponse = await httpClient.GetAsync(address + MetadataPath);
        var result = await readClient.CallToolAsync(
            "Weather_Create",
            new Dictionary<string, object?> { ["request"] = new JsonObject { ["city"] = "Brno", ["date"] = "2026-11-12T00:00:00", ["temperatureC"] = 21 } });

        Assert.NotEmpty(tools);
        Assert.Equal(HttpStatusCode.NotFound, metadataResponse.StatusCode);
        Assert.True(result.IsError);
        Assert.Equal("Insufficient scope. Required: Write, Granted: Read", ((TextContentBlock)result.Content[0]).Text);
    }

    private static async Task<WebApplication> StartAppAsync(bool isSignInDiscoverable = true, bool allowAnonymous = false, bool requiresMemberRole = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(Program).Assembly);
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization(options =>
        {
            if (requiresMemberRole)
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(MemberRole).Build();
            }
        });
        builder.Services.AddMcpTools(typeof(Program).Assembly, options =>
        {
            options.ArePropertyDescriptionsFromXmlDocs = false;
            options.ScopeNames[McpScope.Read] = "mcp:read";
            options.ScopeNames[McpScope.Write] = "mcp:write";
            options.ScopeNames[McpScope.Delete] = "mcp:delete";
            options.ClaimToScopeMapper = claimValue => claimValue
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Aggregate(McpScope.None, (granted, scope) => granted | options.ScopeNames.FirstOrDefault(x => x.Value == scope).Key);
            if (isSignInDiscoverable)
            {
                options.ResourceMetadata = new ProtectedResourceMetadata
                {
                    AuthorizationServers = [AuthorizationServer],
                    ResourceName = "Demo API"
                };
            }
        });
        builder.Services.AddHttpClient(nameof(IMcpHttpInvoker)).ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMcpLoopPrevention();
        app.MapControllers();
        app.MapMcpEndpoint("mcp", allowAnonymous);
        await app.StartAsync();

        return app;
    }

    private static async Task<McpClient> ConnectAsync(string address, string? authorization)
    {
        var headers = new Dictionary<string, string>();
        if (authorization is not null)
        {
            headers["Authorization"] = authorization;
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(address + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers
            },
            NullLoggerFactory.Instance);

        return await McpClient.CreateAsync(transport);
    }

    /// <summary>
    /// A <c>tools/call</c> request as an MCP client sends it, for a test that reads the HTTP response itself.
    /// </summary>
    private static async Task<HttpResponseMessage> PostToolCallAsync(string address, string toolName, string? authorization)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, address + "/mcp");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        request.Content = new StringContent(
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{toolName}\",\"arguments\":{{}}}}}}",
            Encoding.UTF8,
            "application/json");

        return await client.SendAsync(request);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"The tool sent {request.RequestUri} over HTTP.");
        }
    }

    /// <summary>
    /// Stands in for JWT bearer authentication under its scheme name, which the MCP SDK's scheme forwards to.
    /// </summary>
    private sealed class TokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Bearer";

        public TokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString();
            var scope = token switch
            {
                ReadToken or MemberToken => "mcp:read",
                WriteToken => "mcp:read mcp:write",
                _ => null
            };
            if (scope is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("scope", scope)], SchemeName);
            if (token == MemberToken)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, MemberRole));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
