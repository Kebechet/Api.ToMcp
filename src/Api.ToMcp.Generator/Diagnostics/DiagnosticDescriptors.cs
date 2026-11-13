using Microsoft.CodeAnalysis;

namespace Api.ToMcp.Generator.Diagnostics
{
    internal static class DiagnosticDescriptors
    {
        /// <summary>
        /// The generator.json is malformed, has a value of the wrong type or an unknown mode, or could not be read. The
        /// generator falls back as the message says, so the build still succeeds, possibly with no tools.
        /// </summary>
        public static readonly DiagnosticDescriptor InvalidConfig = new DiagnosticDescriptor(
            id: "MCP001",
            title: "Invalid Configuration",
            messageFormat: "generator.json cannot be used as written, so {0}: {1}",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedHttpMethod = new DiagnosticDescriptor(
            id: "MCP002",
            title: "Unsupported HTTP Method",
            messageFormat: "Action '{0}.{1}' uses HTTP method '{2}' which is not supported in v1. Only GET and POST are supported.",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedReturnType = new DiagnosticDescriptor(
            id: "MCP003",
            title: "Unsupported Return Type",
            messageFormat: "Action '{0}.{1}' has return type '{2}' which is not supported. Skipping.",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor McpRouteSkipped = new DiagnosticDescriptor(
            id: "MCP004",
            title: "MCP Route Skipped",
            messageFormat: "Action '{0}.{1}' was skipped because its route is '/mcp' or under it (loop prevention)",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        /// <summary>
        /// The config selects none of the actions it could expose, so the app's MCP server lists no tools. A warning
        /// because nothing else says so: the app builds and starts as usual.
        /// </summary>
        public static readonly DiagnosticDescriptor NoToolsGenerated = new DiagnosticDescriptor(
            id: "MCP006",
            title: "No MCP Tools Generated",
            messageFormat: "No MCP tools are generated: {0}. {1}.",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// The MCP SDK registers tools by name with <c>TryAdd</c>, so of several tools with one name it keeps one and drops
        /// the others without an error. A <c>ToolName</c> on a base action names the tool of every controller that inherits
        /// it, and overloads share the name their method name derives.
        /// </summary>
        public static readonly DiagnosticDescriptor DuplicateToolName = new DiagnosticDescriptor(
            id: "MCP007",
            title: "Duplicate tool name",
            messageFormat: "Tool name '{0}' is given to {1} tools: {2}. The MCP SDK keeps only one tool per name and drops the others without an error.",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Give each tool a name of its own with [McpExpose(ToolName = ...)], on an override of an inherited action where the name comes from its base action.");

        /// <summary>
        /// An include or exclude entry names no controller the generator finds, nor an action of one, so it selects or
        /// excludes nothing. The usual causes are a typo, as entries are case-sensitive, and the name of a base
        /// controller, whose actions are matched by the name of each controller that inherits them. An entry naming an
        /// action that <c>[McpIgnore]</c> or an <c>/mcp</c> route keeps from MCP names something, so it gets none.
        /// </summary>
        public static readonly DiagnosticDescriptor UnmatchedConfigEntry = new DiagnosticDescriptor(
            id: "MCP008",
            title: "Config entry matches nothing",
            messageFormat: "The {0} entry '{1}' in generator.json names no controller or action of the project, so it {2}: {3}",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// The generator cannot build the request MVC serves the action at, so it leaves the tool out rather than generate
        /// one that would not compile or would call another endpoint. The build still succeeds, with the other tools.
        /// </summary>
        public static readonly DiagnosticDescriptor UnbuildableRoute = new DiagnosticDescriptor(
            id: "MCP009",
            title: "Tool route cannot be built",
            messageFormat: "No MCP tool is generated for action '{0}.{1}': {2}",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Give the action a parameter, or a property of one, for every required route value, or mark it [McpIgnore] to keep it from MCP without the warning.");

        /// <summary>
        /// The generator cannot tell at build time what a Minimal API endpoint serves, or cannot send what it reads: its
        /// route, from a pattern or a group prefix that is not a constant, or a route group it cannot follow to the app;
        /// its handler; or a form, a header or a body it cannot tell. It leaves the tool out, as MCP009 does for an action.
        /// </summary>
        public static readonly DiagnosticDescriptor UnbuildableEndpoint = new DiagnosticDescriptor(
            id: "MCP010",
            title: "Endpoint cannot become a tool",
            messageFormat: "No MCP tool is generated for endpoint '{0}.{1}': {2}",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Map the endpoint with a constant route pattern, in route groups the generator can follow to the app, and with a handler it can read, or mark the handler [McpIgnore] to keep it from MCP without the warning.");

        /// <summary>
        /// A tool calls its endpoint with one HTTP method, the first one a tool sends that <c>MapMethods</c> lists, so the
        /// endpoint's other methods have no tool.
        /// </summary>
        public static readonly DiagnosticDescriptor EndpointHasSeveralHttpMethods = new DiagnosticDescriptor(
            id: "MCP011",
            title: "Endpoint mapped for several HTTP methods",
            messageFormat: "The MCP tool of endpoint '{0}.{1}' calls it with {2} only, though it is mapped for {3} as well",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Map each HTTP method with a call of its own, such as MapGet and MapPost, to give each of them a tool.");

        /// <summary>
        /// AllExceptExcluded mode selects Minimal API endpoints only when generator.json opts in, so that updating the
        /// package exposes none a project did not choose. An info, which says the opt-in exists where the project has
        /// endpoints and its config does not say whether it wants them.
        /// </summary>
        public static readonly DiagnosticDescriptor EndpointsLeftOut = new DiagnosticDescriptor(
            id: "MCP012",
            title: "Minimal API endpoints not selected",
            messageFormat: "AllExceptExcluded mode selects none of the project's {0} Minimal API {1}, as generator.json does not set \"minimalApis\": set it to true to select them, or to false to keep them out without this message",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "An include entry naming an endpoint or its class, and [McpExpose] on its handler or class, select an endpoint whatever \"minimalApis\" says.");

        /// <summary>
        /// An include entry such as <c>"Orders"</c> names a controller, <c>OrdersController</c>, and a class of the same
        /// name that declares Minimal API endpoints. It selected the controller before endpoints became tools, so it selects
        /// only the controller's actions rather than expose the endpoints too. An info, as it selects what it did before.
        /// </summary>
        public static readonly DiagnosticDescriptor IncludeEntryNamesControllerAndEndpoints = new DiagnosticDescriptor(
            id: "MCP013",
            title: "Include entry names a controller and Minimal API endpoints",
            messageFormat: "The include entry '{0}' in generator.json names a controller and the class '{1}', which declares Minimal API endpoints; it selects only the controller's actions: {2}",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);
    }
}
