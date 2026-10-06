using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Builds every registered tool when the host starts and reports the assemblies whose XML documentation file was
/// missing while their schemas were built. The SDK otherwise builds the tools on the first MCP request, which would
/// postpone both a tool that cannot be created and these warnings to that request. Hosted services start before
/// the server listens, so the warnings land in the startup log.
/// </summary>
internal sealed class McpToolsStartupCheck : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly XmlDocPropertyDescriber _propertyDescriber;
    private readonly ILogger<McpToolsStartupCheck> _logger;

    public McpToolsStartupCheck(
        IServiceProvider serviceProvider,
        XmlDocPropertyDescriber propertyDescriber,
        ILogger<McpToolsStartupCheck> logger)
    {
        _serviceProvider = serviceProvider;
        _propertyDescriber = propertyDescriber;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var toolCount = _serviceProvider.GetServices<McpServerTool>().Count();
        _logger.LogDebug("Built {ToolCount} MCP tools.", toolCount);

        foreach (var (assembly, xmlDocPath) in _propertyDescriber.MissingXmlDocPathsByAssembly)
        {
            _logger.LogWarning(
                "Assembly '{AssemblyName}' has types in MCP tool schemas but no XML documentation file at '{XmlDocPath}', " +
                "so its properties are described only where a [Description] attribute exists. " +
                "Set <GenerateDocumentationFile>true</GenerateDocumentationFile> in that project and deploy the .xml file " +
                "next to the assembly, or set McpScopeOptions.ArePropertyDescriptionsFromXmlDocs to false.",
                assembly.GetName().Name,
                xmlDocPath);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
