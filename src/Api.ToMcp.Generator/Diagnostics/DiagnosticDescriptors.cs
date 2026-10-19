using Microsoft.CodeAnalysis;

namespace Api.ToMcp.Generator.Diagnostics
{
    internal static class DiagnosticDescriptors
    {
        public static readonly DiagnosticDescriptor ConfigParseError = new DiagnosticDescriptor(
            id: "MCP001",
            title: "Configuration Parse Error",
            messageFormat: "Failed to parse generator.json: {0}",
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

        public static readonly DiagnosticDescriptor NoConfigFile = new DiagnosticDescriptor(
            id: "MCP005",
            title: "No Configuration File",
            messageFormat: "No generator.json found. Using default configuration (SelectedOnly mode with empty include list).",
            category: "Api.ToMcp.Generator",
            DiagnosticSeverity.Info,
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
