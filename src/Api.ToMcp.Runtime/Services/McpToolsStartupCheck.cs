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

    /// <summary>
    /// Ends a generic type's runtime name with its arity, as in <c>List`1</c>.
    /// </summary>
    private const char GenericArityMarker = '`';

    private readonly IServiceProvider _serviceProvider;
    private readonly XmlDocPropertyDescriber _propertyDescriber;
    private readonly McpOutputSchemaBuilder _outputSchemaBuilder;
    private readonly ILogger<McpToolsStartupCheck> _logger;

    public McpToolsStartupCheck(
        IServiceProvider serviceProvider,
        XmlDocPropertyDescriber propertyDescriber,
        McpOutputSchemaBuilder outputSchemaBuilder,
        ILogger<McpToolsStartupCheck> logger)
    {
        _serviceProvider = serviceProvider;
        _propertyDescriber = propertyDescriber;
        _outputSchemaBuilder = outputSchemaBuilder;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var toolCount = _serviceProvider.GetServices<McpServerTool>().Count();
        _logger.LogDebug("Built {ToolCount} MCP tools.", toolCount);

        if (_outputSchemaBuilder.HasBuiltAnyValueSchema)
        {
            _logger.LogWarning(
                "The app's MVC JSON options preserve references (ReferenceHandler.Preserve), which no JSON Schema describes, so generated tools advertise an output schema that accepts any value. Set \"structuredOutput\": false in generator.json to advertise none.");
        }

        var typesWithIgnoredCycles = _outputSchemaBuilder.TypesWithIgnoredCycles;
        if (typesWithIgnoredCycles.Count > 0)
        {
            _logger.LogWarning(
                "The app's MVC JSON options ignore cycles (ReferenceHandler.IgnoreCycles), so MVC writes null where an object would contain itself, which no JSON Schema of the type can foresee. Generated tools returning {TypeNames}, which can contain themselves, advertise an output schema that accepts any value. Set \"structuredOutput\": false in generator.json to advertise none.",
                string.Join(", ", typesWithIgnoredCycles.Select(GetDisplayName).Order(StringComparer.Ordinal)));
        }

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

    /// <summary>
    /// The name as C# writes it, such as <c>List&lt;Order&gt;</c> rather than <c>List`1</c>.
    /// </summary>
    private static string GetDisplayName(Type type)
    {
        var arityStart = type.Name.IndexOf(GenericArityMarker);
        if (!type.IsGenericType || arityStart < 0)
        {
            return type.Name;
        }

        return $"{type.Name[..arityStart]}<{string.Join(", ", type.GetGenericArguments().Select(GetDisplayName))}>";
    }
}
