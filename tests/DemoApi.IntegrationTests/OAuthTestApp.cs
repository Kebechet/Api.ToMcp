using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime;
using Api.ToMcp.Tests.Shared;
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

namespace DemoApi.IntegrationTests;

/// <summary>
/// An app on Kestrel with the demo's generated tools, for the OAuth sign-in discovery tests. Unless a test sets up its own,
/// authentication is a scheme named <c>Bearer</c> that reads fixed tokens. The demo's tools require Read, Write and Delete.
/// The invoker's HTTP client refuses every request, so a tool call that succeeds was served in process.
/// </summary>
internal static class OAuthTestApp
{
    /// <summary>
    /// Grants <c>mcp:read</c>.
    /// </summary>
    public const string ReadToken = "Bearer read-token";

    /// <summary>
    /// Grants <c>mcp:read mcp:write</c>.
    /// </summary>
    public const string WriteToken = "Bearer write-token";

    /// <summary>
    /// Grants <c>mcp:read</c> to a user in <see cref="MemberRole"/>.
    /// </summary>
    public const string MemberToken = "Bearer member-token";

    public const string MemberRole = "mcp-user";
    public const string AuthorizationServer = "https://login.example.com";
    public const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";

    /// <summary>
    /// Seeded in ProductsController.
    /// </summary>
    public const string LaptopId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    public const string ToolsList = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    /// <param name="isSignInDiscoverable">Whether <c>ResourceMetadata</c> is set.</param>
    /// <param name="allowAnonymous">Passed to <c>MapMcpEndpoint</c>.</param>
    /// <param name="configureAuthentication">The app's authentication; a <c>Bearer</c> scheme of <see cref="TokenHandler"/> when null.</param>
    /// <param name="configureAuthorization">The app's authorization options.</param>
    /// <param name="configureServices">Anything else, registered after <c>AddMcpTools</c>.</param>
    /// <param name="pathBase">A path base the app takes with <c>UsePathBase</c>.</param>
    /// <param name="logs">Collects the app's log entries.</param>
    public static async Task<WebApplication> StartAsync(
        bool isSignInDiscoverable = true,
        bool allowAnonymous = false,
        Action<IServiceCollection>? configureAuthentication = null,
        Action<AuthorizationOptions>? configureAuthorization = null,
        Action<IServiceCollection>? configureServices = null,
        string? pathBase = null,
        List<(LogLevel Level, string Message)>? logs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(new ListLoggerProvider(logs));
        }

        builder.Services.AddControllers().AddApplicationPart(typeof(Program).Assembly);
        if (configureAuthentication is not null)
        {
            configureAuthentication(builder.Services);
        }
        else
        {
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TokenHandler>(TokenHandler.SchemeName, null);
        }

        builder.Services.AddAuthorization(options => configureAuthorization?.Invoke(options));
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
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        if (pathBase is not null)
        {
            app.UsePathBase(pathBase);
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMcpLoopPrevention();
        app.MapControllers();
        app.MapMcpEndpoint("mcp", allowAnonymous);
        await app.StartAsync();

        return app;
    }

    public static async Task<McpClient> ConnectAsync(string address, string? authorization)
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
    public static Task<HttpResponseMessage> PostToolCallAsync(string mcpUrl, string toolName, string? authorization, string arguments = "{}")
    {
        return PostAsync(mcpUrl, ToolCall(toolName, arguments), authorization);
    }

    public static string ToolCall(string toolName, string arguments = "{}")
    {
        return $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{toolName}\",\"arguments\":{arguments}}}}}";
    }

    /// <param name="mcpUrl">The MCP endpoint's URL.</param>
    /// <param name="body">The JSON-RPC message.</param>
    /// <param name="authorization">The Authorization header, if any.</param>
    /// <param name="content">The body as it is sent; UTF-8 with its length when null.</param>
    /// <param name="headers">Other request headers.</param>
    public static async Task<HttpResponseMessage> PostAsync(
        string mcpUrl,
        string body,
        string? authorization,
        HttpContent? content = null,
        IDictionary<string, string>? headers = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mcpUrl);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Content = content ?? new StringContent(body, Encoding.UTF8, "application/json");

        return await client.SendAsync(request);
    }

    /// <summary>
    /// The <c>resource_metadata</c> of a <c>WWW-Authenticate</c> header.
    /// </summary>
    public static string GetResourceMetadata(HttpHeaderValueCollection<AuthenticationHeaderValue> challenges)
    {
        var match = Regex.Match(challenges.ToString(), "resource_metadata=\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"The tool sent {request.RequestUri} over HTTP.");
        }
    }
}

/// <summary>
/// Stands in for JWT bearer authentication, under the scheme name the MCP SDK's scheme forwards to unless a test
/// registers it under another: it reads the fixed tokens of <see cref="OAuthTestApp"/>.
/// </summary>
internal sealed class TokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";

    public TokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers.Authorization.ToString();
        var scope = token switch
        {
            OAuthTestApp.ReadToken or OAuthTestApp.MemberToken => "mcp:read",
            OAuthTestApp.WriteToken => "mcp:read mcp:write",
            _ => null
        };
        if (scope is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("sub", "alice-id"), new Claim("scope", scope)], Scheme.Name);
        if (token == OAuthTestApp.MemberToken)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, OAuthTestApp.MemberRole));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

/// <summary>
/// A second scheme that signs a caller in by the <see cref="HeaderName"/> header, as an API key or a client certificate
/// would, and never by a bearer token.
/// </summary>
internal sealed class ApiKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string Key = "demo-key";

    public ApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers[HeaderName].ToString() != Key)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "api-key")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
