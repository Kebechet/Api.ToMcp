using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Builds every registered tool when the host starts and reports the assemblies whose XML documentation file was
/// missing or unreadable while their schemas were built, a transport setting under which the tools cannot reach the MCP
/// request, and OAuth sign-in discovery that lists no authorization server or names no OAuth scope for a scope the tools
/// require. The SDK otherwise builds the tools on the first MCP request, which would postpone both a tool that
/// cannot be created and these warnings to that request. Hosted services start before the server listens, so the
/// warnings land in the startup log.
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

    /// <summary>
    /// The formatter <c>AddNewtonsoftJson</c> puts in place of System.Text.Json's, matched by name so the runtime takes no
    /// dependency on Newtonsoft.Json.
    /// </summary>
    private const string NewtonsoftJsonOutputFormatterTypeName = "Microsoft.AspNetCore.Mvc.Formatters.NewtonsoftJsonOutputFormatter";

    /// <summary>
    /// How many tools a warning names before it only counts the rest, so a large app does not log a wall of names.
    /// </summary>
    private const int MaxListedToolNames = 10;

    private readonly IServiceProvider _serviceProvider;
    private readonly XmlDocPropertyDescriber _propertyDescriber;
    private readonly McpOutputSchemaBuilder _outputSchemaBuilder;
    private readonly HttpServerTransportOptions _transportOptions;
    private readonly McpScopeOptions _scopeOptions;
    private readonly McpToolScopes _toolScopes;
    private readonly MvcOptions _mvcOptions;
    private readonly ILogger<McpToolsStartupCheck> _logger;

    public McpToolsStartupCheck(
        IServiceProvider serviceProvider,
        XmlDocPropertyDescriber propertyDescriber,
        McpOutputSchemaBuilder outputSchemaBuilder,
        IOptions<HttpServerTransportOptions> transportOptions,
        IOptions<McpScopeOptions> scopeOptions,
        McpToolScopes toolScopes,
        IOptions<MvcOptions> mvcOptions,
        ILogger<McpToolsStartupCheck> logger)
    {
        _serviceProvider = serviceProvider;
        _propertyDescriber = propertyDescriber;
        _outputSchemaBuilder = outputSchemaBuilder;
        _transportOptions = transportOptions.Value;
        _scopeOptions = scopeOptions.Value;
        _toolScopes = toolScopes;
        _mvcOptions = mvcOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var tools = _serviceProvider.GetServices<McpServerTool>().ToList();
        _logger.LogDebug("Built {ToolCount} MCP tools.", tools.Count);

        if (RunsToolCallsWithoutTheirRequests(_transportOptions))
        {
            _logger.LogWarning(
                "HttpServerTransportOptions.PerSessionExecutionContext is true and SessionMode is {SessionMode}, so a stateful session runs every tool call in the context of its initialize request, whose HttpContext is gone by then. The generated tools then forward no Authorization header to the API, and scope validation, when configured, refuses every call. Set PerSessionExecutionContext to false.",
                _transportOptions.SessionMode);
        }

        await WarnAboutSignInDiscoveryAsync();

        if (_outputSchemaBuilder.HasBuiltAnyValueSchema)
        {
            _logger.LogWarning(
                "The app's MVC JSON options preserve references (ReferenceHandler.Preserve), which no JSON Schema describes, so generated tools advertise an output schema that accepts any value. Set \"structuredOutput\": false in generator.json to advertise none.");
        }

        // The SDK writes a hand-written tool's structured content itself, so only the generated tools' schemas meet MVC.
        var toolsWithOutputSchema = tools
            .Where(x => x.Metadata.OfType<McpGeneratedToolAttribute>().Any() && x.ProtocolTool.OutputSchema is not null)
            .Select(x => x.ProtocolTool.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (toolsWithOutputSchema.Count > 0 && WritesResponsesWithNewtonsoftJson(_mvcOptions))
        {
            _logger.LogWarning(
                "MVC writes responses with Newtonsoft.Json (AddNewtonsoftJson), but the output schemas of generated tools are built from MVC's System.Text.Json options, which Newtonsoft.Json does not use: [JsonProperty] names, [Newtonsoft.Json.JsonIgnore] and the converters of SerializerSettings, such as StringEnumConverter, are not in them, so a client that validates the structured content against them can reject the result. Generated tools with an output schema: {ToolNames}. The tools of Minimal API endpoints among them are right, as System.Text.Json writes their responses whatever MVC uses. Set \"structuredOutput\": false in generator.json to advertise none.",
                ListToolNames(toolsWithOutputSchema));
        }

        var typesWithIgnoredCycles = _outputSchemaBuilder.TypesWithIgnoredCycles;
        if (typesWithIgnoredCycles.Count > 0)
        {
            _logger.LogWarning(
                "The app's MVC JSON options ignore cycles (ReferenceHandler.IgnoreCycles), so MVC writes null where an object would contain itself, which no JSON Schema of the type can foresee. Generated tools returning {TypeNames}, in which an object can contain itself, advertise an output schema that accepts any value. Set \"structuredOutput\": false in generator.json to advertise none.",
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
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task WarnAboutSignInDiscoveryAsync()
    {
        var resourceMetadata = _scopeOptions.ResourceMetadata;
        if (resourceMetadata is null)
        {
            return;
        }

        if (resourceMetadata.AuthorizationServers.Count == 0)
        {
            _logger.LogWarning(
                "McpScopeOptions.ResourceMetadata lists no authorization server, so an MCP client that reads it cannot find where to sign the user in. Add the issuer of your access tokens to ResourceMetadata.AuthorizationServers.");
        }

        // The names fill scopes_supported only when the app lists no scopes itself, and name scopes in challenges only
        // when scope validation is on.
        var areNamesUsed = resourceMetadata.ScopesSupported.Count == 0 || _scopeOptions.ClaimToScopeMapper is not null;
        _toolScopes.GetScopeNames(_toolScopes.AllRequiredScopes, out var unnamedScope);
        if (areNamesUsed && unnamedScope != McpScope.None)
        {
            _logger.LogWarning(
                "Generated tools require the scope {Scope}, for which McpScopeOptions.ScopeNames names no OAuth scope, so scopes_supported leaves it out and a tool call that scope validation refuses for it gets an error result instead of a scope challenge. Add an entry for each flag, as in ScopeNames[McpScope.Read] = \"mcp:read\".",
                unnamedScope);
        }

        await ReportMissingSignInSchemeAsync();
    }

    /// <summary>
    /// Every request to the MCP endpoint fails when the MCP SDK's scheme is missing, which happens when the app registered
    /// no scheme of its own, or when the scheme it forwards authentication to is.
    /// </summary>
    private async Task ReportMissingSignInSchemeAsync()
    {
        var schemeProvider = _serviceProvider.GetService<IAuthenticationSchemeProvider>();
        var mcpScheme = schemeProvider is not null
            ? await schemeProvider.GetSchemeAsync(McpAuthenticationDefaults.AuthenticationScheme)
            : null;
        if (schemeProvider is null || mcpScheme is null)
        {
            _logger.LogError(
                "OAuth sign-in discovery is on, but the app registered no authentication scheme that validates its access tokens, so every request to the MCP endpoint fails. Register one, as in AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(...).");
            return;
        }

        var mcpOptions = _serviceProvider.GetRequiredService<IOptionsMonitor<McpAuthenticationOptions>>().Get(McpAuthenticationDefaults.AuthenticationScheme);
        if (mcpOptions.ForwardDefaultSelector is not null)
        {
            return;
        }

        var forwardScheme = mcpOptions.ForwardAuthenticate ?? mcpOptions.ForwardDefault;
        if (forwardScheme is null)
        {
            _logger.LogError(
                "OAuth sign-in discovery is on, but McpAuthenticationOptions.ForwardAuthenticate of the MCP SDK's scheme '{McpScheme}' is null, so it reads no access token and the MCP endpoint signs no caller in. Set it to the scheme that validates your access tokens.",
                McpAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var forwardTarget = await schemeProvider.GetSchemeAsync(forwardScheme);
        if (forwardTarget is null)
        {
            _logger.LogError(
                "OAuth sign-in discovery is on, and the MCP SDK's scheme '{McpScheme}' reads the access token through the scheme '{ForwardScheme}', which is not registered, so every request to the MCP endpoint fails. Set McpAuthenticationOptions.ForwardAuthenticate to the scheme that validates your access tokens: services.Configure<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme, options => options.ForwardAuthenticate = \"<scheme>\").",
                McpAuthenticationDefaults.AuthenticationScheme,
                forwardScheme);
        }
    }

    /// <summary>
    /// The names, up to <see cref="MaxListedToolNames"/> of them, then how many more there are.
    /// </summary>
    internal static string ListToolNames(IReadOnlyList<string> names)
    {
        var listed = string.Join(", ", names.Take(MaxListedToolNames));
        return names.Count > MaxListedToolNames
            ? $"{listed} and {names.Count - MaxListedToolNames} more"
            : listed;
    }

    /// <summary>
    /// Whether MVC writes an action's response with Newtonsoft's formatter, which <c>AddNewtonsoftJson</c> puts in place
    /// of System.Text.Json's, or one derived from it. A tool's request sends no <c>Accept</c> header, so MVC takes the
    /// first formatter that can write the value: the first of the two in the list. A formatter that wraps Newtonsoft's
    /// rather than deriving from it cannot be told apart.
    /// </summary>
    private static bool WritesResponsesWithNewtonsoftJson(MvcOptions mvcOptions)
    {
        var jsonFormatter = mvcOptions.OutputFormatters.FirstOrDefault(formatter =>
            formatter is SystemTextJsonOutputFormatter || IsOrDerivesFrom(formatter.GetType(), NewtonsoftJsonOutputFormatterTypeName));
        return jsonFormatter is not null && jsonFormatter is not SystemTextJsonOutputFormatter;
    }

    private static bool IsOrDerivesFrom(Type type, string fullName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a stateful session calls its tools in the execution context of the request that started it rather than
    /// of the request that carried the call, which hides that request's <c>HttpContext</c> from the invoker. A
    /// stateless request is served in its own context whatever the option says.
    /// </summary>
    private static bool RunsToolCallsWithoutTheirRequests(HttpServerTransportOptions transportOptions)
    {
#pragma warning disable MCP9006 // The SDK marks the option obsolete, but it still compiles and takes effect.
        return transportOptions.PerSessionExecutionContext && transportOptions.SessionMode != HttpServerSessionMode.Stateless;
#pragma warning restore MCP9006
    }

    /// <summary>
    /// The name with its type arguments, such as <c>List&lt;Order&gt;</c> rather than <c>List`1</c>.
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
