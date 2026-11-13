namespace Api.ToMcp.Runtime;

/// <summary>
/// The characters a route is written with, which the runtime reads and builds the paths of requests with.
/// </summary>
internal static class RouteTokens
{
    /// <summary>
    /// Separates the segments of a path, and starts one at the app's root.
    /// </summary>
    public const char PathSeparator = '/';
}
