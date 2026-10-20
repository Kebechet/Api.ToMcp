namespace Api.ToMcp.Generator.Models;

/// <summary>
/// MVC's binding attributes and the interfaces they implement, as the generator matches them: the binding-source
/// attributes of parameters and properties by their simple name, the others and the interfaces by Roslyn's
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
    /// Names a binding source only together with a binder type, as does <see cref="BindProperty"/>.
    /// </summary>
    public const string ModelBinder = "Microsoft.AspNetCore.Mvc.ModelBinderAttribute";

    public const string BindProperty = "Microsoft.AspNetCore.Mvc.BindPropertyAttribute";

    public const string BinderTypePropertyName = "BinderType";

    /// <summary>
    /// Implemented by every attribute that can name a binding source, <c>[FromForm]</c> and <c>[ModelBinder]</c> included.
    /// </summary>
    public const string BindingSourceMetadata = "Microsoft.AspNetCore.Mvc.ModelBinding.IBindingSourceMetadata";

    /// <summary>
    /// Implemented by every attribute whose <c>Name</c> renames the key a value is bound from.
    /// </summary>
    public const string ModelNameProvider = "Microsoft.AspNetCore.Mvc.ModelBinding.IModelNameProvider";

    public const string ModelNamePropertyName = "Name";

    /// <summary>
    /// Implemented by <c>[ApiController]</c> and by any attribute that makes MVC apply its conventions.
    /// </summary>
    public const string ApiBehaviorMetadata = "Microsoft.AspNetCore.Mvc.Infrastructure.IApiBehaviorMetadata";
}
