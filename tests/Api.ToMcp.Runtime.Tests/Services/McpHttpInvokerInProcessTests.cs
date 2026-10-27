using System.Net;
using System.Text.Json;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpHttpInvokerInProcessTests
{
    private const string ProviderBaseUrl = "http://localhost:5000";

    private readonly HttpContextAccessor _accessor = new();
    private readonly RecordingHandler _httpHandler = new();

    [Fact]
    public async Task GetAsync_ServesTheRequestInProcess_AtTheAddressTheMcpRequestCameTo()
    {
        _accessor.HttpContext = CreateCaller();
        var invoker = CreateInvoker(BuildEchoPipeline());

        var result = await invoker.GetAsync("/api/products/7?x=1");

        var seen = Echoed(result);
        Assert.Equal("https", seen.GetProperty("scheme").GetString());
        Assert.Equal("api.example.com", seen.GetProperty("host").GetString());
        Assert.Equal("/v1", seen.GetProperty("pathBase").GetString());
        Assert.Equal("/api/products/7", seen.GetProperty("path").GetString());
        Assert.Equal("?x=1", seen.GetProperty("query").GetString());
        Assert.Empty(_httpHandler.Requests);
    }

    [Fact]
    public async Task GetAsync_ForwardsTheAuthorization_AndMarksTheRequestAsInternal_InProcess()
    {
        _accessor.HttpContext = CreateCaller();
        var invoker = CreateInvoker(BuildEchoPipeline());

        var result = await invoker.GetAsync("/api/products/7");

        var seen = Echoed(result);
        Assert.Equal("Bearer caller-token", seen.GetProperty("authorization").GetString());
        Assert.Equal("true", seen.GetProperty("internalCall").GetString());
    }

    [Fact]
    public async Task PostAsync_SendsTheJsonBody_InProcess()
    {
        _accessor.HttpContext = CreateCaller();
        var invoker = CreateInvoker(BuildEchoPipeline());

        var result = await invoker.PostAsync("/api/products", "{\"name\":\"Lamp\"}");

        var seen = Echoed(result);
        Assert.Equal("POST", seen.GetProperty("method").GetString());
        Assert.Equal("application/json; charset=utf-8", seen.GetProperty("contentType").GetString());
        Assert.Equal("{\"name\":\"Lamp\"}", seen.GetProperty("body").GetString());
    }

    [Fact]
    public async Task GetAsync_ServesTheRequestInProcess_AtTheProvidersAddress_OutsideAnMcpRequest()
    {
        var invoker = CreateInvoker(BuildEchoPipeline());

        var result = await invoker.GetAsync("/api/products/7");

        var seen = Echoed(result);
        Assert.Equal("http", seen.GetProperty("scheme").GetString());
        Assert.Equal("localhost:5000", seen.GetProperty("host").GetString());
        Assert.Equal("/api/products/7", seen.GetProperty("path").GetString());
        Assert.Empty(_httpHandler.Requests);
    }

    [Fact]
    public async Task GetAsync_ReportsAnErrorStatus_AsAnErrorResult_InProcess()
    {
        _accessor.HttpContext = CreateCaller();
        var invoker = CreateInvoker(BuildPipeline(app => app.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("no such product");
        })));

        var result = await invoker.GetAsync("/api/products/7");

        Assert.True(result.IsError);
        Assert.Equal("HTTP 404: Not Found - no such product", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task GetAsync_SendsTheRequestOverHttp_WhenInProcessCallsAreOff()
    {
        _accessor.HttpContext = CreateCaller();
        var invoker = CreateInvoker(BuildEchoPipeline(), new McpScopeOptions { AreApiCallsInProcess = false });

        await invoker.GetAsync("/api/products/7");

        Assert.Equal([$"{ProviderBaseUrl}/api/products/7"], _httpHandler.Requests);
    }

    [Fact]
    public async Task GetAsync_SendsTheRequestOverHttp_WhenTheHostBuiltNoPipeline()
    {
        _accessor.HttpContext = CreateCaller();
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var unbuiltPipeline = new InProcessApiPipeline(new DefaultHttpContextFactory(services), NullLogger<InProcessApiPipeline>.Instance);
        var invoker = CreateInvoker(unbuiltPipeline);

        await invoker.GetAsync("/api/products/7");

        Assert.Equal([$"{ProviderBaseUrl}/api/products/7"], _httpHandler.Requests);
    }

    private McpHttpInvoker CreateInvoker(InProcessApiPipeline pipeline, McpScopeOptions? scopeOptions = null)
    {
        return new McpHttpInvoker(
            new HttpClient(_httpHandler),
            new FixedBaseUrlProvider(ProviderBaseUrl),
            _accessor,
            NullLogger<McpHttpInvoker>.Instance,
            Microsoft.Extensions.Options.Options.Create(scopeOptions ?? new McpScopeOptions()),
            pipeline);
    }

    private static DefaultHttpContext CreateCaller()
    {
        var caller = new DefaultHttpContext();
        caller.Request.Scheme = "https";
        caller.Request.Host = new HostString("api.example.com");
        caller.Request.PathBase = "/v1";
        caller.Request.Path = "/mcp";
        caller.Request.Headers.Authorization = "Bearer caller-token";
        return caller;
    }

    private static InProcessApiPipeline BuildEchoPipeline()
    {
        return BuildPipeline(app => app.Run(async context =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
            {
                ["method"] = context.Request.Method,
                ["scheme"] = context.Request.Scheme,
                ["host"] = context.Request.Host.Value,
                ["pathBase"] = context.Request.PathBase.Value,
                ["path"] = context.Request.Path.Value,
                ["query"] = context.Request.QueryString.Value,
                ["authorization"] = context.Request.Headers.Authorization.ToString(),
                ["internalCall"] = context.Request.Headers["X-MCP-Internal-Call"].ToString(),
                ["contentType"] = context.Request.ContentType,
                ["body"] = body
            });
        }));
    }

    private static InProcessApiPipeline BuildPipeline(Action<IApplicationBuilder> configure)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var pipeline = new InProcessApiPipeline(new DefaultHttpContextFactory(services), services.GetRequiredService<ILogger<InProcessApiPipeline>>());
        var builder = new PipelineCapturingApplicationBuilderFactory(new ApplicationBuilderFactory(services), pipeline)
            .CreateBuilder(new FeatureCollection());
        configure(builder);
        builder.Build();

        return pipeline;
    }

    private static JsonElement Echoed(CallToolResult result)
    {
        Assert.False(result.IsError, ((TextContentBlock)result.Content[0]).Text);
        return JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text).RootElement;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class FixedBaseUrlProvider : ISelfBaseUrlProvider
    {
        private readonly string _baseUrl;

        public FixedBaseUrlProvider(string baseUrl)
        {
            _baseUrl = baseUrl;
        }

        public string GetBaseUrl()
        {
            return _baseUrl;
        }
    }
}
