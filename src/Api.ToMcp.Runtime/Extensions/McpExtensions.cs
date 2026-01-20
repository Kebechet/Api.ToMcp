using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;

namespace Api.ToMcp.Runtime;

public static class McpExtensions
{
    /// <summary>
    /// Registers MCP tool services including HTTP invoker and base URL provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="toolAssembly">Assembly containing generated MCP tools. Pass typeof(Program).Assembly or Assembly.GetExecutingAssembly().</param>
    /// <param name="configureScopeOptions">Optional configuration for scope-based access control. When null, all tools are accessible without scope validation.</param>
    public static IServiceCollection AddMcpTools(
        this IServiceCollection services,
        Assembly toolAssembly,
        Action<McpScopeOptions>? configureScopeOptions = null)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ISelfBaseUrlProvider, DefaultBaseUrlProvider>();
        services.AddHttpClient<IMcpHttpInvoker, McpHttpInvoker>();

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
            .WithHttpTransport()
            .WithToolsFromAssembly(toolAssembly);

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
}
