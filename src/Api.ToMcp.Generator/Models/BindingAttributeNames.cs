namespace Api.ToMcp.Generator.Models;

/// <summary>
/// MVC's binding attributes and the interfaces they implement, as the generator matches them: the attributes by their
/// simple name, which the stubs some tests compile against share, and the interfaces by Roslyn's
/// <c>ToDisplayString()</c>. A netstandard2.0 source generator cannot reference ASP.NET Core's types.
/// </summary>
internal static class BindingAttributeNames
{
    public const string FromRoute = "FromRouteAttribute";
    public const string FromQuery = "FromQueryAttribute";
    public const string FromBody = "FromBodyAttribute";
    public const string FromHeader = "FromHeaderAttribute";
    public const string FromServices = "FromServicesAttribute";

    /// <summary>
    /// Implemented by every attribute that names a binding source, <c>[FromForm]</c> and <c>[ModelBinder]</c> included.
    /// </summary>
    public const string BindingSourceMetadata = "Microsoft.AspNetCore.Mvc.ModelBinding.IBindingSourceMetadata";

    /// <summary>
    /// Implemented by every attribute whose <c>Name</c> renames the key a value is bound from.
    /// </summary>
    public const string ModelNameProvider = "Microsoft.AspNetCore.Mvc.ModelBinding.IModelNameProvider";

    public const string ModelNamePropertyName = "Name";
}
