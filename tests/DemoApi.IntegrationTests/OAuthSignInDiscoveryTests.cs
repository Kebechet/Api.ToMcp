using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using Xunit;
using static DemoApi.IntegrationTests.OAuthTestApp;

namespace DemoApi.IntegrationTests;

/// <summary>
/// OAuth sign-in discovery as a client meets it: the challenges, the metadata, and the scope challenges of tool calls. See
/// <see cref="OAuthTestApp"/> for the app.
/// </summary>
public sealed class OAuthSignInDiscoveryTests
{
    /// <summary>
    /// The body size up to which the README says a tool call is read before the SDK reads it.
    /// </summary>
    private const int DocumentedPreReadLimit = 1024 * 1024;

    private static readonly JsonObject Forecast = new() { ["city"] = "Brno", ["date"] = "2026-11-12T00:00:00", ["temperatureC"] = 21, ["summary"] = "Mild" };

    [Fact]
    public async Task ARequestWithoutAToken_GetsA401_ThatPointsToTheMetadata()
    {
        await using var app = await StartAsync();
        var address = app.Urls.Single();

        using var response = await PostToolCallAsync(address + "/mcp", "Products_GetAll", authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{address}{MetadataPath}\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task TheMetadata_NamesTheResource_TheAuthorizationServer_AndTheScopesTheToolsRequire()
    {
        await using var app = await StartAsync();
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
        await using var app = await StartAsync();
        await using var client = await ConnectAsync(app.Urls.Single(), token);

        var result = await client.CallToolAsync("Products_GetById", new Dictionary<string, object?> { ["id"] = LaptopId });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task AToolCallWithTooLittleScope_GetsA403InsufficientScope_NamingTheScopesItRequiresAndTheCallerHas()
    {
        await using var app = await StartAsync();
        var address = app.Urls.Single();

        using var response = await PostToolCallAsync(address + "/mcp", "Weather_Create", ReadToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            $"Bearer error=\"insufficient_scope\", scope=\"mcp:read mcp:write\", resource_metadata=\"{address}{MetadataPath}\", error_description=\"Insufficient scope. Required: Write, Granted: Read\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AToolCallWithTheScopeItRequires_IsServed()
    {
        await using var app = await StartAsync();
        await using var client = await ConnectAsync(app.Urls.Single(), WriteToken);

        var result = await client.CallToolAsync("Weather_Create", new Dictionary<string, object?> { ["request"] = Forecast.DeepClone() });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Brno", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task WithAnonymousAccess_AClientListsTheToolsBeforeItSignsIn_AndIsAskedToSignInWhenItCallsOne()
    {
        await using var app = await StartAsync(allowAnonymous: true);
        var address = app.Urls.Single();
        await using var client = await ConnectAsync(address, authorization: null);

        var tools = await client.ListToolsAsync();
        using var response = await PostToolCallAsync(address + "/mcp", "Products_GetById", authorization: null);

        Assert.Contains(tools, x => x.Name == "Products_GetById");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{address}{MetadataPath}\", scope=\"mcp:read\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "/tenant")]
    [InlineData("/oauth/resource", "/tenant")]
    [InlineData("oauth/resource", null)]
    [InlineData("https://metadata.example.com/prm", "/tenant")]
    public async Task TheScopeChallenge_PointsToTheMetadata_AsTheSdksOwnChallengeDoes(string? configuredUri, string? pathBase)
    {
        await using var app = await StartAsync(
            configureServices: services => services.Configure<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme, options =>
            {
                if (configuredUri is not null)
                {
                    options.ResourceMetadataUri = new Uri(configuredUri, UriKind.RelativeOrAbsolute);
                }
            }),
            pathBase: pathBase);
        var mcpUrl = app.Urls.Single() + pathBase + "/mcp";

        using var sdkChallenge = await PostAsync(mcpUrl, ToolsList, authorization: null);
        using var scopeChallenge = await PostToolCallAsync(mcpUrl, "Weather_Create", ReadToken);

        Assert.Equal(HttpStatusCode.Unauthorized, sdkChallenge.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, scopeChallenge.StatusCode);
        Assert.NotEqual("", GetResourceMetadata(sdkChallenge.Headers.WwwAuthenticate));
        Assert.Equal(GetResourceMetadata(sdkChallenge.Headers.WwwAuthenticate), GetResourceMetadata(scopeChallenge.Headers.WwwAuthenticate));
    }

    [Fact]
    public async Task AToolCallInUtf16_GetsTheSameChallenge_AsTheSdkReadsItTheSameWay()
    {
        await using var app = await StartAsync();
        var body = ToolCall("Weather_Create");

        using var response = await PostAsync(app.Urls.Single() + "/mcp", body, ReadToken, new StringContent(body, Encoding.Unicode, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("error=\"insufficient_scope\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AChunkedToolCall_AsTheSdksClientSendsIt_GetsTheChallenge()
    {
        await using var app = await StartAsync();
        var content = new ChunkedContent(ToolCall("Weather_Create"));

        using var response = await PostAsync(app.Urls.Single() + "/mcp", "", ReadToken, content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("error=\"insufficient_scope\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AToolCallLargerThanTheLimit_IsLeftToTheToolsOwnCheck_WhetherItsLengthIsKnownOrNot(bool isChunked)
    {
        await using var app = await StartAsync();
        var forecast = Forecast.DeepClone();
        forecast["summary"] = new string('x', DocumentedPreReadLimit);
        var body = ToolCall("Weather_Create", new JsonObject { ["request"] = forecast }.ToJsonString());
        HttpContent content = isChunked ? new ChunkedContent(body) : new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await PostAsync(app.Urls.Single() + "/mcp", body, ReadToken, content);
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Contains("Insufficient scope. Required: Write, Granted: Read", responseText);
    }

    [Fact]
    public async Task WithoutResourceMetadata_TheEndpointServesAnyone_TheMetadataIsNotFound_AndARefusedScopeIsAnErrorResult()
    {
        await using var app = await StartAsync(isSignInDiscoverable: false);
        var address = app.Urls.Single();
        using var httpClient = new HttpClient();
        await using var anonymousClient = await ConnectAsync(address, authorization: null);
        await using var readClient = await ConnectAsync(address, ReadToken);

        var tools = await anonymousClient.ListToolsAsync();
        using var metadataResponse = await httpClient.GetAsync(address + MetadataPath);
        var result = await readClient.CallToolAsync("Weather_Create", new Dictionary<string, object?> { ["request"] = Forecast.DeepClone() });

        Assert.NotEmpty(tools);
        Assert.Equal(HttpStatusCode.NotFound, metadataResponse.StatusCode);
        Assert.True(result.IsError);
        Assert.Equal("Insufficient scope. Required: Write, Granted: Read", ((TextContentBlock)result.Content[0]).Text);
    }

    /// <summary>
    /// A body without a length, which <c>HttpClient</c> sends chunked, as it sends the MCP SDK client's <c>JsonContent</c>.
    /// </summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _body;

        public ChunkedContent(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(_body).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
