using System.Net;
using System.Security.Cryptography;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using Xunit;
using static DemoApi.IntegrationTests.OAuthTestApp;

namespace DemoApi.IntegrationTests;

/// <summary>
/// Apps in the Development environment, where the host validates every registered service when it builds the provider,
/// as <c>dotnet run</c> with the usual launch profile does. The other tests run in whatever environment the machine sets,
/// and <c>WebApplicationFactory</c> follows an <c>ASPNETCORE_ENVIRONMENT</c> variable, so they may never validate.
/// </summary>
public sealed class DevelopmentHostTests
{
    private const string Issuer = "https://login.example.com";

    [Fact]
    public async Task AnAppWithoutAuthentication_StartsInDevelopment_AndServesAToolCall()
    {
        await using var app = await StartAsync(hasAuthentication: false, configureServices: _ => { });
        await using var client = await ConnectAsync(app.Urls.Single(), authorization: null);

        var result = await client.CallToolAsync("Products_GetById", new Dictionary<string, object?> { ["id"] = LaptopId });

        Assert.Equal(Environments.Development, app.Environment.EnvironmentName);
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task AnAppWithSignInDiscoveryAndJwtBearer_StartsInDevelopment_AndSignsTheCallerIn()
    {
        var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        await using var app = await StartAsync(hasAuthentication: true, configureServices: services =>
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidateAudience = false,
                    IssuerSigningKey = signingKey
                };
            });
            services.AddAuthorization();
            services.Configure<McpScopeOptions>(options => options.ResourceMetadata = new ProtectedResourceMetadata { AuthorizationServers = [Issuer] });
        });
        var address = app.Urls.Single();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object> { ["sub"] = "alice" },
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
        });

        using var anonymous = await PostAsync(address + "/mcp", ToolsList, authorization: null);
        await using var client = await ConnectAsync(address, "Bearer " + token);
        var result = await client.CallToolAsync("Products_GetById", new Dictionary<string, object?> { ["id"] = LaptopId });

        Assert.Equal(Environments.Development, app.Environment.EnvironmentName);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{address}{MetadataPath}\"", anonymous.Headers.WwwAuthenticate.ToString());
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)result.Content[0]).Text);
    }

    private static async Task<WebApplication> StartAsync(bool hasAuthentication, Action<IServiceCollection> configureServices)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(Program).Assembly);
        builder.Services.AddMcpTools(typeof(Program).Assembly, options => options.ArePropertyDescriptionsFromXmlDocs = false);
        configureServices(builder.Services);

        var app = builder.Build();
        if (hasAuthentication)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseMcpLoopPrevention();
        app.MapControllers();
        app.MapMcpEndpoint("mcp");
        await app.StartAsync();

        return app;
    }
}
