namespace Api.ToMcp.Generator.Models;

/// <summary>
/// The content type a generated tool sends a form as, and the media ranges of a <c>[Consumes]</c> that take it. Mirrors
/// <c>MediaTypeNames</c>, which a netstandard2.0 source generator does not have.
/// </summary>
internal static class FormContentTypes
{
    public const string UrlEncoded = "application/x-www-form-urlencoded";
    public const string AnyApplication = "application/*";
    public const string Any = "*/*";

    /// <summary>
    /// Ends a media type where its parameters, such as <c>charset</c>, start.
    /// </summary>
    public const char ParameterSeparator = ';';
}
