namespace Api.ToMcp.Generator.Models;

/// <summary>
/// The HTTP status codes the generator reads from attributes and helper calls. Mirrors ASP.NET Core's
/// <c>StatusCodes</c>, which a netstandard2.0 source generator cannot reference.
/// </summary>
internal static class HttpStatusCodes
{
    public const int Status200OK = 200;
    public const int Status201Created = 201;

    private const int LastSuccess = 299;

    public static bool IsSuccess(int statusCode)
    {
        return statusCode >= Status200OK && statusCode <= LastSuccess;
    }
}
