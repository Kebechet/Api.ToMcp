using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;

namespace Api.ToMcp.Runtime;

public static class McpExtensions
{
    /// <summary>
    /// Registers MCP tool services including HTTP invoker and base URL provider, and every tool of the assembly:
    /// the generated ones and any hand-written <see cref="McpServerToolTypeAttribute"/> class.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="toolAssembly">Assembly containing generated MCP tools. Pass typeof(Program).Assembly or Assembly.GetExecutingAssembly().</param>
    /// <param name="configureScopeOptions">Optional configuration for scope-based access control and schema descriptions. When null, all tools are accessible without scope validation.</param>
    [RequiresUnreferencedCode("Tools are discovered by reflection over the assembly, as in the SDK's WithToolsFromAssembly; trimming can remove them.")]
    public static IServiceCollection AddMcpTools(
        this IServiceCollection services,
        Assembly toolAssembly,
        Action<McpScopeOptions>? configureScopeOptions = null)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ISelfBaseUrlProvider, DefaultBaseUrlProvider>();
        services.AddHttpClient<IMcpHttpInvoker, McpHttpInvoker>();
        services.TryAddSingleton<XmlDocSummaryReader>();
        services.TryAddSingleton<XmlDocPropertyDescriber>();
        services.TryAddSingleton<McpOutputSchemaBuilder>();
        services.AddHostedService<McpToolsStartupCheck>();

        // Configure scope options (default is no scope checking)
        if (configureScopeOptions is not null)
        {
            services.Configure(configureScopeOptions);
        }
        else
        {
            services.Configure<McpScopeOptions>(_ => { });
        }

        // Register the official MCP server with HTTP transport
        services.AddMcpServer()
            .WithHttpTransport();

        // Same discovery as the SDK's WithToolsFromAssembly, with the XML doc schema hook passed to each tool.
        // A tool is created lazily from the built provider, as the SDK does it, because the SDK decides which
        // parameters are injected from DI (and so left out of the schema) by asking that provider.
        foreach (var (toolType, toolMethod) in FindToolMethods(toolAssembly))
        {
            services.AddSingleton<McpServerTool>(serviceProvider => CreateTool(serviceProvider, toolType, toolMethod));
        }

        return services;
    }

    /// <summary>
    /// Adds middleware to prevent infinite loops when MCP tools call back to the API.
    /// </summary>
    public static IApplicationBuilder UseMcpLoopPrevention(this IApplicationBuilder app)
    {
        return app.UseMiddleware<McpLoopPreventionMiddleware>();
    }

    /// <summary>
    /// Maps the MCP endpoint at the specified path.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern for the MCP endpoint.</param>
    /// <param name="allowAnonymous">If true, allows anonymous access to the MCP endpoint.</param>
    public static IEndpointRouteBuilder MapMcpEndpoint(this IEndpointRouteBuilder endpoints, string pattern = "mcp", bool allowAnonymous = false)
    {
        var endpoint = endpoints.MapMcp(pattern);
        if (allowAnonymous)
        {
            endpoint.AllowAnonymous();
        }
        return endpoints;
    }

    /// <summary>
    /// The methods <c>WithToolsFromAssembly</c> registers: every <see cref="McpServerToolAttribute"/> method, public or
    /// not, static or instance, of every <see cref="McpServerToolTypeAttribute"/> type in the assembly.
    /// </summary>
    private static IEnumerable<(Type ToolType, MethodInfo ToolMethod)> FindToolMethods(Assembly toolAssembly)
    {
        return toolAssembly.GetTypes()
            .Where(x => x.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(toolType => toolType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(toolMethod => toolMethod.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(toolMethod => (toolType, toolMethod)));
    }

    private static McpServerTool CreateTool(IServiceProvider serviceProvider, Type toolType, MethodInfo toolMethod)
    {
        var scopeOptions = serviceProvider.GetRequiredService<IOptions<McpScopeOptions>>().Value;
        var createOptions = new McpServerToolCreateOptions
        {
            Services = serviceProvider,
            SchemaCreateOptions = scopeOptions.ArePropertyDescriptionsFromXmlDocs
                ? serviceProvider.GetRequiredService<XmlDocPropertyDescriber>().SchemaCreateOptions
                : null
        };

        // The SDK would build this schema from OutputSchemaType with its own JSON options; an explicit one wins.
        if (toolMethod.GetCustomAttribute<McpGeneratedToolAttribute>() is { } generatedTool &&
            toolMethod.GetCustomAttribute<McpServerToolAttribute>() is { UseStructuredContent: true, OutputSchemaType: { } outputSchemaType })
        {
            createOptions.OutputSchema = serviceProvider.GetRequiredService<McpOutputSchemaBuilder>()
                .Build(outputSchemaType, generatedTool.IsOutputNullable, createOptions.SchemaCreateOptions);
        }

        if (toolMethod.IsStatic)
        {
            return McpServerTool.Create(toolMethod, options: createOptions);
        }

        return McpServerTool.Create(toolMethod, requestContext => CreateToolInstance(requestContext.Services, toolType), createOptions);
    }

    private static object CreateToolInstance(IServiceProvider? services, Type toolType)
    {
        return services is not null
            ? ActivatorUtilities.CreateInstance(services, toolType)
            : Activator.CreateInstance(toolType)!;
    }
}
