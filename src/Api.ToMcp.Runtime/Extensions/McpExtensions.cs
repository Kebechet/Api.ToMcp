using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Services;

namespace Api.ToMcp.Runtime;

public static class McpExtensions
{
    /// <summary>
    /// Registers MCP tool services including HTTP invoker and base URL provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="toolAssembly">Assembly containing generated MCP tools. Pass typeof(Program).Assembly or Assembly.GetExecutingAssembly().</param>
    public static IServiceCollection AddMcpTools(this IServiceCollection services, Assembly toolAssembly)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ISelfBaseUrlProvider, DefaultBaseUrlProvider>();
        services.AddHttpClient<IMcpHttpInvoker, McpHttpInvoker>();

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
    public static IEndpointRouteBuilder MapMcpEndpoint(this IEndpointRouteBuilder endpoints, string pattern = "mcp")
    {
        endpoints.MapMcp(pattern);
        return endpoints;
    }
}
