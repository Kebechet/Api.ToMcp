using Api.ToMcp.Runtime.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Builds every registered tool when the host starts and reports the assemblies whose XML documentation file was
/// missing or unreadable while their schemas were built. The SDK otherwise builds the tools on the first MCP request,
/// which would postpone both a tool that cannot be created and these warnings to that request. Hosted services start
/// before the server listens, so the warnings land in the startup log.
/// </summary>
internal sealed class McpToolsStartupCheck : IHostedService
{
    /// <summary>
    /// The option that turns the XML doc descriptions, and with them these warnings, off.
    /// </summary>
    private const string OptionName = $"{nameof(McpScopeOptions)}.{nameof(McpScopeOptions.ArePropertyDescriptionsFromXmlDocs)}";

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
                "Assembly '{AssemblyName}' has types in MCP tool schemas but no XML documentation file at '{XmlDocPath}', so its properties are described only where a [Description] attribute exists. Set <GenerateDocumentationFile>true</GenerateDocumentationFile> in the project that builds it, or <CopyDocumentationFilesFromPackages>true</CopyDocumentationFilesFromPackages> in the app when it comes from a NuGet package, and deploy the .xml next to the assembly; or set {OptionName} to false.",
                assembly.GetName().Name,
                xmlDocPath,
                OptionName);
        }

        foreach (var (assembly, xmlDocReadFailure) in _propertyDescriber.UnreadableXmlDocsByAssembly)
        {
            _logger.LogWarning(
                xmlDocReadFailure.Exception,
                "Assembly '{AssemblyName}' has types in MCP tool schemas but its XML documentation file '{XmlDocPath}' could not be read ({Reason}), so its properties are described only where a [Description] attribute exists. Rebuild and redeploy the file, or set {OptionName} to false.",
                assembly.GetName().Name,
                xmlDocReadFailure.XmlDocPath,
                xmlDocReadFailure.Exception.Message,
                OptionName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
