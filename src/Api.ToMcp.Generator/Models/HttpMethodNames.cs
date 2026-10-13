namespace Api.ToMcp.Generator.Models;

/// <summary>
/// The HTTP methods as the generator spells them: the upper-case verb of an action's <c>[Http*]</c> attribute. Mirrors
/// ASP.NET Core's <c>HttpMethods</c>, which a netstandard2.0 source generator cannot reference and whose fields are not
/// constants, so they could not serve as <c>switch</c> cases or patterns.
/// </summary>
internal static class HttpMethodNames
{
    public const string Get = "GET";
    public const string Head = "HEAD";
    public const string Options = "OPTIONS";
    public const string Post = "POST";
    public const string Put = "PUT";
    public const string Patch = "PATCH";
    public const string Delete = "DELETE";
}
