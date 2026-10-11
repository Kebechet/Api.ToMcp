using System.Reflection;
using System.Text.Json;
using Api.ToMcp.Abstractions;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
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
        var expectedTools = BuildTools(services => services.AddMcpServer().WithToolsFromAssembly(ToolAssembly));
        var actualTools = BuildTools(services => services.AddMcpTools(ToolAssembly, options => options.ArePropertyDescriptionsFromXmlDocs = false));

        Assert.Equal(3, actualTools.Count);
        Assert.Equal(expectedTools.Select(ToJson).OrderBy(x => x), actualTools.Select(ToJson).OrderBy(x => x));
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
