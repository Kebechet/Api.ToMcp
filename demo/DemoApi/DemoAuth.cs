using System.Security.Cryptography;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Authentication;

namespace DemoApi;

/// <summary>
/// An optional sign-in setup for trying OAuth sign-in discovery locally, off unless <c>DemoAuth:Enabled</c> is true. The
/// app validates JWT bearer tokens signed with a key it makes when it starts, and a development-only endpoint issues them
/// with the scopes asked for. That endpoint stands in for an authorization server and is not one: it signs no one in.
/// </summary>
public static class DemoAuth
{
    public const string EnabledKey = "DemoAuth:Enabled";
    public const string AllowAnonymousKey = "DemoAuth:AllowAnonymous";
    public const string TokenPath = "/dev/token";

    private const string AddressKey = "urls";
    private const char AddressSeparator = ';';
    private const char PathSeparator = '/';
    private const string DefaultAddress = "http://localhost:5000";
    private const string McpPath = "/mcp";
    private const string ScopeClaim = "scope";
    private const string SubjectClaim = "sub";
    private const string ScopeSeparator = " ";
    private const string ReadScope = "mcp:read";
    private const string WriteScope = "mcp:write";
    private const string DeleteScope = "mcp:delete";
    private const string DemoUser = "demo-user";
    private const string ResourceName = "Api.ToMcp demo";
    private const int KeyBytes = 32;

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(KeyBytes));

    public static bool IsEnabled(IConfiguration configuration)
    {
        return configuration.GetValue<bool>(EnabledKey);
    }

    public static bool AllowsAnonymousCallers(IConfiguration configuration)
    {
        return IsEnabled(configuration) && configuration.GetValue<bool>(AllowAnonymousKey);
    }

    /// <summary>
    /// JWT bearer authentication for tokens issued to the MCP endpoint, scope validation, and the protected resource
    /// metadata, which names the demo itself as the authorization server.
    /// </summary>
    public static void AddDemoAuth(this WebApplicationBuilder builder)
    {
        var address = GetAddress(builder.Configuration);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = address,
                    ValidAudience = address + McpPath,
                    IssuerSigningKey = SigningKey
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.Configure<McpScopeOptions>(options =>
        {
            options.ScopeNames[McpScope.Read] = ReadScope;
            options.ScopeNames[McpScope.Write] = WriteScope;
            options.ScopeNames[McpScope.Delete] = DeleteScope;
            options.ClaimName = ScopeClaim;
            options.ClaimToScopeMapper = claimValue => claimValue
                .Split(ScopeSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Aggregate(McpScope.None, (granted, scope) => granted | ToMcpScope(scope));
            options.ResourceMetadata = new ProtectedResourceMetadata
            {
                AuthorizationServers = [address],
                ResourceName = ResourceName
            };
        });
    }

    /// <summary>
    /// Authentication, authorization and, in Development, the endpoint that issues tokens:
    /// <c>POST /dev/token?scope=mcp:read%20mcp:write</c>.
    /// </summary>
    public static void UseDemoAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            var address = GetAddress(app.Configuration);
            app.MapPost(TokenPath, (string? scope) => IssueToken(address, scope ?? ReadScope))
                .AllowAnonymous()
                .ExcludeFromDescription();
        }
    }

    private static object IssueToken(string address, string scope)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = address,
            Audience = address + McpPath,
            Claims = new Dictionary<string, object> { [ScopeClaim] = scope, [SubjectClaim] = DemoUser },
            Expires = DateTime.UtcNow.Add(TokenLifetime),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });

        return new { access_token = token, token_type = JwtBearerDefaults.AuthenticationScheme, expires_in = (int)TokenLifetime.TotalSeconds, scope };
    }

    private static McpScope ToMcpScope(string scope)
    {
        return scope switch
        {
            ReadScope => McpScope.Read,
            WriteScope => McpScope.Write,
            DeleteScope => McpScope.Delete,
            _ => McpScope.None
        };
    }

    /// <summary>
    /// The first address the app listens on, which is both the issuer of its tokens and, with <c>/mcp</c>, their audience:
    /// the MCP endpoint's URL, which the metadata names as the resource.
    /// </summary>
    private static string GetAddress(IConfiguration configuration)
    {
        var addresses = configuration[AddressKey];
        var address = string.IsNullOrEmpty(addresses) ? DefaultAddress : addresses.Split(AddressSeparator)[0];
        return address.TrimEnd(PathSeparator);
    }
}
