using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// An action MVC serves at several routes, of which its tool calls one, as 2.4.0's did. MCP014 reports another route at
/// which MVC binds a value of the action from the route alone that the route the tool calls cannot carry. See
/// https://github.com/Kebechet/Api.ToMcp/issues/61.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string UncalledRouteId = "MCP014";

    /// <summary>
    /// Types whose properties MVC binds from a route value, beside others: the MVC form of <c>[AsParameters]</c>.
    /// </summary>
    private const string RouteValueTypes = @"
    public sealed class DocKey
    {
        public int Id { get; set; }

        public int Version { get; set; }
    }

    public sealed class RouteSlugFilter
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    public sealed class RouteSlugNamed
    {
        [FromRoute(Name = ""slug"")]
        public string? S { get; set; }
    }

    public sealed class RouteSlugAndQuery
    {
        [FromRoute]
        public int Id { get; set; }

        [FromRoute]
        public string? Slug { get; set; }

        [FromQuery]
        public int Page { get; set; }
    }

    public sealed class VersionedKey
    {
        public int Id { get; set; }

        [FromQuery]
        public int Version { get; set; }
    }
";

    [Theory]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, string? slug", "GET /api/orders/{id}", "GET /api/orders/{id}/{slug}", "'slug'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-slug/{slug}\")]", "int? id, string? slug", "GET /api/orders/by-id/{id}", "GET /api/orders/by-slug/{slug}", "'slug'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"all\")] [HttpGet(\"one/{id?}\")]", "int? id", "GET /api/orders/all", "GET /api/orders/one/{id?}", "'id'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-code/{productCode}\")]", "int? id, [FromRoute(Name = \"productCode\")] string? code", "GET /api/orders/by-id/{id}", "GET /api/orders/by-code/{productCode}", "'code'")]
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromRoute] string? slug", "GET /api/orders/{id}", "GET /api/orders/{id}/{slug}", "'slug'")]
    // MVC binds a [FromRoute] array from the route value, which the tool does not send.
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{tags}\")]", "int id, [FromRoute] string[]? tags", "GET /api/orders/{id}", "GET /api/orders/{id}/{tags}", "'tags'")]
    // [FromQuery] slug arrives; the [FromRoute] value of the same name does not.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromQuery] string? slug, [FromRoute(Name = \"slug\")] string? routeSlug", "GET /api/orders/{id}", "GET /api/orders/{id}/{slug}", "'routeSlug'")]
    // A [Route] without an HTTP method serves the method the tool sends too.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [Route(\"r/{slug}\")]", "int id, string? slug", "GET /api/orders/{id}", "GET /api/orders/r/{slug}", "'slug'")]
    public void Generator_ReportsARouteTheToolDoesNotCall_WhereMvcBindsAValueFromTheRouteAlone(
        string controllerDeclaration, string httpAttributes, string parameters, string calledRoute, string uncalledRoute, string lostValues)
    {
        // [ApiController] binds a simple parameter whose own name any route of the action names from the route alone, as
        // [FromRoute] does in any controller, and the route the tool calls has no such value.
        var source = UncalledRoutesSource(controllerDeclaration, $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);

        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Equal(
            $"The MCP tool of action 'OrdersController.Find' calls it at {calledRoute}, not at {uncalledRoute}. MVC binds {lostValues} from the route alone, " +
            "and the route the tool calls has no such route value. Give that route an action of its own for a tool to call it.",
            info.GetMessage());
        Assert.Equal("Find", info.Location.SourceTree!.GetText().ToString(info.Location.SourceSpan));
    }

    [Theory]
    [InlineData("[HttpGet(\"{a}\")] [HttpGet(\"x/{b}/{c}\")]", "string a, string? b, string? c", "GET /api/orders/x/{b}/{c}", "'b' and 'c'")]
    [InlineData("[HttpGet(\"{a}\")] [HttpGet(\"x/{b}/{c}/{d}\")]", "string a, string? b, string? c, string? d", "GET /api/orders/x/{b}/{c}/{d}", "'b', 'c' and 'd'")]
    public void Generator_ReportsEveryValueARouteTheToolDoesNotCallCarries_InOneInfo(string httpAttributes, string parameters, string uncalledRoute, string lostValues)
    {
        var source = UncalledRoutesSource(ApiControllerDeclaration, $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);

        Assert.Equal(
            $"The MCP tool of action 'OrdersController.Find' calls it at GET /api/orders/{{a}}, not at {uncalledRoute}. MVC binds {lostValues} from the route alone, " +
            "and the route the tool calls has no such route values. Give that route an action of its own for a tool to call it.",
            info.GetMessage());
    }

    [Fact]
    public void Generator_ReportsTheRouteOfAnotherControllerRoute_WhereMvcBindsAValueFromTheRouteAlone()
    {
        // MVC serves the action at each [Route] of its controller; the tool calls it at the first.
        var source = UncalledRoutesSource(
            "[ApiController] [Route(\"api/orders\")] [Route(\"api/tenants/{tenantId}/orders\")] public class OrdersController : ControllerBase",
            "[HttpGet(\"{id}\")] public Task<string> Find(string? tenantId, int id) => Task.FromResult(string.Empty);");

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);

        Assert.Equal(
            "The MCP tool of action 'OrdersController.Find' calls it at GET /api/orders/{id}, not at GET /api/tenants/{tenantId}/orders/{id}. MVC binds 'tenantId' from the route alone, " +
            "and the route the tool calls has no such route value. Give that route an action of its own for a tool to call it.",
            info.GetMessage());
    }

    [Fact]
    public void Generator_ReportsEachRouteTheToolDoesNotCall_ThatCarriesTheValue()
    {
        // Under each [Route] of the controller, {id}/{slug} carries slug; {id} under v1/orders does not, nor does the
        // route the tool calls.
        var source = UncalledRoutesSource(
            "[ApiController] [Route(\"api/orders\")] [Route(\"v1/orders\")] public class OrdersController : ControllerBase",
            "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")] public Task<string> Find(int id, string? slug) => Task.FromResult(string.Empty);");

        var routes = GetGeneratorDiagnostics(source, AllExceptExcludedConfig)
            .Where(x => x.Id == UncalledRouteId)
            .Select(x => x.GetMessage().Split(", not at ")[1].Split('.')[0])
            .ToList();

        Assert.Equal(["GET /api/orders/{id}/{slug}", "GET /v1/orders/{id}/{slug}"], routes);
    }

    [Fact]
    public void Generator_ReportsARouteOnce_WhenSeveralAttributesGiveTheActionIt()
    {
        var source = UncalledRoutesSource(
            ApiControllerDeclaration,
            "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")] [Route(\"{id}/{slug}\")] public Task<string> Find(int id, string? slug) => Task.FromResult(string.Empty);");

        Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Theory]
    // A property of a [FromRoute] parameter, without a binding attribute or with [FromRoute], under its own name.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/v/{version:min(1)}\")]", "[FromRoute] DocKey key", "GET /api/orders/{id}/v/{version:min(1)}", "the property Version of 'key'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromRoute] RouteSlugNamed f", "GET /api/orders/{id}/{slug}", "the property S of 'f'")]
    // A [FromRoute] property of a parameter of any other source MVC reads properties of.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, RouteSlugFilter f", "GET /api/orders/{id}/{slug}", "the property Slug of 'f'")]
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, RouteSlugFilter f", "GET /api/orders/{id}/{slug}", "the property Slug of 'f'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromQuery] RouteSlugFilter f", "GET /api/orders/{id}/{slug}", "the property Slug of 'f'")]
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{slug}\")]", "int id, [FromForm] RouteSlugFilter f", "POST /api/orders/{id}/{slug}", "the property Slug of 'f'")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "RouteSlugAndQuery p", "GET /api/orders/{id}/{slug}", "the property Slug of 'p'")]
    // [FromQuery] id arrives; the property Id of key does not.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"all\")] [HttpGet(\"{id}\")]", "[FromQuery] int? id, [FromRoute] DocKey key", "GET /api/orders/{id}", "the property Id of 'key'")]
    // Two values bind the route value, each from the route alone.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromRoute] string? slug, RouteSlugFilter f", "GET /api/orders/{id}/{slug}", "'slug' and the property Slug of 'f'")]
    public void Generator_ReportsAPropertyThatMvcBindsFromARouteTheToolDoesNotCall(string controllerDeclaration, string httpAttributes, string parameters, string uncalledRoute, string lostValue)
    {
        var source = UncalledRoutesSource(controllerDeclaration, $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);", RouteValueTypes);

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.Contains($", not at {uncalledRoute}. MVC binds {lostValue} from the route alone, and ", info.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    // In a controller without [ApiController], MVC binds slug from the query string, where the tool sends it.
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, string? slug")]
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-slug/{slug}\")]", "int id = 5, string slug = \"x\"")]
    // A binding attribute of another source makes MVC bind the value from there.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromQuery] string? slug")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromQuery(Name = \"slug\")] string? s")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromHeader(Name = \"slug\")] string? slug")]
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{note}\")]", "int id, [FromBody] Shipment note")]
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{note}\")]", "int id, [FromForm] string? note")]
    // [ApiController] infers [FromRoute] by a parameter's own name, which no route names: MVC binds s from the query
    // string under slug, where the tool sends it.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [ModelBinder(Name = \"slug\")] string? s")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [Bind(Prefix = \"slug\")] string? s")]
    // [ApiController] reads a complex parameter without a binding attribute from the body, whatever the routes name.
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{shipment}\")]", "int id, Shipment shipment")]
    // The route the tool calls carries the value as well.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}/{slug}\")] [HttpGet(\"{id}\")]", "int id, string? slug")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"x/{ID}\")]", "int id")]
    // No parameter takes {culture}, which middleware such as a culture provider reads.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{culture}/{id}\")]", "int id")]
    // The other route takes no value; MVC serves the other one for POST, which the tool, sending GET, never calls.
    [InlineData(ApiControllerDeclaration, "[HttpGet] [HttpGet(\"{id?}\")]", "int? id")]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"a/{x}\")] [HttpPost(\"b/{y}\")]", "string? x, string? y")]
    // No tool calls a URL at /mcp or under it, so an action of its own would get no tool there either.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id:int}\")] [HttpGet(\"/mcp/by-name/{name}\")]", "int? id, string? name")]
    public void Generator_ReportsNothing_WhereNoValueOfTheActionIsLost(string controllerDeclaration, string httpAttributes, string parameters)
    {
        var source = UncalledRoutesSource(controllerDeclaration, $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    /// <summary>
    /// A binder of the project's, and a binding behavior attribute of its own for parameters too, that passes Never.
    /// </summary>
    private const string ParameterBindingTypes = @"
    public sealed class FilterBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext) => Task.CompletedTask;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Parameter)]
    public sealed class NeverBoundAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public NeverBoundAttribute() : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Never)
        {
        }
    }
";

    [Theory]
    // MCP014 is not sure MVC binds a parameter whose own binding behavior is Never, from the route or at all.
    [InlineData("int id, [FromRoute] [NeverBound] string? slug")]
    [InlineData("int id, [NeverBound] RouteSlugFilter f")]
    // A binder of the project's binds the whole parameter, so MVC binds no property of it one by one.
    [InlineData("int id, [FromQuery] [ModelBinder(typeof(FilterBinder))] RouteSlugFilter f")]
    [InlineData("int id, [FromRoute] [ModelBinder(typeof(FilterBinder))] RouteSlugFilter f")]
    public void Generator_ReportsNothing_ForAParameterMvcNeverBindsOrBindsThroughABinderOfTheProjects(string parameters)
    {
        var source = UncalledRoutesSource(
            PlainControllerDeclaration,
            $"[HttpGet(\"{{id}}\")] [HttpGet(\"{{id}}/{{slug}}\")] public Task<string> Find({parameters}) => Task.FromResult(string.Empty);",
            RouteValueTypes + ParameterBindingTypes);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    /// <summary>
    /// Types with a [FromRoute] property Slug or Tags that MVC's complex binder binds or never binds, or that MCP014
    /// cannot be sure it binds from one route value.
    /// </summary>
    private const string BindingEligibilityTypes = @"
    public sealed class NoBindAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public NoBindAttribute() : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Never)
        {
        }
    }

    public sealed class NeverSlug
    {
        [FromRoute]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    public sealed class CustomNeverSlug
    {
        [FromRoute]
        [NoBind]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public sealed class NeverTypeSlug
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public sealed class OptionalSlugOfNeverType
    {
        [FromRoute]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Optional)]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    public sealed class ReadOnlySlug
    {
        [FromRoute]
        public string? Slug { get; }

        public int Page { get; set; }
    }

    public sealed class PrivateSetSlug
    {
        [FromRoute]
        public string? Slug { get; private set; }

        public int Page { get; set; }
    }

    public sealed class InitSlug
    {
        [FromRoute]
        public string? Slug { get; init; }

        public int Page { get; set; }
    }

    public sealed class ReadOnlyTags
    {
        [FromRoute]
        public List<string> Tags { get; } = new List<string>();

        public int Page { get; set; }
    }

    public sealed record SlugRecord([FromRoute] string? Slug, int Page);

    public sealed class ReadOnlyUri
    {
        [FromRoute]
        public Uri? Slug { get; }

        public int Page { get; set; }
    }

    public sealed class SetTags
    {
        [FromRoute]
        public List<string>? Tags { get; set; }
    }

    public sealed class ArrayTags
    {
        [FromRoute]
        public string[]? Tags { get; set; }
    }

    public sealed class ReadOnlyListTags
    {
        [FromRoute]
        public IReadOnlyList<int>? Tags { get; set; }
    }

    public sealed class HashSetTags
    {
        [FromRoute]
        public HashSet<string>? Tags { get; set; }
    }

    public sealed class Address
    {
        public string? City { get; set; }
    }

    public sealed class AddressSlug
    {
        [FromRoute]
        public Address? Slug { get; set; }

        public int Page { get; set; }
    }

    public sealed class AddressesSlug
    {
        [FromRoute]
        public List<Address>? Slug { get; set; }
    }

    public sealed class CtorSlug
    {
        public CtorSlug(int page)
        {
            Page = page;
        }

        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    public sealed class TwoCtorsSlug
    {
        public TwoCtorsSlug()
        {
        }

        public TwoCtorsSlug(int page)
        {
            Page = page;
        }

        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    public abstract class AbstractSlug
    {
        public AbstractSlug()
        {
        }

        [FromRoute]
        public string? Slug { get; set; }
    }

    public struct SlugStruct
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    [Bind(""Page"")]
    public sealed class PageOnlyFilter
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    [Bind(""Slug"")]
    public sealed class SlugOnlyFilter
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    [Bind(""Page"")]
    public class PageOnlyBase
    {
    }

    public sealed class SlugOfPageOnlyBase : PageOnlyBase
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    public sealed class AllPropertiesAttribute : Attribute, Microsoft.AspNetCore.Mvc.ModelBinding.IPropertyFilterProvider
    {
        public Func<Microsoft.AspNetCore.Mvc.ModelBinding.ModelMetadata, bool> PropertyFilter => _ => true;
    }

    public sealed class PageAndSlugBindAttribute : BindAttribute
    {
        public PageAndSlugBindAttribute() : base(""Page"", ""Slug"")
        {
        }
    }

    public sealed class PageBindAttribute : BindAttribute
    {
        public PageBindAttribute(params string[] include) : base(""Page"")
        {
        }
    }

    public sealed class BinderIdSlug
    {
        [FromRoute]
        public BinderId? Slug { get; set; }

        public int Page { get; set; }
    }
";

    [Theory]
    // MVC never binds a property with [BindNever], or a binding behavior attribute of the project's that passes Never,
    // nor one whose type has [BindNever] and the property no binding behavior of its own.
    [InlineData("{id}/{slug}", "NeverSlug", false)]
    [InlineData("{id}/{slug}", "CustomNeverSlug", false)]
    [InlineData("{id}/{slug}", "NeverTypeSlug", false)]
    [InlineData("{id}/{slug}", "OptionalSlugOfNeverType", true)]
    // Nor one without a public setter, unless a record's constructor sets it: MVC sets no new value into it, and fills
    // in place only a collection the instance already holds, which the generator cannot see.
    [InlineData("{id}/{slug}", "ReadOnlySlug", false)]
    [InlineData("{id}/{slug}", "PrivateSetSlug", false)]
    [InlineData("{id}/{slug}", "InitSlug", true)]
    [InlineData("{id}/{slug}", "SlugRecord", true)]
    [InlineData("{id}/{tags}", "ReadOnlyTags", false)]
    [InlineData("{id}/{slug}", "ReadOnlyUri", false)]
    // One route value fills a simple value, an array of such, or a collection MVC creates as a List<T> of such; a
    // complex value MVC binds from keys such as Slug.City.
    [InlineData("{id}/{tags}", "SetTags", true)]
    [InlineData("{id}/{tags}", "ArrayTags", true)]
    [InlineData("{id}/{tags}", "ReadOnlyListTags", true)]
    [InlineData("{id}/{tags}", "HashSetTags", false)]
    [InlineData("{id}/{slug}", "AddressSlug", false)]
    [InlineData("{id}/{slug}", "AddressesSlug", false)]
    // MVC creates a complex value through a public constructor without parameters, or a record's primary one; it
    // creates no abstract class, nor a struct without such a constructor declared on it.
    [InlineData("{id}/{slug}", "CtorSlug", false)]
    [InlineData("{id}/{slug}", "TwoCtorsSlug", true)]
    [InlineData("{id}/{slug}", "AbstractSlug", false)]
    [InlineData("{id}/{slug}", "SlugStruct", false)]
    // The [Bind] include list of the type, or of a type it derives from, names the properties MVC binds.
    [InlineData("{id}/{slug}", "PageOnlyFilter", false)]
    [InlineData("{id}/{slug}", "SlugOnlyFilter", true)]
    [InlineData("{id}/{slug}", "SlugOfPageOnlyBase", false)]
    public void Generator_ReportsAPropertyOfARouteValue_OnlyWhereItIsSureMvcBindsIt(string uncalledRoute, string type, bool isReported)
    {
        var source = UncalledRoutesSource(
            PlainControllerDeclaration,
            $"[HttpGet(\"{{id}}\")] [HttpGet(\"{uncalledRoute}\")] public Task<string> Find(int id, {type} f) => Task.FromResult(string.Empty);",
            BindingEligibilityTypes);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.Equal(isReported, GetGeneratorDiagnostics(source, AllExceptExcludedConfig).Any(x => x.Id == UncalledRouteId));
    }

    [Theory]
    [InlineData("[Bind(\"Page\")] RouteSlugFilter f", false)]
    [InlineData("[Bind(\"slug\")] RouteSlugFilter f", false)]
    [InlineData("[Bind(\"Page\")] SlugRecord f", false)]
    [InlineData("[Bind(\"Page, Slug\")] RouteSlugFilter f", true)]
    [InlineData("[Bind(\"Page\", \"Slug\")] RouteSlugFilter f", true)]
    [InlineData("[Bind(\"Slug\")] SlugRecord f", true)]
    [InlineData("[Bind] RouteSlugFilter f", true)]
    // The list of the parameter and the list of its type each have to name it.
    [InlineData("[Bind(\"Page, Slug\")] PageOnlyFilter f", false)]
    [InlineData("[Bind(\"Page\")] SlugOnlyFilter f", false)]
    [InlineData("[Bind(\"Slug\")] SlugOnlyFilter f", true)]
    // The generator cannot read what another property filter, or a [Bind] of the project's, lets MVC bind.
    [InlineData("[AllProperties] RouteSlugFilter f", false)]
    [InlineData("[PageAndSlugBind] RouteSlugFilter f", false)]
    [InlineData("[PageBind(\"Slug\")] RouteSlugFilter f", false)]
    public void Generator_ReportsAPropertyOfARouteValue_OnlyWhereEveryBindIncludeListNamesIt(string parameter, bool isReported)
    {
        // MVC's complex binder binds only the properties every [Bind] include list of the parameter and its type names,
        // each item split at commas, by the property's name, case and all.
        var source = UncalledRoutesSource(
            PlainControllerDeclaration,
            $"[HttpGet(\"{{id}}\")] [HttpGet(\"{{id}}/{{slug}}\")] public Task<string> Find(int id, {parameter}) => Task.FromResult(string.Empty);",
            RouteValueTypes + BindingEligibilityTypes);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.Equal(isReported, GetGeneratorDiagnostics(source, AllExceptExcludedConfig).Any(x => x.Id == UncalledRouteId));
    }

    [Theory]
    // [ApiController] reads a type from the body when no property of it names a binding source, whatever the HTTP
    // method, and a property the record's constructor sets counts for none (ModelMetadata.BoundProperties).
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, SlugRecord f", false)]
    [InlineData(ApiControllerDeclaration, "[HttpPost(\"{id}\")] [HttpPost(\"{id}/{slug}\")]", "int id, SlugRecord f", false)]
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, RouteSlugFilter f", true)]
    // A binding attribute of the parameter, or a controller without [ApiController], makes MVC bind it property by
    // property.
    [InlineData(ApiControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, [FromQuery] SlugRecord f", true)]
    [InlineData(PlainControllerDeclaration, "[HttpGet(\"{id}\")] [HttpGet(\"{id}/{slug}\")]", "int id, SlugRecord f", true)]
    public void Generator_ReportsARouteValueOfARecordsConstructor_OnlyWhereMvcBindsTheRecordPropertyByProperty(
        string controllerDeclaration, string httpAttributes, string parameters, bool isReported)
    {
        var source = UncalledRoutesSource(
            controllerDeclaration,
            $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);",
            RouteValueTypes + BindingEligibilityTypes);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.Equal(isReported, GetGeneratorDiagnostics(source, AllExceptExcludedConfig).Any(x => x.Id == UncalledRouteId));
    }

    [Theory]
    [InlineData("[FromRoute] int[]? tags", true)]
    [InlineData("[FromRoute] List<string>? tags", true)]
    [InlineData("[FromRoute] IEnumerable<int>? tags", true)]
    [InlineData("[FromRoute] IReadOnlyList<string>? tags", true)]
    // MVC binds the items of a complex type, and a dictionary, from keys such as tags[0].City; MCP014 is not sure what
    // a collection MVC does not create as a List<T> holds.
    [InlineData("[FromRoute] Address[]? tags", false)]
    [InlineData("[FromRoute] List<Address>? tags", false)]
    [InlineData("[FromRoute] Dictionary<string, string>? tags", false)]
    [InlineData("[FromRoute] HashSet<string>? tags", false)]
    public void Generator_ReportsACollection_OnlyWhereMvcFillsItFromOneRouteValue(string parameter, bool isReported)
    {
        var source = UncalledRoutesSource(
            PlainControllerDeclaration,
            $"[HttpGet(\"{{id}}\")] [HttpGet(\"{{id}}/{{tags}}\")] public Task<string> Find(int id, {parameter}) => Task.FromResult(string.Empty);",
            BindingEligibilityTypes);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.Equal(isReported, GetGeneratorDiagnostics(source, AllExceptExcludedConfig).Any(x => x.Id == UncalledRouteId));
    }

    [Theory]
    [InlineData("{id}/{other}", "[FromRoute] BinderId? other")]
    [InlineData("{id}/{slug}", "[FromRoute, ModelBinder(typeof(PlainCodeBinder))] string? slug")]
    [InlineData("{id}/{slug}", "BinderIdSlug f")]
    public void Generator_ReportsNothing_ForAValueThatABinderOfTheProjectsBinds(string uncalledRoute, string parameter)
    {
        // A binder type of the value or of its type binds it, with the route values of [FromRoute] to read, and the
        // generator cannot see what it reads.
        var source = UncalledRoutesSource(
            PlainControllerDeclaration,
            $"[HttpGet(\"{{id}}\")] [HttpGet(\"{uncalledRoute}\")] public Task<string> Find(int id, {parameter}) => Task.FromResult(string.Empty);",
            BindingEligibilityTypes);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Theory]
    // MVC reads the [FromQuery] Version from the query string, where the tool sends it.
    [InlineData("[HttpGet(\"{id}\")] [HttpGet(\"{id}/v/{version}\")]", "[FromRoute] VersionedKey key")]
    // [ApiController] infers no source for a type with a property that names one, so MVC reads the parameter from the
    // query string as well.
    [InlineData("[HttpGet(\"{id}\")] [HttpGet(\"{id}/{key}\")]", "int id, VersionedKey key")]
    // MVC binds the properties of a parameter with a name of its own under that name, key.Version, which no route value is.
    [InlineData("[HttpGet(\"all\")] [HttpGet(\"{version}\")]", "[FromRoute(Name = \"key\")] DocKey key")]
    public void Generator_ReportsNothing_ForAComplexValue_ThatNoRouteValueCarriesAlone(string httpAttributes, string parameters)
    {
        var source = UncalledRoutesSource(
            ApiControllerDeclaration,
            $"{httpAttributes} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);",
            RouteValueTypes);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Fact]
    public void Generator_ReportsNothing_ForARouteOfAnotherHttpMethodUnderAnotherControllerRoute()
    {
        // MVC serves b/{y} for POST under each [Route] of the controller, and the tool sends GET.
        var source = UncalledRoutesSource(
            "[ApiController] [Route(\"api/orders\")] [Route(\"v1/orders\")] public class OrdersController : ControllerBase",
            "[HttpGet(\"a/{x}\")] [HttpPost(\"b/{y}\")] public Task<string> Find(string? x, string? y) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Fact]
    public void Generator_ReportsNothing_ForAControllerRouteWhoseValueTheActionReadsFromTheQueryString()
    {
        // The culture provider reads ?culture= too, and so does the action.
        var source = UncalledRoutesSource(
            "[ApiController] [Route(\"api/orders\")] [Route(\"{culture}/api/orders\")] public class OrdersController : ControllerBase",
            "[HttpGet(\"{id}\")] public Task<string> Find(int id, [FromQuery] string? culture) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UncalledRouteId);
    }

    [Fact]
    public void Generator_EmitsTheToolOfAnActionWithSeveralRoutes_AsMainDoes()
    {
        // The tool calls the first route, as 2.4.0's did: the expected text is what main 0d09708 generates.
        var source = UncalledRoutesSource(
            ApiControllerDeclaration,
            "[HttpGet(\"by-id/{id}\")] [HttpGet(\"by-slug/{slug}\")] public Task<string> Find(int? id, string? slug, [FromQuery] int page = 1) => Task.FromResult(string.Empty); " +
            "[HttpPost(\"{id}/reviews\")] [HttpPost(\"by-slug/{slug}/reviews\")] public Task<string> Review(int id, string? slug, [FromForm] Shipment review, [FromHeader(Name = \"Idempotency-Key\")] string? key) => Task.FromResult(string.Empty);");

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Empty(CompileWithGeneratedTools(source, AllExceptExcludedConfig));
        AssertMatchesSnapshot("ToolOfAnActionWithSeveralRoutes.txt", GetToolClass(generated, SimpleTypeToolClassName));
        AssertMatchesSnapshot("FormToolOfAnActionWithSeveralRoutes.txt", GetToolClass(generated, "OrdersController_ReviewTool"));
    }

    /// <param name="types">Types declared beside the controller.</param>
    private static string UncalledRoutesSource(string controllerDeclaration, string actions, string types = "")
    {
        return SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
{types}
    {controllerDeclaration}
    {{
        {actions}
    }}
}}
";
    }
}
