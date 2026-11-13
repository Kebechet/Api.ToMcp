using System;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// The MCP endpoint's default route, at which and under which no action gets a tool. The runtime's loop prevention
    /// refuses a tool's request to every path under it, and the endpoint <c>MapMcpEndpoint</c> maps refuses one wherever
    /// it is mapped.
    /// </summary>
    internal static class McpEndpointRoute
    {
        /// <summary>
        /// The runtime's <c>McpExtensions.DefaultMcpPattern</c> after a slash, as the loop prevention middleware has it.
        /// </summary>
        public const string Route = "/mcp";

        private const string PathSeparator = RouteTokens.PathSeparatorText;

        /// <summary>
        /// Whether <paramref name="route"/> is <c>/mcp</c> or a path under it, with or without its leading slash and in any
        /// letter case, comparing whole segments as the loop prevention middleware does. A route that only holds the
        /// letters, such as <c>/api/mcp</c>, <c>/mcpservers</c> or <c>/api/settings/mcp-status</c>, is the app's own. See
        /// https://github.com/Kebechet/Api.ToMcp/issues/53.
        /// </summary>
        public static bool Covers(string route)
        {
            var path = route.StartsWith(PathSeparator, StringComparison.Ordinal) ? route : PathSeparator + route;

            return path.Equals(Route, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Route + PathSeparator, StringComparison.OrdinalIgnoreCase);
        }
    }
}
