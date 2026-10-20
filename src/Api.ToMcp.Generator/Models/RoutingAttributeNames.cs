namespace Api.ToMcp.Generator.Models;

/// <summary>
/// MVC's routing attributes, the interfaces through which MVC reads them and the members it reads, as Roslyn's
/// <c>ToDisplayString()</c> spells them. A netstandard2.0 source generator cannot reference ASP.NET Core's types.
/// </summary>
internal static class RoutingAttributeNames
{
    /// <summary>
    /// Implemented by every attribute MVC reads a route template from: <c>[Route]</c>, the <c>[Http*]</c> attributes,
    /// <c>[AcceptVerbs]</c> and any of an app's own.
    /// </summary>
    public const string RouteTemplateProvider = "Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider";

    /// <summary>
    /// Implemented by every attribute MVC reads the HTTP methods an action serves from.
    /// </summary>
    public const string ActionHttpMethodProvider = "Microsoft.AspNetCore.Mvc.Routing.IActionHttpMethodProvider";

    public const string Route = "Microsoft.AspNetCore.Mvc.RouteAttribute";

    /// <summary>
    /// The base of the <c>[Http*]</c> attributes, which takes the HTTP methods and the template from its constructor.
    /// </summary>
    public const string HttpMethod = "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute";

    public const string AcceptVerbs = "Microsoft.AspNetCore.Mvc.AcceptVerbsAttribute";
    public const string HttpGet = "Microsoft.AspNetCore.Mvc.HttpGetAttribute";
    public const string HttpPost = "Microsoft.AspNetCore.Mvc.HttpPostAttribute";
    public const string HttpPut = "Microsoft.AspNetCore.Mvc.HttpPutAttribute";
    public const string HttpPatch = "Microsoft.AspNetCore.Mvc.HttpPatchAttribute";
    public const string HttpDelete = "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute";
    public const string HttpHead = "Microsoft.AspNetCore.Mvc.HttpHeadAttribute";
    public const string HttpOptions = "Microsoft.AspNetCore.Mvc.HttpOptionsAttribute";

    public const string TemplatePropertyName = "Template";
    public const string NamePropertyName = "Name";
    public const string OrderPropertyName = "Order";
    public const string HttpMethodsPropertyName = "HttpMethods";

    /// <summary>
    /// The property that holds <c>[AcceptVerbs]</c>'s template.
    /// </summary>
    public const string AcceptVerbsRoutePropertyName = "Route";
}
