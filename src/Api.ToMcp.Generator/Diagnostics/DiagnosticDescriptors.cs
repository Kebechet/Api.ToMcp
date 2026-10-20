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
            messageFormat: "Action '{0}.{1}' was skipped because its route contains '/mcp' (loop prevention).",
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
            messageFormat: "No MCP tools are generated: {0} mode selects none of the project's HTTP actions. {1}.",
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
    }
}
