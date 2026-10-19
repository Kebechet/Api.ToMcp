using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.ToMcp.Abstractions;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
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
        // Generated tools are left out: their output schema is meant to differ, it follows MVC's JSON options.
        var expectedTools = BuildTools(services => services.AddMcpServer().WithToolsFromAssembly(ToolAssembly)).Where(x => !IsGenerated(x));
        var actualTools = BuildTools(services => services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false)).Where(x => !IsGenerated(x)).ToList();

        Assert.Equal(4, actualTools.Count);
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

    [Fact]
    public async Task StartupCheck_StaysSilent_WhenTheOptionIsOff()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        using var provider = BuildProvider(services => services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false), logs);
        var startupCheck = ActivatorUtilities.CreateInstance<McpToolsStartupCheck>(provider);

        await startupCheck.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logs, x => x.Level == LogLevel.Warning);
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

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _logs;

        public ListLoggerProvider(List<(LogLevel Level, string Message)> logs)
        {
            _logs = logs;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new ListLogger(_logs);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _logs;

        public ListLogger(List<(LogLevel Level, string Message)> logs)
        {
            _logs = logs;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
