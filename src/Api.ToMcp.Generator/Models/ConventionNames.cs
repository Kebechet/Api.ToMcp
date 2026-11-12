namespace Api.ToMcp.Generator.Models;

/// <summary>
/// Names the generator recognizes by convention rather than by symbol: the config file it reads from the additional
/// files, and the suffix MVC drops from a controller's class name in routes and the generator drops in tool names.
/// </summary>
internal static class ConventionNames
{
    public const string ConfigFileName = "generator.json";
    public const string ControllerSuffix = "Controller";

    /// <summary>
    /// Separates the controller from the action in an include or exclude entry such as <c>Products.GetById</c>.
    /// </summary>
    public const char EntryActionSeparator = '.';
}
