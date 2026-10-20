using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Tests.Shared;
using DemoApi.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// End-to-end tests that boot the real DemoApi in-memory via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// and exercise the full package: source-generated tools, the runtime invoker, loop-prevention
/// middleware, and the wired-up MCP endpoint.
/// </summary>
public class DemoApiE2ETests : IClassFixture<WebApplicationFactory<Program>>
{
    // Seeded in ProductsController.
    private static readonly Guid LaptopId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <summary>
    /// A protocol from before 2026-07-28, which allowed only objects as output schemas and structured content.
    /// </summary>
    private const string OlderProtocolVersion = "2025-06-18";

    private readonly WebApplicationFactory<Program> _factory;

    public DemoApiE2ETests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Controller_Endpoint_ReturnsData()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/products");

        response.EnsureSuccessStatusCode();
        Assert.Contains("Laptop", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task McpEndpoint_IsMappedAndBlocksInternalCalls()
    {
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add("X-MCP-Internal-Call", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot be called internally", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void GeneratedTools_HonorConfigSelectionAndMcpIgnore()
    {
        var toolNames = GetGeneratedToolTypeNames();

        // In the demo's generator.json (SelectedOnly): GetAll and GetById are included.
        Assert.Contains("ProductsController_GetAllTool", toolNames);
        Assert.Contains("ProductsController_GetByIdTool", toolNames);

        // Create is not in the include list; Delete carries [McpIgnore].
        Assert.DoesNotContain(toolNames, n => n.StartsWith("ProductsController_Create"));
        Assert.DoesNotContain(toolNames, n => n.StartsWith("ProductsController_Delete"));
    }

    [Fact]
    public async Task GeneratedTool_InvokesApiEndToEnd_ThroughRuntimeInvoker()
    {
        // Wire the runtime invoker to the in-memory test server so a generated tool's call
        // travels the real path: tool -> IMcpHttpInvoker -> HTTP -> ASP.NET pipeline -> controller.
        var client = _factory.CreateClient();
        var invoker = new McpHttpInvoker(
            client,
            new FixedBaseUrlProvider(client.BaseAddress!.ToString().TrimEnd('/')),
            new HttpContextAccessor(),
            NullLogger<McpHttpInvoker>.Instance,
            Options.Create(new McpScopeOptions()));

        var toolType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.ProductsController_GetByIdTool", throwOnError: true)!;
        var invoke = toolType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;

        // A tool with an output schema hands the parsed body to the SDK, which turns it into structured content.
        var task = (Task<object>)invoke.Invoke(null, new object?[] { invoker, LaptopId, CancellationToken.None })!;
        var product = Assert.IsType<JsonElement>(await task);

        Assert.Equal("Laptop", product.GetProperty("name").GetString());
        Assert.Equal(LaptopId, product.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task GeneratedTool_PropagatesCancellation_ToTheHttpCall()
    {
        // A cancelled token must abort the call. If the generated tool dropped the token
        // (passing default), this request would instead succeed and return data.
        var client = _factory.CreateClient();
        var invoker = new McpHttpInvoker(
            client,
            new FixedBaseUrlProvider(client.BaseAddress!.ToString().TrimEnd('/')),
            new HttpContextAccessor(),
            NullLogger<McpHttpInvoker>.Instance,
            Options.Create(new McpScopeOptions()));

        var toolType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.ProductsController_GetByIdTool", throwOnError: true)!;
        var invoke = toolType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = (Task<object>)invoke.Invoke(null, new object?[] { invoker, LaptopId, cts.Token })!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void GeneratedTool_TakesDescriptions_FromXmlDocs_InRealBuild()
    {
        // Proves the real demo build (GenerateDocumentationFile=true) flows <summary>/<param>
        // docs into the generated [Description] attributes, not the generic fallbacks.
        var toolType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.ProductsController_GetByIdTool", throwOnError: true)!;
        var method = toolType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;

        var toolDescription = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.Equal("Gets a product by its unique identifier.", toolDescription);

        var idParam = method.GetParameters().Single(p => p.Name == "id");
        var paramDescription = idParam.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.Equal("The unique identifier of the product to retrieve.", paramDescription);
    }

    [Fact]
    public async Task GeneratedTool_ReturnsIsError_WhenApiReturnsErrorStatus()
    {
        // A non-2xx downstream response must surface as isError = true on the tool result
        // (issue #7) - conditioned, not thrown - so the model sees a deliberate failure.
        var client = _factory.CreateClient();
        var invoker = new McpHttpInvoker(
            client,
            new FixedBaseUrlProvider(client.BaseAddress!.ToString().TrimEnd('/')),
            new HttpContextAccessor(),
            NullLogger<McpHttpInvoker>.Instance,
            Options.Create(new McpScopeOptions()));

        var toolType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.ProductsController_GetByIdTool", throwOnError: true)!;
        var invoke = toolType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;

        var unknownId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var task = (Task<object>)invoke.Invoke(null, new object?[] { invoker, unknownId, CancellationToken.None })!;
        var result = Assert.IsType<CallToolResult>(await task);

        Assert.True(result.IsError);
        Assert.Contains("404", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task GeneratedTool_SendsDateRouteValue_InIsoFormat_RegardlessOfCulture()
    {
        // DateOnly.ToString() follows the current culture ("9/28/2026" in en-US), which the
        // route binder cannot parse, so every date-routed tool answered 400.
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            var invoke = GetGeneratedInvokeMethod("WeatherController_GetByDateTool");

            var task = (Task<object>)invoke.Invoke(null, new object?[] { CreateDirectInvoker(), new DateOnly(2026, 9, 28), CancellationToken.None })!;
            var forecast = Assert.IsType<JsonElement>(await task);

            Assert.Contains("2026-09-28", forecast.GetProperty("date").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task GeneratedTool_ExpandsFromQueryObject_IntoItsProperties()
    {
        // A [FromQuery] object used to be sent as "filter=<type name>", which the binder ignores,
        // so the endpoint silently ran with an empty filter instead of failing.
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            var invoke = GetGeneratedInvokeMethod("WeatherController_SearchTool");
            var filter = new WeatherSearchFilter
            {
                City = "Brno",
                From = new DateOnly(2026, 9, 28),
                Summaries = ["Mild", "Warm"]
            };

            var task = (Task<object>)invoke.Invoke(null, new object?[] { CreateDirectInvoker(), filter, CancellationToken.None })!;
            var text = Assert.IsType<JsonElement>(await task).GetRawText();

            Assert.Contains("Brno", text);
            Assert.Contains("Mild", text);
            Assert.Contains("Warm", text);
            Assert.Contains("2026-09-29", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_SurfacesIsError_OverTheWire()
    {
        // The definitive #7 test: drive the real /mcp endpoint with an MCP client and assert
        // that a failed downstream call comes back as isError:true in the protocol response
        // (verifies the SDK maps the returned CallToolResult.IsError onto the wire).
        await using var client = await ConnectMcpClientAsync();

        var ok = await client.CallToolAsync(
            "Products_GetById",
            new Dictionary<string, object?> { ["id"] = LaptopId.ToString() });
        Assert.NotEqual(true, ok.IsError);
        Assert.Contains("Laptop", ((TextContentBlock)ok.Content[0]).Text);

        var failed = await client.CallToolAsync(
            "Products_GetById",
            new Dictionary<string, object?> { ["id"] = "ffffffff-ffff-ffff-ffff-ffffffffffff" });
        Assert.True(failed.IsError);
        Assert.Contains("404", ((TextContentBlock)failed.Content[0]).Text);
    }

    [Theory]
    [InlineData(null, "User is not authenticated. Scope validation requires authentication.")]
    [InlineData("write", "Insufficient scope. Required: Read, Granted: Write")]
    public async Task McpProtocol_ToolsCall_TellsTheClientWhyTheScopeCheckRefusedIt(string? grantedScopes, string expectedText)
    {
        await using var client = await ConnectMcpClientAsync(configureServices: RequireScopes, grantedScopes: grantedScopes);

        var result = await CallToolRawAsync(client, "Products_GetById", new JsonObject { ["id"] = LaptopId.ToString() });

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.False(result.AsObject().ContainsKey("structuredContent"));
        Assert.Equal(expectedText, Assert.Single(result["content"]!.AsArray())!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_PassesTheScopeCheck_WithTheRequiredScope()
    {
        await using var client = await ConnectMcpClientAsync(configureServices: RequireScopes, grantedScopes: "read");

        var result = await CallToolRawAsync(client, "Products_GetById", new JsonObject { ["id"] = LaptopId.ToString() });

        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Equal("Laptop", result["structuredContent"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_ChecksScopes_InAStatefulSession()
    {
        await using var client = await ConnectMcpClientAsync(OlderProtocolVersion, services =>
        {
            RequireScopes(services);
            services.Configure<HttpServerTransportOptions>(options => options.SessionMode = HttpServerSessionMode.Stateful);
        }, grantedScopes: "read");

        var result = await CallToolRawAsync(client, "Products_GetById", new JsonObject { ["id"] = LaptopId.ToString() });

        Assert.NotNull(client.SessionId);
        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Equal("Laptop", result["structuredContent"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_NamesPerSessionExecutionContext_WhenAStatefulSessionRunsTheCallWithoutItsRequest()
    {
        // The session runs every call in the context of its initialize request, whose HttpContext is gone by then.
        await using var client = await ConnectMcpClientAsync(OlderProtocolVersion, services =>
        {
            RequireScopes(services);
            services.Configure<HttpServerTransportOptions>(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateful;
#pragma warning disable MCP9006 // The obsolete option is what the test is about.
                options.PerSessionExecutionContext = true;
#pragma warning restore MCP9006
            });
        }, grantedScopes: "read");

        var result = await CallToolRawAsync(client, "Products_GetById", new JsonObject { ["id"] = LaptopId.ToString() });

        Assert.NotNull(client.SessionId);
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(
            "Scope validation needs the HTTP request of the tool call, and none is available. A stateful session with " +
            "HttpServerTransportOptions.PerSessionExecutionContext = true runs its tool calls without their requests; set the option to false.",
            result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpProtocol_ToolsList_DescribesRequestFields_FromXmlDocs()
    {
        // The SDK reads only [Description] when it builds a parameter type's schema; each DTO property's XML
        // <summary> is filled in at runtime from DemoApi.xml, and an explicit [Description] still wins.
        await using var client = await ConnectMcpClientAsync();

        var tools = await client.ListToolsAsync();

        var requestProperties = tools.Single(x => x.Name == "Weather_Create").JsonSchema
            .GetProperty("properties")
            .GetProperty("request")
            .GetProperty("properties");
        Assert.Equal("Name of the city the forecast is for.", GetDescription(requestProperties, "city"));
        Assert.Equal("Day the forecast applies to.", GetDescription(requestProperties, "date"));
        Assert.Equal("Air temperature in degrees Celsius.", GetDescription(requestProperties, "temperatureC"));
        Assert.Equal("One-word summary of the weather, such as Mild or Scorching.", GetDescription(requestProperties, "summary"));

        var filterProperties = tools.Single(x => x.Name == "Weather_Search").JsonSchema
            .GetProperty("properties")
            .GetProperty("filter")
            .GetProperty("properties");
        Assert.Equal("Summaries to produce one forecast each for.", GetDescription(filterProperties, "summaries"));
    }

    [Fact]
    public async Task McpProtocol_ToolsList_StatesEveryAnnotationExplicitly_ForGeneratedTools()
    {
        // A hint left out reads as its spec default on the client: a GET would count as destructive and open-world.
        await using var client = await ConnectMcpClientAsync();

        var tools = await client.ListToolsAsync();

        var read = tools.Single(x => x.Name == "Weather_GetAll").ProtocolTool.Annotations;
        Assert.True(read?.ReadOnlyHint);
        Assert.False(read?.DestructiveHint);
        Assert.True(read?.IdempotentHint);
        Assert.False(read?.OpenWorldHint);

        var write = tools.Single(x => x.Name == "Weather_Create").ProtocolTool.Annotations;
        Assert.False(write?.ReadOnlyHint);
        Assert.False(write?.DestructiveHint);
        Assert.False(write?.IdempotentHint);
        Assert.False(write?.OpenWorldHint);

        var delete = tools.Single(x => x.Name == "Weather_DeleteByCity").ProtocolTool.Annotations;
        Assert.False(delete?.ReadOnlyHint);
        Assert.True(delete?.DestructiveHint);
        Assert.True(delete?.IdempotentHint);
        Assert.False(delete?.OpenWorldHint);
    }

    [Fact]
    public async Task McpProtocol_ToolsList_TitlesEveryGeneratedTool()
    {
        // Clients show the title in place of the tool name; without one a directory lists "Weather_GetAll".
        await using var client = await ConnectMcpClientAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal("Get all", tools.Single(x => x.Name == "Weather_GetAll").ProtocolTool.Annotations?.Title);
        Assert.Equal("Delete a city's forecasts", tools.Single(x => x.Name == "Weather_DeleteByCity").ProtocolTool.Annotations?.Title);
        Assert.Equal("Get by id", tools.Single(x => x.Name == "Products_GetById").ProtocolTool.Annotations?.Title);
    }

    [Fact]
    public async Task McpProtocol_ToolsList_AdvertisesOutputSchemas_FromTheResponseTypes()
    {
        await using var client = await ConnectMcpClientAsync();

        var tools = await client.ListToolsAsync();

        var product = GetOutputSchema(tools, "Products_GetById");
        Assert.Equal("object", product.GetProperty("type").GetString());
        Assert.Equal("Display name of the product.", GetDescription(product.GetProperty("properties"), "name"));
        Assert.Equal("array", GetOutputSchema(tools, "Products_GetAll").GetProperty("type").GetString());
        // GetCheapest answers NoContent() for an empty category, so its schema allows null.
        var cheapest = GetOutputSchema(tools, "Products_GetCheapest").GetProperty("anyOf");
        Assert.Equal("object", cheapest[0].GetProperty("type").GetString());
        Assert.Equal("null", cheapest[1].GetProperty("type").GetString());
        Assert.Null(tools.Single(x => x.Name == "Server_GetEnvironment").ProtocolTool.OutputSchema);
    }

    [Fact]
    public async Task McpProtocol_ToolsList_WrapsSchemasThatAreNotPlainObjects_ForAClientOnAnOlderProtocol()
    {
        // Before 2026-07-28 an output schema had to describe an object; the SDK moves any other under "result".
        await using var client = await ConnectMcpClientAsync(OlderProtocolVersion);

        var tools = await client.ListToolsAsync();

        Assert.Equal(OlderProtocolVersion, client.NegotiatedProtocolVersion);
        Assert.Equal("object", GetOutputSchema(tools, "Products_GetById").GetProperty("type").GetString());
        Assert.Equal("array", GetResultSchema(GetOutputSchema(tools, "Products_GetAll")).GetProperty("type").GetString());
        Assert.True(GetResultSchema(GetOutputSchema(tools, "Products_GetCheapest")).TryGetProperty("anyOf", out _));
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_ReturnsTheResponseBody_AsStructuredContent()
    {
        await using var client = await ConnectMcpClientAsync();

        var product = await CallToolRawAsync(client, "Products_GetById", new JsonObject { ["id"] = LaptopId.ToString() });
        var products = await CallToolRawAsync(client, "Products_GetAll", new JsonObject());
        var cheapest = await CallToolRawAsync(client, "Products_GetCheapest", new JsonObject { ["category"] = "Kitchen" });

        Assert.Equal("Laptop", product["structuredContent"]!["name"]!.GetValue<string>());
        Assert.Equal(3, products["structuredContent"]!.AsArray().Count);
        Assert.Equal("Coffee Mug", cheapest["structuredContent"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_SendsNullStructuredContent_ForASuccessWithoutABody()
    {
        // A client treats a missing structuredContent on a tool with an output schema as an error; null is a value.
        await using var client = await ConnectMcpClientAsync();

        var result = await CallToolRawAsync(client, "Products_GetCheapest", new JsonObject { ["category"] = "Garden" });

        Assert.True(string.CompareOrdinal(client.NegotiatedProtocolVersion, "2026-07-28") >= 0);
        Assert.True(result.AsObject().ContainsKey("structuredContent"));
        Assert.Null(result["structuredContent"]);
        Assert.Equal("null", Assert.Single(result["content"]!.AsArray())!["text"]!.GetValue<string>());
        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_WrapsTheResult_ForAClientOnAnOlderProtocol()
    {
        // Before 2026-07-28 structured content had to be an object, so a list or a null comes back under "result".
        await using var client = await ConnectMcpClientAsync(OlderProtocolVersion);

        var products = await CallToolRawAsync(client, "Products_GetAll", new JsonObject());
        var cheapest = await CallToolRawAsync(client, "Products_GetCheapest", new JsonObject { ["category"] = "Garden" });
        var product = await CallToolRawAsync(client, "Products_GetById", new JsonObject { ["id"] = LaptopId.ToString() });

        Assert.Equal(3, products["structuredContent"]!["result"]!.AsArray().Count);
        Assert.True(cheapest["structuredContent"]!.AsObject().ContainsKey("result"));
        Assert.Null(cheapest["structuredContent"]!["result"]);
        Assert.Equal("Laptop", product["structuredContent"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpProtocol_ToolsCall_SendsNoStructuredContent_ForAnError()
    {
        await using var client = await ConnectMcpClientAsync();

        var result = await CallToolRawAsync(client, "Products_GetById", new JsonObject { ["id"] = "ffffffff-ffff-ffff-ffff-ffffffffffff" });

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.False(result.AsObject().ContainsKey("structuredContent"));
        Assert.Contains("404", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpProtocol_CallsATool_ThatTheControllerInheritsFromAGenericBase()
    {
        // CategoriesController declares no action: it serves the GetAll of ListControllerBase<Category> at its own route,
        // with Category for T. The abstract base serves nothing. https://github.com/Kebechet/Api.ToMcp/issues/28
        await using var client = await ConnectMcpClientAsync();
        var tools = await client.ListToolsAsync();

        Assert.DoesNotContain(tools, x => x.Name.StartsWith("ListControllerBase", StringComparison.Ordinal));
        var categorySchema = GetOutputSchema(tools, "Categories_GetAll").GetProperty("items");
        Assert.Equal("Name of the category, as products name it.", GetDescription(categorySchema.GetProperty("properties"), "name"));

        var result = await CallToolRawAsync(client, "Categories_GetAll", new JsonObject());

        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Equal(["Electronics", "Kitchen"], result["structuredContent"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task McpProtocol_ListsAndCallsAHandWrittenTool_WithItsAnnotationsAndInjectedService()
    {
        // ServerTools has no endpoint behind it; AddMcpTools must register it exactly as the SDK's assembly scan
        // did: name, description, annotations, and IHostEnvironment injected rather than exposed as an argument.
        await using var client = await ConnectMcpClientAsync();

        var tool = (await client.ListToolsAsync()).Single(x => x.Name == "Server_GetEnvironment");
        Assert.Equal("Gets the name of the hosting environment the API runs in.", tool.Description);
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint);
        Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint);
        var hasEnvironmentArgument = tool.JsonSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("environment", out _);
        Assert.False(hasEnvironmentArgument);

        var result = await client.CallToolAsync("Server_GetEnvironment");

        Assert.NotEqual(true, result.IsError);
        var environmentName = _factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName;
        Assert.Equal(environmentName, ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public void Startup_BuildsTheTools_AndReportsNoMissingXmlDocs()
    {
        // The tools are built when the host starts, not on the first MCP request, so a missing XML documentation
        // file is warned about in the startup log. The demo ships DemoApi.xml, so nothing is missing.
        var logs = new List<(LogLevel Level, string Message)>();
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(new ListLoggerProvider(logs));
            logging.AddFilter<ListLoggerProvider>("Api.ToMcp", LogLevel.Debug);
        }));

        using var client = factory.CreateClient();

        List<(LogLevel Level, string Message)> entries;
        lock (logs)
        {
            entries = logs.ToList();
        }

        Assert.Contains(entries, x => x.Level == LogLevel.Debug && x.Message.Contains("MCP tools"));
        Assert.Empty(entries.Where(x => x.Level == LogLevel.Warning && x.Message.Contains("XML documentation")));
    }

    private static JsonElement GetOutputSchema(IList<McpClientTool> tools, string toolName)
    {
        var outputSchema = tools.Single(x => x.Name == toolName).ProtocolTool.OutputSchema;

        Assert.NotNull(outputSchema);
        return outputSchema.Value;
    }

    /// <summary>
    /// The schema inside the SDK's envelope for older clients: <c>{"type":"object","properties":{"result":...},"required":["result"]}</c>.
    /// </summary>
    private static JsonElement GetResultSchema(JsonElement outputSchema)
    {
        Assert.Equal("object", outputSchema.GetProperty("type").GetString());
        Assert.Equal(["result"], outputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));

        return outputSchema.GetProperty("properties").GetProperty("result");
    }

    /// <summary>
    /// The raw <c>tools/call</c> result: the typed client reads <c>"structuredContent": null</c> and a missing one alike.
    /// </summary>
    private static async Task<JsonNode> CallToolRawAsync(McpClient client, string toolName, JsonObject arguments)
    {
        var response = await client.SendRequestAsync(new JsonRpcRequest
        {
            Method = RequestMethods.ToolsCall,
            Params = new JsonObject { ["name"] = toolName, ["arguments"] = arguments }
        });

        return response.Result!;
    }

    private static string? GetDescription(JsonElement properties, string propertyName)
    {
        return properties.GetProperty(propertyName).TryGetProperty("description", out var description)
            ? description.GetString()
            : null;
    }

    private async Task<McpClient> ConnectMcpClientAsync(string? protocolVersion = null, Action<IServiceCollection>? configureServices = null, string? grantedScopes = null)
    {
        // Route the invoker's internal self-call back into the in-memory server (otherwise it
        // uses a real HttpClient that can't reach the TestServer, and every call "fails").
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("McpTools:BaseUrl", "http://localhost");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<IMcpHttpInvoker, McpHttpInvoker>()
                    .ConfigurePrimaryHttpMessageHandler(sp =>
                        ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler());
                services.AddTransient<IStartupFilter, TestUserStartupFilter>();
                configureServices?.Invoke(services);
            });
        });

        var headers = new Dictionary<string, string>();
        if (grantedScopes is not null)
        {
            headers[TestUserStartupFilter.ScopeHeader] = grantedScopes;
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers
            },
            factory.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion });
    }

    private McpHttpInvoker CreateDirectInvoker()
    {
        var client = _factory.CreateClient();
        return new McpHttpInvoker(
            client,
            new FixedBaseUrlProvider(client.BaseAddress!.ToString().TrimEnd('/')),
            new HttpContextAccessor(),
            NullLogger<McpHttpInvoker>.Instance,
            Options.Create(new McpScopeOptions()));
    }

    private static MethodInfo GetGeneratedInvokeMethod(string toolTypeName)
    {
        var toolType = typeof(Program).Assembly
            .GetType($"Api.ToMcp.Generated.{toolTypeName}", throwOnError: true)!;
        return toolType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)!;
    }

    private static string[] GetGeneratedToolTypeNames()
    {
        var infoType = typeof(Program).Assembly
            .GetType("Api.ToMcp.Generated.McpToolsInfo", throwOnError: true)!;
        var toolTypes = (Type[])infoType
            .GetField("ToolTypes", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        return toolTypes.Select(t => t.Name).ToArray();
    }

    /// <summary>
    /// Scope validation as an app turns it on: the "scope" claim lists the granted scopes, separated by spaces.
    /// </summary>
    private static void RequireScopes(IServiceCollection services)
    {
        services.Configure<McpScopeOptions>(options => options.ClaimToScopeMapper = claimValue => claimValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(McpScope.None, (granted, scope) => granted | Enum.Parse<McpScope>(scope, ignoreCase: true)));
    }

    /// <summary>
    /// Signs the request in as a user whose "scope" claim is the <see cref="ScopeHeader"/> header, as an authentication
    /// handler would from a token; a request without the header stays anonymous.
    /// </summary>
    private sealed class TestUserStartupFilter : IStartupFilter
    {
        public const string ScopeHeader = "X-Test-Scope";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(ScopeHeader, out var scopes))
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", scopes.ToString())], authenticationType: "Test"));
                    }

                    return nextMiddleware(context);
                });
                next(app);
            };
        }
    }

    private sealed class FixedBaseUrlProvider : ISelfBaseUrlProvider
    {
        private readonly string _baseUrl;
        public FixedBaseUrlProvider(string baseUrl) => _baseUrl = baseUrl;
        public string GetBaseUrl() => _baseUrl;
    }
}
