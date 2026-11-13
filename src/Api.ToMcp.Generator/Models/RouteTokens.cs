namespace Api.ToMcp.Generator.Models;

/// <summary>
/// The characters ASP.NET Core's routing reads in a route, which the generator reads and builds routes with.
/// </summary>
internal static class RouteTokens
{
    /// <summary>
    /// Separates the segments of a route, and starts one served at the app's root.
    /// </summary>
    public const char PathSeparator = '/';

    /// <summary>
    /// <see cref="PathSeparator"/> as a string, for the string methods netstandard2.0 has no <c>char</c> overload of.
    /// </summary>
    public const string PathSeparatorText = "/";

    /// <summary>
    /// The <c>~</c> of <see cref="AppRelativePrefix"/>.
    /// </summary>
    public const char AppRelativeMarker = '~';

    /// <summary>
    /// Starts an action's template that MVC serves without the controller's route prefix, as it does one starting with
    /// <see cref="PathSeparator"/>.
    /// </summary>
    public const string AppRelativePrefix = "~/";
}
