using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.ToMcp.Abstractions;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
using Api.ToMcp.Tests.Shared;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Extensions;

public class McpExtensionsTests
{
    private static readonly Assembly ToolAssembly = typeof(StaticParityTools).Assembly;

    [Fact]
    public void AddMcpTools_RegistersTheSameTools_AsWithToolsFromAssembly()
    {
        // The SDK scan is the reference: with the XML docs off, every tool must come out identical in name,
        // description, annotations, metadata and input schema (DI-bound parameters left out, [Description]s kept).
        // Generated tools are left out: their output schema is meant to differ, it follows MVC's JSON options. A
        // property with a [JsonConverter] of its own would differ too, on purpose, and none of these tools has one.
        var expectedTools = BuildTools(services => services.AddMcpServer().WithToolsFromAssembly(ToolAssembly)).Where(x => !IsGenerated(x));
        var actualTools = BuildTools(services => services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false)).Where(x => !IsGenerated(x)).ToList();

        Assert.Equal(5, actualTools.Count);
        Assert.Equal(expectedTools.Select(ToJson).OrderBy(x => x), actualTools.Select(ToJson).OrderBy(x => x));
    }

    [Fact]
    public void AddMcpTools_BuildsAGeneratedToolsOutputSchema_FromTheAppsMvcJsonOptions()
    {
        // The SDK's own options would give camelCase names and the enum as strings; MVC is configured otherwise.
        var tools = BuildTools(services =>
        {
            services.Configure<JsonOptions>(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);
            services.AddMcpTools(ToolAssembly);
        });

        var outputSchema = GetOutputSchema(tools, "generated_strict");

        Assert.Equal("object", outputSchema.GetProperty("type").GetString());
        var properties = outputSchema.GetProperty("properties");
        Assert.Equal(["Name", "Kind"], properties.EnumerateObject().Select(x => x.Name));
        Assert.Equal("integer", properties.GetProperty("Kind").GetProperty("type").GetString());
    }

    [Fact]
    public void AddMcpTools_GivesAGeneratedToolASchemaOfAnyValue_WhenMvcPreservesReferences()
    {
        var tools = BuildTools(services =>
        {
            services.Configure<JsonOptions>(options => options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.Preserve);
            services.AddMcpTools(ToolAssembly);
        });

        Assert.Equal("{}", GetOutputSchema(tools, "generated_strict").GetRawText());
        Assert.Equal("{}", GetOutputSchema(tools, "generated_nullable").GetRawText());
    }

    [Fact]
    public void AddMcpTools_GivesASchemaOfAnyValue_OnlyToAResponseTypeThatCanContainItself_WhenMvcIgnoresCycles()
    {
        var tools = BuildTools(services =>
        {
            services.Configure<JsonOptions>(options => options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
            services.AddMcpTools(ToolAssembly);
        });

        Assert.Equal("{}", GetOutputSchema(tools, "generated_recursive").GetRawText());
        Assert.Equal("object", GetOutputSchema(tools, "generated_strict").GetProperty("type").GetString());
    }

    [Fact]
    public void AddMcpTools_DescribesAGeneratedToolsOutput_FromXmlDocs()
    {
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly));

        var properties = GetOutputSchema(tools, "generated_strict").GetProperty("properties");

        Assert.Equal("Name documented from XML.", properties.GetProperty("name").GetProperty("description").GetString());
    }

    [Fact]
    public void AddMcpTools_LetsANullableGeneratedToolsOutputSchemaAcceptNull()
    {
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly));

        var branches = GetOutputSchema(tools, "generated_nullable").GetProperty("anyOf").EnumerateArray().ToList();

        Assert.Equal("object", branches[0].GetProperty("type").GetString());
        Assert.Equal("null", branches[1].GetProperty("type").GetString());
    }

    [Fact]
    public void AddMcpTools_LetsAPropertyWrittenByItsOwnConverter_AcceptAnyValue_InTheInputAndOutputSchema()
    {
        // The SDK reads the argument, and MVC writes the response, with the converter: "12.5 EUR", not an object.
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly));
        var tool = tools.Single(x => x.ProtocolTool.Name == "generated_converted").ProtocolTool;

        var inputTotal = tool.InputSchema.GetProperty("properties").GetProperty("invoice").GetProperty("properties").GetProperty("total");
        var outputTotal = GetOutputSchema(tools, "generated_converted").GetProperty("properties").GetProperty("total");

        Assert.Equal("""{"description":"Total as the till prints it."}""", inputTotal.GetRawText());
        Assert.Equal("""{"description":"Total as the till prints it."}""", outputTotal.GetRawText());
    }

    [Fact]
    public void AddMcpTools_LetsAPropertyWrittenByItsOwnConverter_AcceptAnyValue_WhenXmlDocDescriptionsAreOff()
    {
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false));
        var tool = tools.Single(x => x.ProtocolTool.Name == "generated_converted").ProtocolTool;

        var inputTotal = tool.InputSchema.GetProperty("properties").GetProperty("invoice").GetProperty("properties").GetProperty("total");
        var outputTotal = GetOutputSchema(tools, "generated_converted").GetProperty("properties").GetProperty("total");

        Assert.Equal(JsonValueKind.True, inputTotal.ValueKind);
        Assert.Equal(JsonValueKind.True, outputTotal.ValueKind);
    }

    [Fact]
    public void AddMcpTools_LeavesTheArgumentPropertiesAGeneratedToolNeverSends_OutOfItsInputSchema()
    {
        // The query string carries none of them, and a JSON body none without a public getter.
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly));

        var request = GetToolInputProperty(tools, "generated_unsent", "request");

        Assert.Equal(["city", "note"], request.GetProperty("properties").EnumerateObject().Select(x => x.Name));
        Assert.Equal(["city"], request.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var upload = GetToolInputProperty(tools, "generated_unsent", "upload");
        Assert.Equal(["name"], upload.GetProperty("properties").EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public void AddMcpTools_LeavesAHandWrittenToolsInputSchema_ToTheSdk()
    {
        // A hand-written tool gets the request object itself, so a property without a public getter can matter to it.
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly));

        var request = GetToolInputProperty(tools, "handwritten_unsent", "request");

        Assert.Contains("secret", request.GetProperty("properties").EnumerateObject().Select(x => x.Name));
        Assert.Contains("file", request.GetProperty("properties").EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public async Task AddMcpTools_IgnoresAnArgumentPropertyAGeneratedToolNeverSends_WhenTheClientSendsItAnyway()
    {
        // The SDK cannot read an IFormFile at all, so the call failed with "An error occurred invoking".
        var tool = BuildTools(services => services.AddMcpTools(ToolAssembly)).Single(x => x.ProtocolTool.Name == "generated_unsent");
        await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"request":{"city":"Brno","secret":"s1","file":{"fileName":"a.txt"},"files":[{"fileName":"b.txt"}],"token":{"isCancellationRequested":true}},"upload":{"name":"n1","file":{"fileName":"c.txt"},"token":{}}}""")!;
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = "generated_unsent", Arguments = arguments });

        var result = await tool.InvokeAsync(context, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Brno:default-secret:n1", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void AddMcpTools_LeavesAHandWrittenToolsOutputSchema_ToTheSdk()
    {
        var expected = GetOutputSchema(BuildTools(services => services.AddMcpServer().WithToolsFromAssembly(ToolAssembly)), "handwritten_structured");
        var actual = GetOutputSchema(
            BuildTools(services =>
            {
                services.Configure<JsonOptions>(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);
                services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false);
            }),
            "handwritten_structured");

        Assert.Equal(expected.GetRawText(), actual.GetRawText());
    }

    [Fact]
    public void AddMcpTools_KeepsAnnotationsAndDiBoundParameters_AndDescribesRequestFieldsFromXmlDocs()
    {
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly));
        var staticTool = tools.Single(x => x.ProtocolTool.Name == "parity_static").ProtocolTool;
        var instanceTool = tools.Single(x => x.ProtocolTool.Name == "parity_instance").ProtocolTool;

        Assert.Equal("Static tool with injected services.", staticTool.Description);
        Assert.True(staticTool.Annotations?.ReadOnlyHint);
        Assert.True(staticTool.Annotations?.IdempotentHint);
        Assert.False(staticTool.Annotations?.OpenWorldHint);
        Assert.Equal("Instance parity", instanceTool.Title);
        Assert.True(instanceTool.Annotations?.DestructiveHint);

        var properties = staticTool.InputSchema.GetProperty("properties");
        Assert.Equal(["text", "request"], properties.EnumerateObject().Select(x => x.Name));
        Assert.Equal("Text to echo.", properties.GetProperty("text").GetProperty("description").GetString());
        var requestProperties = properties.GetProperty("request").GetProperty("properties");
        Assert.Equal("Documented from XML.", requestProperties.GetProperty("documented").GetProperty("description").GetString());
    }

    [Fact]
    public void AddMcpTools_LeavesRequestFieldsUndescribed_WhenTheOptionIsOff()
    {
        var tools = BuildTools(services => services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false));
        var requestProperties = tools.Single(x => x.ProtocolTool.Name == "parity_static").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("request")
            .GetProperty("properties");

        Assert.False(requestProperties.GetProperty("documented").TryGetProperty("description", out _));
    }

    [Fact]
    public void AddMcpTools_RegistersABaseUrlProvider_ThatSeesTheRequest()
    {
        // An http binding follows a request that came in over https, which the provider can tell only from the request.
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("http://+:8080");
        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addresses);
        var server = new Mock<IServer>();
        server.Setup(x => x.Features).Returns(features);
        using var provider = BuildProvider(
            services =>
            {
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                services.AddSingleton(server.Object);
                services.AddMcpTools(ToolAssembly);
            },
            logs: null);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.example.com");
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        var baseUrl = provider.GetRequiredService<ISelfBaseUrlProvider>().GetBaseUrl();

        Assert.Equal("https://api.example.com", baseUrl);
    }

    [Fact]
    public void AddMcpTools_RegistersTheStartupCheck_AsAHostedService()
    {
        var services = new ServiceCollection();

        services.AddMcpTools(ToolAssembly);

        Assert.Contains(services, x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(McpToolsStartupCheck));
    }

    [Fact]
    public async Task StartupCheck_WarnsOncePerAssemblyWithoutXmlDocs_AndNamesTheFix()
    {
        // ParityRequest.FromPackage comes from xunit.core, whose .xml is not copied; the test assembly ships its own.
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(services => services.AddMcpTools(ToolAssembly), logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning);
        Assert.Contains("'xunit.core'", warning.Message);
        Assert.Contains("xunit.core.xml", warning.Message);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", warning.Message);
        Assert.Contains("<CopyDocumentationFilesFromPackages>true</CopyDocumentationFilesFromPackages>", warning.Message);
        Assert.Contains("McpScopeOptions.ArePropertyDescriptionsFromXmlDocs", warning.Message);
    }

    [Fact]
    public async Task StartupCheck_WarnsAboutAnUnreadableXmlDocFile_WithTheReason()
    {
        using var assemblyCopy = new IsolatedAssemblyCopy(typeof(McpExposeAttribute).Assembly, xmlDocContent: "<doc><members>");
        var summaryReader = new XmlDocSummaryReader();
        summaryReader.GetSummary(assemblyCopy.GetProperty(typeof(McpExposeAttribute), nameof(McpExposeAttribute.Scope)));
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(
            services =>
            {
                services.AddSingleton(summaryReader);
                services.AddMcpTools(ToolAssembly);
            },
            logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning && x.Message.Contains("could not be read"));
        Assert.Contains("'Api.ToMcp.Abstractions'", warning.Message);
        Assert.Contains(Path.ChangeExtension(assemblyCopy.Assembly.Location, ".xml"), warning.Message);
    }

    [Fact]
    public async Task StartupCheck_Warns_WhenMvcPreservesReferences()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(
            services =>
            {
                services.Configure<JsonOptions>(options => options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.Preserve);
                services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false);
            },
            logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning);
        Assert.Contains("ReferenceHandler.Preserve", warning.Message);
        Assert.Contains("\"structuredOutput\": false", warning.Message);
    }

    [Fact]
    public async Task StartupCheck_Warns_WhenMvcIgnoresCycles_InAResponseTypeThatCanContainItself()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(
            services =>
            {
                services.Configure<JsonOptions>(options => options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
                services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false);
            },
            logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning);
        Assert.Contains("ReferenceHandler.IgnoreCycles", warning.Message);
        Assert.Contains("StructuredNode", warning.Message);
        Assert.DoesNotContain("StructuredResponse", warning.Message);
        Assert.Contains("\"structuredOutput\": false", warning.Message);
    }

    [Theory]
    [InlineData(HttpServerSessionMode.Stateful)]
    [InlineData(HttpServerSessionMode.StatefulForInitializeClients)]
    public async Task StartupCheck_Warns_WhenAStatefulSessionRunsToolCallsWithoutTheirRequests(HttpServerSessionMode sessionMode)
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(
            services =>
            {
                services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false);
                services.Configure<HttpServerTransportOptions>(options => UsePerSessionExecutionContext(options, sessionMode));
            },
            logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logs, x => x.Level == LogLevel.Warning);
        Assert.Contains("PerSessionExecutionContext", warning.Message);
        Assert.Contains(sessionMode.ToString(), warning.Message);
        Assert.Contains("Authorization", warning.Message);
        Assert.Contains("scope validation", warning.Message);
    }

    [Fact]
    public async Task StartupCheck_DoesNotWarnAboutPerSessionExecutionContext_InStatelessMode_WhichIgnoresIt()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(
            services =>
            {
                services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false);
                services.Configure<HttpServerTransportOptions>(options => UsePerSessionExecutionContext(options, HttpServerSessionMode.Stateless));
            },
            logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logs, x => x.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartupCheck_StaysSilent_WhenTheOptionIsOff()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(services => services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false), logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logs, x => x.Level == LogLevel.Warning);
    }

    private static void UsePerSessionExecutionContext(HttpServerTransportOptions options, HttpServerSessionMode sessionMode)
    {
        options.SessionMode = sessionMode;
#pragma warning disable MCP9006 // The obsolete option is what the tests are about.
        options.PerSessionExecutionContext = true;
#pragma warning restore MCP9006
    }

    private static List<McpServerTool> BuildTools(Action<IServiceCollection> register)
    {
        using var provider = BuildProvider(register, logs: null);

        return provider.GetServices<McpServerTool>().ToList();
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> register, List<(LogLevel Level, string Message)>? logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IParityService, ParityService>();
        services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.AddProvider(new ListLoggerProvider(logs));
            }
        });
        register(services);

        return services.BuildServiceProvider();
    }

    private static bool IsGenerated(McpServerTool tool)
    {
        return tool.ProtocolTool.Name.StartsWith("generated_", StringComparison.Ordinal);
    }

    private static JsonElement GetToolInputProperty(IEnumerable<McpServerTool> tools, string toolName, string argumentName)
    {
        return tools.Single(x => x.ProtocolTool.Name == toolName).ProtocolTool.InputSchema.GetProperty("properties").GetProperty(argumentName);
    }

    private static JsonElement GetOutputSchema(IEnumerable<McpServerTool> tools, string toolName)
    {
        var outputSchema = tools.Single(x => x.ProtocolTool.Name == toolName).ProtocolTool.OutputSchema;

        Assert.NotNull(outputSchema);
        return outputSchema.Value;
    }

    private static string ToJson(McpServerTool tool)
    {
        return JsonSerializer.Serialize(tool.ProtocolTool, McpJsonUtilities.DefaultOptions);
    }
}
