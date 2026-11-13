using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;
using Api.ToMcp.Runtime.Middleware;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;

namespace Api.ToMcp.Runtime;

public static class McpExtensions
{
    /// <summary>
    /// The class the generator emits into the app with the list of the tools it generated, and that list.
    /// </summary>
    private const string GeneratedToolsInfoTypeName = "Api.ToMcp.Generated.McpToolsInfo";
    private const string GeneratedToolTypesFieldName = "ToolTypes";

    /// <summary>
    /// The pattern <see cref="MapMcpEndpoint"/> maps the MCP endpoint at unless told otherwise.
    /// </summary>
    internal const string DefaultMcpPattern = "mcp";

    /// <summary>
    /// Registers MCP tool services, among them the invoker that serves a tool's API request in process, through the
    /// request pipeline the host builds for the server, and every tool of the assembly: the generated ones and any
    /// hand-written <see cref="McpServerToolTypeAttribute"/> class.
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
        services.TryAddSingleton<IHttpContextFactory, DefaultHttpContextFactory>();
        services.TryAddSingleton<InProcessApiPipeline>();
        CaptureThePipelineTheHostBuilds(services);
        services.AddHttpClient<IMcpHttpInvoker, McpHttpInvoker>((httpClient, serviceProvider) => new McpHttpInvoker(
            httpClient,
            serviceProvider.GetRequiredService<ISelfBaseUrlProvider>(),
            serviceProvider.GetRequiredService<IHttpContextAccessor>(),
            serviceProvider.GetRequiredService<ILogger<McpHttpInvoker>>(),
            serviceProvider.GetRequiredService<IOptions<McpScopeOptions>>(),
            serviceProvider.GetRequiredService<InProcessApiPipeline>(),
            serviceProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value.ForwardedProtoHeaderName));
        services.TryAddSingleton<XmlDocSummaryReader>();
        services.TryAddSingleton<XmlDocPropertyDescriber>();
        services.TryAddSingleton<McpOutputSchemaBuilder>();
        services.TryAddSingleton<McpToolScopes>();
        services.TryAddSingleton<McpScopeChallenge>();
        // McpAuthenticationHandler is not registered: it needs UrlEncoder, which only AddAuthentication registers, and a
        // host that validates its services on build, as in Development, would fail for an app without authentication.
        // AuthenticationHandlerProvider creates a scheme's handler with ActivatorUtilities when it is not registered.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<AuthenticationOptions>, McpAuthenticationSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<McpAuthenticationOptions>, McpAuthenticationSetup>());
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
            .WithHttpTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpScopeValidationFilter.Create));

        // Same discovery as the SDK's WithToolsFromAssembly, with the XML doc schema hook passed to each tool.
        // A tool is created lazily from the built provider, as the SDK does it, because the SDK decides which
        // parameters are injected from DI (and so left out of the schema) by asking that provider.
        var generatedToolTypes = FindGeneratedToolTypes(toolAssembly);
        foreach (var (toolType, toolMethod) in FindToolMethods(toolAssembly))
        {
            var isGenerated = generatedToolTypes.Contains(toolType);
            services.AddSingleton<McpServerTool>(serviceProvider => CreateTool(serviceProvider, toolType, toolMethod, isGenerated));
        }

        return services;
    }

    /// <summary>
    /// Adds middleware to prevent infinite loops when MCP tools call back to the API: the MCP endpoint
    /// <see cref="MapMcpEndpoint"/> maps refuses a request a tool sent, wherever it is mapped.
    /// </summary>
    public static IApplicationBuilder UseMcpLoopPrevention(this IApplicationBuilder app)
    {
        return app.UseMiddleware<McpLoopPreventionMiddleware>();
    }

    /// <summary>
    /// Maps the MCP endpoint at the specified path. With <see cref="UseMcpLoopPrevention"/>, it refuses a request that
    /// a tool sent. With <see cref="McpScopeOptions.ResourceMetadata"/> set, it signs the caller in through the MCP SDK's
    /// scheme, requires a signed-in caller who meets the app's default and fallback policies, answers a caller who is not
    /// signed in with a <c>401</c> challenge that points to the protected resource metadata, and answers a tool call that
    /// scope validation refuses with a scope challenge.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern for the MCP endpoint.</param>
    /// <param name="allowAnonymous">If true, allows anonymous access to the MCP endpoint. With
    /// <see cref="McpScopeOptions.ResourceMetadata"/> set, a client can then list the tools before it signs in, and is
    /// asked to sign in when it calls a tool that scope validation refuses.</param>
    public static IEndpointRouteBuilder MapMcpEndpoint(this IEndpointRouteBuilder endpoints, string pattern = DefaultMcpPattern, bool allowAnonymous = false)
    {
        var endpoint = endpoints.MapMcp(pattern);
        var isSignInDiscoverable = endpoints.ServiceProvider.GetRequiredService<IOptions<McpScopeOptions>>().Value.ResourceMetadata is not null;
        var scopeChallenge = isSignInDiscoverable ? endpoints.ServiceProvider.GetRequiredService<McpScopeChallenge>() : null;
        endpoint.Add(builder =>
        {
            if (builder.RequestDelegate is null)
            {
                return;
            }

            var requestDelegate = scopeChallenge is not null
                ? scopeChallenge.ChallengeRefusedToolCalls(builder.RequestDelegate)
                : builder.RequestDelegate;
            builder.RequestDelegate = McpLoopPreventionMiddleware.RefuseInternalCalls(requestDelegate);
        });
        if (isSignInDiscoverable)
        {
            endpoint.RequireAuthorization(allowAnonymous
                ? McpEndpointAuthorization.CreateAnonymousPolicy()
                : McpEndpointAuthorization.CreateSignedInPolicy(endpoints.ServiceProvider.GetRequiredService<IHttpContextAccessor>()));
            if (allowAnonymous)
            {
                WarnAboutAnonymousStatefulSessions(endpoints.ServiceProvider);
            }
        }

        if (allowAnonymous)
        {
            endpoint.AllowAnonymous();
        }
        return endpoints;
    }

    /// <summary>
    /// A stateful session belongs to the user who started it, and the SDK refuses a request of another user in it, so a
    /// client that starts one without a token and signs in when a tool call asks for it cannot go on in that session.
    /// </summary>
    private static void WarnAboutAnonymousStatefulSessions(IServiceProvider services)
    {
        var sessionMode = services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.SessionMode;
        if (sessionMode == HttpServerSessionMode.Stateless)
        {
            return;
        }

        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpExtensions).FullName!).LogWarning(
            "MapMcpEndpoint allows anonymous callers with OAuth sign-in discovery on, and SessionMode is {SessionMode}. The SDK ties a stateful session to the user who started it, so a client that starts one without a token and signs in when a tool call asks for it is refused with 403 in that session. Use HttpServerSessionMode.Stateless, or require sign-in on the endpoint.",
            sessionMode);
    }

    /// <summary>
    /// Wraps the factory the host builds the app's request pipeline with, so <see cref="InProcessApiPipeline"/> gets that
    /// pipeline: the one the host registered when it did so before the app's services, as <c>WebApplicationBuilder</c>
    /// does, and otherwise the default the host would register, which its TryAdd then leaves in place.
    /// </summary>
    private static void CaptureThePipelineTheHostBuilds(IServiceCollection services)
    {
        var hostFactory = services.LastOrDefault(x => x.ServiceType == typeof(IApplicationBuilderFactory) && !x.IsKeyedService);
        if (hostFactory is not null)
        {
            services.Remove(hostFactory);
        }

        services.Add(ServiceDescriptor.Describe(
            typeof(IApplicationBuilderFactory),
            serviceProvider => new PipelineCapturingApplicationBuilderFactory(
                CreateHostFactory(serviceProvider, hostFactory),
                serviceProvider.GetRequiredService<InProcessApiPipeline>()),
            hostFactory?.Lifetime ?? ServiceLifetime.Singleton));
    }

    private static IApplicationBuilderFactory CreateHostFactory(IServiceProvider serviceProvider, ServiceDescriptor? hostFactory)
    {
        if (hostFactory is null)
        {
            return new ApplicationBuilderFactory(serviceProvider);
        }

        return (IApplicationBuilderFactory)(hostFactory.ImplementationInstance ??
            hostFactory.ImplementationFactory?.Invoke(serviceProvider) ??
            ActivatorUtilities.CreateInstance(serviceProvider, hostFactory.ImplementationType!));
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

    /// <summary>
    /// The tool classes the generator lists in the app's <c>McpToolsInfo</c>; none in an assembly it did not run on.
    /// </summary>
    private static HashSet<Type> FindGeneratedToolTypes(Assembly toolAssembly)
    {
        var toolTypes = toolAssembly
            .GetType(GeneratedToolsInfoTypeName)?
            .GetField(GeneratedToolTypesFieldName, BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null) as Type[];

        return [.. toolTypes ?? []];
    }

    private static McpServerTool CreateTool(IServiceProvider serviceProvider, Type toolType, MethodInfo toolMethod, bool isGenerated)
    {
        var scopeOptions = serviceProvider.GetRequiredService<IOptions<McpScopeOptions>>().Value;
        var describingOptions = scopeOptions.ArePropertyDescriptionsFromXmlDocs
            ? serviceProvider.GetRequiredService<XmlDocPropertyDescriber>().SchemaCreateOptions
            : null;
        var createOptions = new McpServerToolCreateOptions
        {
            Services = serviceProvider,
            SchemaCreateOptions = ConvertedPropertySchema.Apply(describingOptions),
            SerializerOptions = isGenerated ? GeneratedToolArguments.SerializerOptions : null
        };

        // The SDK would build this schema from OutputSchemaType with its own JSON options; an explicit one wins.
        if (toolMethod.GetCustomAttribute<McpGeneratedToolAttribute>() is { } generatedTool &&
            toolMethod.GetCustomAttribute<McpServerToolAttribute>() is { UseStructuredContent: true, OutputSchemaType: { } outputSchemaType })
        {
            createOptions.OutputSchema = serviceProvider.GetRequiredService<McpOutputSchemaBuilder>()
                .Build(outputSchemaType, generatedTool.IsOutputNullable, describingOptions);
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
