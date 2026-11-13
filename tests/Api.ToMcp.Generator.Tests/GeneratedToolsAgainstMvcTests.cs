using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Runs generated tools against real MVC. The test controllers and the tools the generator emits for them are compiled
/// into one assembly, MVC serves the controllers on a loopback port, and each tool sends its request there; every action
/// echoes what MVC bound. A binding rule the generator gets wrong fails here even when the emitted code looks right.
/// </summary>
public sealed class GeneratedToolsAgainstMvcTests : IClassFixture<GeneratedToolsAgainstMvcTests.MvcHost>
{
    private const string Controllers = @"
namespace TestApi
{
    [ApiController]
    [Route(""api"")]
    public class EchoController : ControllerBase
    {
        [HttpGet(""attr/{id:int}"")]
        public object GetAttributed(AttributedQuery q) => q;

        [HttpPost(""attr/{id:int}"")]
        public object PostAttributed(AttributedQuery q) => q;

        [HttpGet(""query-only"")]
        public object GetQueryOnly(QueryOnlyFilter q) => q;

        [HttpGet(""aliased/{orderId}"")]
        public object GetAliased(AliasedRouteQuery q) => q;

        [HttpGet(""from-route/{orderId}"")]
        public object GetFromRoute([FromRoute] AliasedRouteQuery q) => q;

        [HttpPost(""record-property"")]
        public object PostRecordProperty(RecordPropertyFilter q) => q;

        [HttpPost(""record-parameter"")]
        public object PostRecordParameter(RecordParameterFilter q) => q;

        [HttpPost(""record-with-query"")]
        public object PostRecordWithQuery(RecordWithQueryProperty q) => q;

        [HttpPost(""named-binder"")]
        public object PostNamedBinder(NamedModelBinderFilter q) => q;

        [HttpPost(""named-bind-property"")]
        public object PostNamedBindProperty(NamedBindPropertyFilter q) => q;

        [HttpPost(""keyword/{namespace}/{id}"")]
        public object PostKeyword(string @namespace, KeywordQuery q, [FromBody] ProductFilter @object, [FromQuery] string? @class)
            => new { @namespace, q.Id, q.@event, @class, @object.City };

        [HttpGet(""private-getter/{id}"")]
        public object GetPrivateGetter(PrivateGetterQuery q) => new { q.Id, q.Echo };

        [HttpGet(""two/{id}"")]
        public object GetTwo(AttributedQuery a, NotedShipment b) => new { aId = a.Id, a.City, aNote = a.Note, bId = b.Id, bNote = b.Note };

        [HttpPost(""record-named"")]
        public object PostRecordNamed(RecordNamedCity q) => q;

        [HttpGet(""record-route/{orderId}"")]
        public object GetRecordRoute(RecordRouteOrder q) => q;

        [HttpGet(""query-id/{id}"")]
        public object GetQueryId([FromRoute] QueryIdFilter q) => q;

        [HttpGet(""direct/{id}"")]
        public object GetDirect(int id, AttributedQuery q) => new { id, qId = q.Id, q.City };

        [HttpGet(""order"")]
        public object GetOrder(OrderId orderId) => new { orderId = orderId.Value };

        [HttpPost(""body-id/{value}"")]
        public object PostBodyId([FromBody] OrderId id) => new { id = id.Value };

        [HttpPost(""upper/{Id}"")]
        public object PostUpper(int id) => new { id };

        [HttpGet(""ordered"")]
        public object GetOrdered(string name, int? count, OrderId orderId) => new { name, count, orderId = orderId.Value };

        [HttpPost(""clash/{route}"")]
        public object PostClash(string route, string invoker, string queryParts, [FromBody] ProductFilter bodyJson, string cancellationToken, string routeroute, string System, string JsonSerializer, string McpScope)
            => new { route, invoker, queryParts, bodyJson.City, cancellationToken, routeroute, System, JsonSerializer, McpScope };

        [HttpGet(""files/{**path}"")]
        public object GetFile(string path) => new { path };

        [HttpGet(""star/{*path}"")]
        public object GetStar(string path) => new { path };

        [HttpGet(""query-record/{orderId}"")]
        public object GetQueryRecord([FromQuery] RecordRouteOrder q) => q;

        [HttpGet(""query-aliased/{orderId}"")]
        public object GetQueryAliased([FromQuery] AliasedRouteQuery q) => q;

        [HttpGet(""query-named/{orderId}"")]
        public object GetQueryNamed([FromQuery(Name = ""f"")] RecordRouteOrder q) => q;

        [HttpPost(""body-route/{id}"")]
        public object PostBodyRoute([FromBody] Shipment body, AttributedQuery q) => new { bodyId = body.Id, qId = q.Id, q.City };

        [HttpGet(""text-order"")]
        public object GetTextOrder(TextOrderId id) => new { id = id.Value };

        [HttpPost(""text-order"")]
        public object PostTextOrder(TextOrderId id) => new { id = id.Value };

        [HttpPost(""plain-code"")]
        public object PostPlainCode([ModelBinder(typeof(PlainCodeBinder), Name = ""code"")] PlainCode value) => new { code = value.Value };

        [HttpPost(""typed-binder"")]
        public object PostTypedBinder(TypedBinderQuery q) => q;

        [HttpPost(""nullable-binder"")]
        public object PostNullableBinder(NullableBinderQuery q) => q;

        [HttpGet(""header"")]
        public object GetHeader([FromHeader(Name = ""X-Tenant"")] string? tenant, [FromHeader(Name = ""X-Ids"")] int[]? ids, [FromHeader(Name = ""X-Tags"")] string[]? tags, [FromHeader] OrderStatus? status)
            => new { tenant, ids, tags, status };

        [HttpGet(""header-default"")]
        public object GetHeaderDefault([FromHeader(Name = ""X-Page"")] int page = 2) => new { page, sent = Request.Headers.ContainsKey(""X-Page"") };

        [HttpPost(""form"")]
        public object PostForm([FromForm] string? name, [FromForm(Name = ""f"")] ProductFilter? filter, [FromForm] int[]? ids, [FromForm] OrderStatus status = OrderStatus.Closed)
            => new { name, city = filter?.City, ids, status, isForm = Request.HasFormContentType };

        [HttpPut(""form-route/{id:int}"")]
        public object PutFormRoute([FromForm] AttributedQuery q) => new { q.Id, q.Note };

        [HttpPost(""form-header/{id}"")]
        public object PostFormHeader(int id, [FromForm] Shipment shipment, [FromHeader(Name = ""Idempotency-Key"")] string key) => new { id, shipment.Note, key };

        [HttpPost(""bind-prefix"")]
        public object PostBindPrefix([FromForm, Bind(Prefix = ""r"")] ProductFilter review, [FromHeader, ModelBinder(Name = ""X-Tenant"")] string tenant) => new { review.City, tenant };

        // The AI never sets these headers, so the tool takes no argument for them and sends the request's own.
        [HttpGet(""own-headers"")]
        public object GetOwnHeaders([FromHeader(Name = ""Authorization"")] string? auth, [FromHeader(Name = ""X-MS-CLIENT-PRINCIPAL-ID"")] string? userId, [FromHeader(Name = ""X-Forwarded-For"")] string? clientIp)
            => new { auth, userId, clientIp };

        [HttpPost(""amounts"")]
        public object PostAmounts([FromForm] decimal amount, [FromForm] double ratio, [FromForm] DateOnly day, [FromForm] int delta, [FromForm] decimal[]? amounts, [FromHeader(Name = ""X-Amount"")] decimal? headerAmount, [FromQuery] decimal? queryAmount)
            => new { amount, ratio, day, delta, amounts, headerAmount, queryAmount };

        // UseRequestLocalization picks the request's culture from Accept-Language and from the culture query value, so
        // the AI would pick the culture MVC reads the form in.
        [HttpPost(""localized"")]
        public object PostLocalized([FromHeader(Name = ""Accept-Language"")] string? language, [FromForm] decimal amount)
            => new { language, amount, culture = System.Globalization.CultureInfo.CurrentCulture.Name };

        [HttpPost(""localized-query"")]
        public object PostLocalizedQuery([FromQuery] string? culture, [FromForm] decimal amount)
            => new { culture, amount, requestCulture = System.Globalization.CultureInfo.CurrentCulture.Name };

        [HttpPost(""dated"")]
        public object PostDated([FromForm] DatedAmount a) => a;

        // A form of text alone reads the same in every culture, so the language and the culture are the action's own data.
        [HttpPost(""translate"")]
        public object PostTranslate([FromHeader(Name = ""Accept-Language"")] string? language, [FromForm] string text)
            => new { language, text, culture = System.Globalization.CultureInfo.CurrentCulture.Name };

        [HttpPost(""names"")]
        public object PostNames([FromQuery] string? culture, [FromForm] string name)
            => new { culture, name, requestCulture = System.Globalization.CultureInfo.CurrentCulture.Name };

        [HttpGet(""nan"")]
        public object GetNan(double value = double.NaN, float inf = float.PositiveInfinity, double negInf = double.NegativeInfinity)
            => new { isNaN = double.IsNaN(value), inf = float.IsPositiveInfinity(inf), negInf = double.IsNegativeInfinity(negInf), query = Request.QueryString.Value };

        // MVC refuses 99, which no member of OrderStatus has, from text, but applies it as the default of a value not sent.
        [HttpGet(""defaults"")]
        public object GetDefaults(OrderStatus unknown = (OrderStatus)99, int page = 2, string text = ""x"")
            => new { unknown, page, text, query = Request.QueryString.Value };

        // MVC answers 400 when a binding-required or [Required] parameter gets no value, its default notwithstanding.
        [HttpGet(""bind-required"")]
        public object GetBindRequired([FromQuery, Microsoft.AspNetCore.Mvc.ModelBinding.BindRequired] int page = 1) => new { page, query = Request.QueryString.Value };

        [HttpGet(""required-default"")]
        public object GetRequiredDefault([FromQuery, System.ComponentModel.DataAnnotations.Required] int size = 1) => new { size, query = Request.QueryString.Value };

        [HttpGet(""must-bind"")]
        public object GetMustBind([FromQuery, MustBind] int page = 1) => new { page, query = Request.QueryString.Value };

        // MVC reads a combination of [Flags] members from the text C# writes of it, and refuses a value none makes.
        [HttpGet(""access"")]
        public object GetAccess([FromQuery] EchoAccess access = EchoAccess.Read) => new { access, query = Request.QueryString.Value };

        [HttpGet(""names/{name}.{ext?}"")]
        public object GetFileName(string name, string ext = ""txt"") => new { name, ext, path = Request.Path.Value };

        // MVC serves the action at report without the dot, and answers report. with 404.
        [HttpGet(""optional-names/{name}.{ext?}"")]
        public object GetOptionalFileName(string name, string? ext = null) => new { name, ext, path = Request.Path.Value };

        [HttpGet(""optional-names/{name}.{ext?}/size"")]
        public object GetOptionalFileSize(string name, string? ext = null) => new { name, ext, path = Request.Path.Value };

        [HttpGet(""default-route/{status?}"")]
        public object GetDefaultRoute(OrderStatus status = (OrderStatus)99) => new { status, path = Request.Path.Value };

        [HttpPost(""default-body"")]
        public object PostDefaultBody([FromBody] OrderStatus status = (OrderStatus)99) => new { status };

        [HttpGet(""optional/{id?}"")]
        public object GetOptional() => new { id = RouteData.Values[""id""], path = Request.Path.Value };

        // MVC binds a parameter under the name the first IModelNameProvider among its attributes, then its type's, gives.
        [HttpGet(""binder-named"")]
        public object GetBinderNamed([ModelBinder(Name = ""q"")] string? search) => new { search, query = Request.QueryString.Value };

        [HttpGet(""query-bind-prefix"")]
        public object GetQueryBindPrefix([FromQuery, Bind(Prefix = ""f"")] ProductFilter filter) => new { filter.City, query = Request.QueryString.Value };

        [HttpGet(""route-named/{productId}"")]
        public object GetRouteNamed([FromRoute(Name = ""productId"")] int id) => new { id, path = Request.Path.Value };

        [HttpDelete(""route-named/{productCode}"")]
        public object DeleteRouteNamed([FromRoute(Name = ""productCode"")] string code) => new { deleted = code };

        // [ApiController] infers [FromQuery] for value, which the route does not name, and MVC reads it from ?code=.
        [HttpGet(""binder-route/{code}"")]
        public object GetBinderRoute([ModelBinder(Name = ""code"")] string value) => new { value, path = Request.Path.Value, query = Request.QueryString.Value };

        // Under the prefix o, Order would read the route value o.orderId, which no route has, and City reads o.c.
        [HttpGet(""route-object/{orderId}"")]
        public object GetRouteObject([FromRoute(Name = ""o"")] AliasedRouteQuery q) => new { q.Order, q.City, query = Request.QueryString.Value };

        // MVC infers [FromRoute] for code and [FromQuery] for value, so code binds {code} and value ?code=.
        [HttpGet(""api-dup/{code}"")]
        public object GetApiDup([ModelBinder(Name = ""code"")] string? value, string? code) => new { value, code };

        // The prefix id names the object, not one value: MVC binds its Id from ?id.Id=, though the URL needs {id}.
        [HttpGet(""query-object/{id:int}"")]
        public object GetQueryObject([FromQuery(Name = ""id"")] Shipment shipment) => new { shipment.Id, shipment.Note, path = Request.Path.Value };

        // id fills {productId}, and only body.Id matches {id:guid}.
        [HttpPut(""two/{id:guid}/items/{productId:int}"")]
        public object PutTwo([FromRoute(Name = ""productId"")] int id, [FromBody] GuidDto body) => new { id, bodyId = body.Id, routeId = RouteData.Values[""id""] };
    }

    // Without its id, a call to Get or Delete requests the URL that List or DeleteAll is served at.
    [ApiController]
    [Route(""api/items"")]
    public class ItemsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        public object Get(Guid? id) => new { id };

        [HttpGet]
        public object List() => new { listed = ""all"" };

        [HttpDelete(""{id}"")]
        public object Delete(Guid? id) => new { deleted = id };

        [HttpDelete]
        [Api.ToMcp.Abstractions.McpIgnore]
        public object DeleteAll() => new { deleted = ""all"" };

        [HttpGet(""d/{id}/x"")]
        public object Find(int? id) => new { id };

        // With .. the URL reaches DeleteAll, as System.Uri removes a dot segment with the one before it.
        [HttpDelete(""named/{name}"")]
        public object DeleteNamed(string name) => new { deleted = name };

        [HttpGet(""files/{**path}"")]
        public object GetFile(string? path) => new { path };

        [HttpGet(""labels/{label?}"")]
        public object GetLabel(string? label) => new { label };

        [HttpGet(""secret"")]
        [Api.ToMcp.Abstractions.McpIgnore]
        public object Secret() => new { secret = true };

        // MVC serves the action at multi too, so a call may leave out its id.
        [HttpGet(""multi/{id}"")]
        [HttpGet(""multi"")]
        public object GetMulti(int? id) => new { id, path = Request.Path.Value };

        // Without their value, DELETE api/items/opt/ and DELETE api/items/files/ reach the action served there, which MVC
        // puts ahead of the optional route value and of the catch-all.
        [HttpDelete(""opt/{id?}"")]
        public object DeleteOpt(Guid? id) => new { deleted = id };

        [HttpDelete(""opt"")]
        [Api.ToMcp.Abstractions.McpIgnore]
        public object DeleteAllOpt() => new { deleted = ""allOpt"" };

        [HttpDelete(""files/{**path}"")]
        public object DeleteFile(string? path) => new { deleted = path };

        [HttpDelete(""files"")]
        [Api.ToMcp.Abstractions.McpIgnore]
        public object DeleteAllFiles() => new { deleted = ""allFiles"" };
    }

    [Route(""plain"")]
    public class PlainEchoController : ControllerBase
    {
        [HttpPost(""search"")]
        public object PostSearch(ProductFilter filter) => filter;

        [HttpGet(""search"")]
        public object GetSearch(ProductFilter? filter) => filter ?? new ProductFilter();

        [HttpPut(""shipments/{id:int}"")]
        public object PutShipment(Shipment shipment) => shipment;

        [HttpPost(""simple"")]
        public object PostSimple(string name, int? count) => new { name, count };

        [HttpPut(""ship/{id}"")]
        public object PutShip(int id, Shipment shipment) => new { id, shipmentId = shipment.Id, shipment.Note };

        [HttpGet(""same/{id}"")]
        public object GetSame(Shipment a, Shipment b) => new { aId = a.Id, aNote = a.Note, bId = b.Id, bNote = b.Note };

        [HttpGet(""record-named"")]
        public object GetRecordNamed(RecordNamedCity filter) => filter;

        [HttpGet(""record-property-named"")]
        public object GetRecordPropertyNamed(RecordPropertyNamedCity filter) => filter;

        [HttpGet(""private-getter"")]
        public object GetPrivateGetter(PrivateGetterFilter filter) => new { filter.City, query = Request.QueryString.Value };

        [HttpGet(""record-query-id/{id}"")]
        public object GetRecordQueryId(int id, RecordQueryId filter) => new { id, filterId = filter.Id, filter.City };

        [HttpGet(""binder-route/{code}"")]
        public object GetBinderRoute([ModelBinder(Name = ""code"")] string value) => new { value, path = Request.Path.Value, query = Request.QueryString.Value };

        [HttpGet(""bind-prefix"")]
        public object GetBindPrefix([Bind(Prefix = ""f"")] ProductFilter filter) => new { filter.City, query = Request.QueryString.Value };

        [HttpGet(""type-prefix"")]
        public object GetTypePrefix(PrefixedFilter filter) => new { filter.City, query = Request.QueryString.Value };

        // MVC binds Id from s.Id under the prefix, never from the route value {id}, which the URL still needs.
        [HttpGet(""bind-prefix/{id}"")]
        public object GetBindPrefixShipment([Bind(Prefix = ""s"")] Shipment shipment) => new { shipment.Id, shipment.Note, query = Request.QueryString.Value };

        // MVC binds id from {id} and the parameter before it from ?id=; filled from that one, the route would delete
        // the record it names.
        [HttpGet(""dup/{id}"")]
        public object GetDup([FromQuery(Name = ""id"")] int x, int id) => new { x, id };

        [HttpDelete(""dup/{id}"")]
        public object DeleteDup([FromQuery(Name = ""id"")] int other, int id) => new { deleted = id, other };

        [HttpGet(""dup-prop/{id}"")]
        public object GetDupProp([FromQuery(Name = ""id"")] int q, [FromRoute] Shipment s) => new { q, sId = s.Id };

        // An empty prefix binds the properties under their own names, whatever the type's [Bind(Prefix)] says.
        [HttpGet(""empty-prefix"")]
        public object GetEmptyPrefix([Bind(Prefix = """")] PrefixedFilter filter) => new { filter.City, query = Request.QueryString.Value };

        [HttpGet(""nested-prefix"")]
        public object GetNestedPrefix([FromQuery] PrefixedWrapper w) => new { city = w.Inner?.City, query = Request.QueryString.Value };
    }

    public sealed class DatedAmount
    {
        public decimal Amount { get; set; }

        public DateOnly Day { get; set; }
    }

    [Flags]
    public enum EchoAccess
    {
        Read = 1,
        Write = 2
    }

    public sealed class MustBindAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public MustBindAttribute() : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Required)
        {
        }
    }

    // Route attributes an app derives from MVC's or implements itself, which MVC reads by their interfaces.
    public sealed class EchoRouteAttribute : RouteAttribute
    {
        public EchoRouteAttribute() : base(""custom/[controller]"")
        {
        }
    }

    public sealed class EchoGetAttribute : HttpGetAttribute
    {
        public EchoGetAttribute(string template) : base(""by/"" + template)
        {
        }
    }

    public sealed class EchoVersionRouteAttribute : RouteAttribute
    {
        public EchoVersionRouteAttribute(int version) : base($""v{version}/[controller]"")
        {
        }
    }

    public enum EchoVersion
    {
        V1 = 1,
        V2 = 2
    }

    public sealed class EchoEnumRouteAttribute : RouteAttribute
    {
        public EchoEnumRouteAttribute(EchoVersion version) : base($""e/{version}/[controller]"")
        {
        }
    }

    [EchoEnumRoute(EchoVersion.V2)]
    public class EnumEchoController : ControllerBase
    {
        [HttpGet(""ping"")]
        public object Ping() => new { path = Request.Path.Value };
    }

    [EchoVersionRoute(2)]
    public class VersionedEchoController : ControllerBase
    {
        [HttpGet(""ping"")]
        public object Ping() => new { path = Request.Path.Value };
    }

    public sealed class EchoCustomGetAttribute : Attribute, Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider, Microsoft.AspNetCore.Mvc.Routing.IActionHttpMethodProvider
    {
        public string? Template { get; set; } = ""fallback"";

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class EchoDocsRouteAttribute : Attribute, Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider
    {
        public string Template => ""docs/{slug}"";

        public int? Order => null;

        public string? Name => null;
    }

    [ApiController]
    [EchoRoute]
    public class CustomRouteController : ControllerBase
    {
        [EchoGet(""{id}"")]
        public object GetById(int id) => new { id, path = Request.Path.Value };

        [AcceptVerbs(""HEAD"", ""PATCH"", Route = ""verbs/{id}"")]
        public object PatchVerbs(int id, [FromBody] ProductFilter filter) => new { id, filter.City, method = Request.Method };

        // A name makes [HttpGet] a route of its own, the controller's, while [Route] serves every method at its template.
        [HttpGet(Name = ""named-get"")]
        [Route(""named/{id}"")]
        public object GetNamed(int id) => new { id };

        [EchoCustomGet(Template = ""custom-get"")]
        public object GetCustom() => new { path = Request.Path.Value };

        [HttpGet]
        [EchoDocsRoute]
        public object GetDocs(string slug) => new { slug };
    }

    // Actions a concrete controller inherits from a generic base, with the base's type arguments substituted.
    [ApiController]
    [Route(""api/[controller]"")]
    public abstract class EchoListController<TId, TFilter> : ControllerBase where TFilter : class
    {
        [HttpGet(""by-id"")]
        public object GetById(TId id) => new { id };

        [HttpPost(""search"")]
        public object PostSearch(TFilter filter) => filter;

        [HttpGet(""find"")]
        public virtual object Find([FromQuery] TFilter filter) => filter;

        [HttpGet(""named"")]
        public virtual object FindNamed([FromQuery(Name = ""c"")] string city) => new { city };

        [HttpGet(""mask"")]
        public virtual object Mask([FromQuery(Name = ""c"")] string city) => new { city };
    }

    // MVC reads a parameter's binding attributes through its model metadata, which inherits them from the parameter
    // at the same position in the method an override replaces.
    public class OrderEchoController : EchoListController<OrderId, ProductFilter>
    {
        public override object Find(ProductFilter filter) => new { filter.City, overridden = true };

        public override object FindNamed(string town) => new { town, overridden = true };

        // Its own [FromQuery] replaces the base's whole, Name included, so MVC binds it as city.
        public override object Mask([FromQuery] string city) => new { city };
    }

    // The tool calls {id}, as 2.4.0's did. [ApiController] binds slug from the route alone, as {id}/{slug} names it.
    [ApiController]
    [Route(""api/uncalled"")]
    public class UncalledRouteController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, string? slug) => new { id, slug, path = Request.Path.Value };
    }

    // Without [ApiController], MVC binds slug from the query string as well.
    [Route(""plain/uncalled"")]
    public class PlainUncalledRouteController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, string? slug) => new { id, slug, path = Request.Path.Value };
    }

    // [ApiController] infers [FromRoute] by the parameter's own name, s, which no route names.
    [ApiController]
    [Route(""api/mcp014-binder"")]
    public class Mcp014BinderController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, [ModelBinder(Name = ""slug"")] string? s) => new { id, s };
    }

    public sealed class Mcp014SlugFilter
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }

    // MVC binds a [FromRoute] property from the route alone, whatever its parameter's source.
    [ApiController]
    [Route(""api/mcp014-filter"")]
    public class Mcp014FilterController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014SlugFilter f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-filter"")]
    public class Mcp014PlainFilterController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014SlugFilter f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-tags"")]
    public class Mcp014TagsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{tags}"")]
        public object Get(int id, [FromRoute] string[]? tags) => new { id, tags };
    }

    public sealed class Mcp014Key
    {
        public int Id { get; set; }

        public int Version { get; set; }
    }

    // id arrives from the query string; key.Id only from {id}.
    [ApiController]
    [Route(""api/mcp014-key"")]
    public class Mcp014KeyController : ControllerBase
    {
        [HttpGet(""all"")]
        [HttpGet(""{id}"")]
        public object Get([FromQuery] int? id, [FromRoute] Mcp014Key key) => new { id, keyId = key.Id };
    }

    // MVC's complex binder binds a [FromRoute] property only where its binding behavior and setter let it. A type's
    // [BindNever] keeps every property without a binding behavior of its own from binding, Page as well.
    public sealed class Mcp014NeverSlug
    {
        [FromRoute]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }
    public sealed class Mcp014NoBindAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public Mcp014NoBindAttribute() : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Never)
        {
        }
    }

    public sealed class Mcp014CustomNeverSlug
    {
        [FromRoute]
        [Mcp014NoBind]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public sealed class Mcp014NeverTypeSlug
    {
        [FromRoute]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public sealed class Mcp014OptionalSlugOfNeverType
    {
        [FromRoute]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Optional)]
        public string? Slug { get; set; }

        public int Page { get; set; }
    }
    public sealed class Mcp014ReadOnlySlug
    {
        [FromRoute]
        public string? Slug { get; }

        public int Page { get; set; }
    }
    public sealed class Mcp014PrivateSetSlug
    {
        [FromRoute]
        public string? Slug { get; private set; }

        public int Page { get; set; }
    }
    public sealed class Mcp014InitSlug
    {
        [FromRoute]
        public string? Slug { get; init; }

        public int Page { get; set; }
    }
    public sealed record Mcp014SlugRecord([FromRoute] string? Slug, int Page);

    [Route(""plain/mcp014-neverslug"")]
    public class Mcp014NeverSlugController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014NeverSlug f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-customneverslug"")]
    public class Mcp014CustomNeverSlugController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014CustomNeverSlug f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-nevertypeslug"")]
    public class Mcp014NeverTypeSlugController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014NeverTypeSlug f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-optionalslugofnevertype"")]
    public class Mcp014OptionalSlugOfNeverTypeController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014OptionalSlugOfNeverType f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-readonlyslug"")]
    public class Mcp014ReadOnlySlugController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014ReadOnlySlug f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-privatesetslug"")]
    public class Mcp014PrivateSetSlugController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014PrivateSetSlug f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-initslug"")]
    public class Mcp014InitSlugController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014InitSlug f) => new { id, f.Slug, f.Page };
    }

    [Route(""plain/mcp014-slugrecord"")]
    public class Mcp014SlugRecordController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{slug}"")]
        public object Get(int id, Mcp014SlugRecord f) => new { id, f.Slug, f.Page };
    }

    public sealed class Mcp014ReadOnlyTags
    {
        [FromRoute]
        public List<string> Tags { get; } = new List<string>();

        public int Page { get; set; }
    }

    // MVC binds a collection without a setter in place.
    [Route(""plain/mcp014-readonlytags"")]
    public class Mcp014ReadOnlyTagsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [HttpGet(""{id}/{tags}"")]
        public object Get(int id, Mcp014ReadOnlyTags f) => new { id, f.Tags, f.Page };
    }

    // MVC binds key's properties under key.Version, which {version} is not.
    [ApiController]
    [Route(""api/mcp014-named-key"")]
    public class Mcp014NamedKeyController : ControllerBase
    {
        [HttpGet(""all"")]
        [HttpGet(""{version}"")]
        public object Get([FromRoute(Name = ""key"")] Mcp014Key key) => new { key.Version };
    }
}
";

    private const string OrderGuid = "0189dfd3-5bd1-a760-bed1-d1d5751e4e8c";

    private readonly MvcHost _host;

    public GeneratedToolsAgainstMvcTests(MvcHost host)
    {
        _host = host;
    }

    [Theory]
    [InlineData("EchoController_GetAttributedTool", @"{""q"":{""id"":7,""city"":""Brno"",""note"":""n1"",""tenant"":""t1""}}", @"{""id"":7,""city"":""Brno"",""note"":""n1"",""tenant"":null}")]
    [InlineData("EchoController_PostAttributedTool", @"{""q"":{""id"":7,""city"":""Brno"",""note"":""n1""}}", @"{""id"":7,""city"":""Brno"",""note"":""n1"",""tenant"":null}")]
    [InlineData("EchoController_GetQueryOnlyTool", @"{""q"":{""city"":""Brno"",""note"":""n1""}}", @"{""city"":""Brno"",""note"":""n1""}")]
    [InlineData("EchoController_GetAliasedTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno""}}", @"{""order"":""" + OrderGuid + @""",""city"":""Brno""}")]
    [InlineData("EchoController_GetFromRouteTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno""}}", @"{""order"":""" + OrderGuid + @""",""city"":""Brno""}")]
    [InlineData("EchoController_PostRecordPropertyTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostRecordParameterTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostRecordWithQueryTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostNamedBinderTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostNamedBindPropertyTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostKeywordTool", @"{""namespace"":""ns"",""q"":{""id"":7,""event"":""launch""},""class"":""c1"",""object"":{""city"":""Brno""}}", @"{""namespace"":""ns"",""id"":7,""event"":""launch"",""class"":""c1"",""city"":""Brno""}")]
    [InlineData("EchoController_GetPrivateGetterTool", @"{""q"":{""id"":7}}", @"{""id"":7,""echo"":null}")]
    [InlineData("EchoController_GetTwoTool", @"{""a"":{""id"":7,""city"":""Brno"",""note"":""na""},""b"":{""id"":9,""note"":""nb""}}", @"{""aId"":7,""city"":""Brno"",""aNote"":""na"",""bId"":9,""bNote"":""nb""}")]
    [InlineData("EchoController_PostRecordNamedTool", @"{""q"":{""city"":""Brno"",""page"":3,""extra"":1}}", @"{""city"":""Brno"",""page"":3,""extra"":1}")]
    [InlineData("EchoController_GetRecordRouteTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno"",""page"":3}}", @"{""order"":""" + OrderGuid + @""",""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_GetQueryIdTool", @"{""q"":{""id"":9,""city"":""Brno""}}", @"{""id"":9,""city"":null}")]
    [InlineData("EchoController_GetDirectTool", @"{""id"":7,""q"":{""id"":7,""city"":""Brno""}}", @"{""id"":7,""qId"":7,""city"":""Brno""}")]
    [InlineData("EchoController_GetOrderTool", @"{""orderId"":{""value"":""" + OrderGuid + @"""}}", @"{""orderId"":""" + OrderGuid + @"""}")]
    [InlineData("EchoController_PostBodyIdTool", @"{""id"":{""value"":""" + OrderGuid + @"""}}", @"{""id"":""" + OrderGuid + @"""}")]
    [InlineData("EchoController_PostUpperTool", @"{""id"":7}", @"{""id"":7}")]
    [InlineData("EchoController_GetOrderedTool", @"{""name"":""a"",""count"":2,""orderId"":{""value"":""" + OrderGuid + @"""}}", @"{""name"":""a"",""count"":2,""orderId"":""" + OrderGuid + @"""}")]
    [InlineData("EchoController_PostClashTool", @"{""route"":""r1"",""invoker"":""i1"",""queryParts"":""q1"",""bodyJson"":{""city"":""Brno""},""cancellationToken"":""c1"",""routeroute"":""rr"",""System"":""s1"",""JsonSerializer"":""j1"",""McpScope"":""m1""}", @"{""route"":""r1"",""invoker"":""i1"",""queryParts"":""q1"",""city"":""Brno"",""cancellationToken"":""c1"",""routeroute"":""rr"",""system"":""s1"",""jsonSerializer"":""j1"",""mcpScope"":""m1""}")]
    [InlineData("EchoController_GetQueryRecordTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno"",""page"":3}}", @"{""order"":""" + OrderGuid + @""",""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_GetQueryAliasedTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno""}}", @"{""order"":""" + OrderGuid + @""",""city"":""Brno""}")]
    // MVC binds a parameter with a name of its own under that prefix, so never from the route.
    [InlineData("EchoController_GetQueryNamedTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno"",""page"":3}}", @"{""order"":""00000000-0000-0000-0000-000000000000"",""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostBodyRouteTool", @"{""body"":{""id"":9,""note"":""n""},""q"":{""id"":7,""city"":""Brno""}}", @"{""bodyId"":9,""qId"":7,""city"":""Brno""}")]
    [InlineData("EchoController_GetTextOrderTool", @"{""id"":{""value"":""A-7""}}", @"{""id"":""A-7""}")]
    [InlineData("EchoController_PostTextOrderTool", @"{""id"":{""value"":""A-7""}}", @"{""id"":""A-7""}")]
    [InlineData("EchoController_PostPlainCodeTool", @"{""value"":{""value"":""B-8""}}", @"{""code"":""B-8""}")]
    [InlineData("EchoController_PostTypedBinderTool", @"{""q"":{""ref"":{""value"":5},""city"":""Brno""}}", @"{""ref"":{""value"":5},""city"":""Brno""}")]
    [InlineData("EchoController_PostNullableBinderTool", @"{""q"":{""ref"":{""value"":5},""city"":""Brno""}}", @"{""ref"":{""value"":5},""city"":""Brno""}")]
    [InlineData("EchoController_GetHeaderTool", @"{""tenant"":""t 1"",""ids"":[1,2],""tags"":[""a,b"",""c""],""status"":1}", @"{""tenant"":""t 1"",""ids"":[1,2],""tags"":[""a,b"",""c""],""status"":1}")]
    [InlineData("EchoController_GetHeaderTool", @"{}", @"{""tenant"":null,""ids"":[],""tags"":[],""status"":null}")]
    [InlineData("EchoController_GetHeaderTool", @"{""tenant"":""  t1\t "",""tags"":["" sp "",""a, b""]}", @"{""tenant"":""t1"",""ids"":[],""tags"":[""sp"",""a, b""],""status"":null}")]
    [InlineData("EchoController_PostBindPrefixTool", @"{""review"":{""city"":""Brno""},""tenant"":""t1""}", @"{""city"":""Brno"",""tenant"":""t1""}")]
    [InlineData("EchoController_GetOwnHeadersTool", @"{""auth"":""Bearer forged"",""userId"":""bob"",""clientIp"":""10.0.0.5""}", @"{""auth"":null,""userId"":null,""clientIp"":null}")]
    [InlineData("EchoController_GetHeaderDefaultTool", @"{""page"":3}", @"{""page"":3,""sent"":true}")]
    [InlineData("EchoController_GetHeaderDefaultTool", @"{}", @"{""page"":2,""sent"":false}")]
    [InlineData("EchoController_PostFormTool", @"{""name"":""a b&c=d+e"",""filter"":{""city"":""Brno""},""ids"":[1,2],""status"":0}", @"{""name"":""a b&c=d+e"",""city"":""Brno"",""ids"":[1,2],""status"":0,""isForm"":true}")]
    [InlineData("EchoController_PostFormTool", @"{}", @"{""name"":null,""city"":null,""ids"":[],""status"":1,""isForm"":true}")]
    [InlineData("EchoController_PutFormRouteTool", @"{""q"":{""id"":7,""note"":""n1"",""city"":""Brno""}}", @"{""id"":7,""note"":""n1""}")]
    [InlineData("EchoController_PostFormHeaderTool", @"{""id"":3,""shipment"":{""id"":9,""note"":""fragile""},""key"":""k-1""}", @"{""id"":3,""note"":""fragile"",""key"":""k-1""}")]
    [InlineData("CustomRouteController_GetByIdTool", @"{""id"":7}", @"{""id"":7,""path"":""/custom/customroute/by/7""}")]
    [InlineData("CustomRouteController_PatchVerbsTool", @"{""id"":7,""filter"":{""city"":""Brno""}}", @"{""id"":7,""city"":""Brno"",""method"":""PATCH""}")]
    [InlineData("CustomRouteController_GetNamedTool", @"{""id"":7}", @"{""id"":7}")]
    [InlineData("VersionedEchoController_PingTool", @"{}", @"{""path"":""/v2/versionedecho/ping""}")]
    [InlineData("EnumEchoController_PingTool", @"{}", @"{""path"":""/e/V2/enumecho/ping""}")]
    [InlineData("CustomRouteController_GetCustomTool", @"{}", @"{""path"":""/custom/customroute/custom-get""}")]
    [InlineData("CustomRouteController_GetDocsTool", @"{""slug"":""intro""}", @"{""slug"":""intro""}")]
    [InlineData("EchoController_GetFileTool", @"{""path"":""a/b c""}", @"{""path"":""a/b c""}")]
    [InlineData("EchoController_GetStarTool", @"{""path"":""a/b c""}", @"{""path"":""a/b c""}")]
    [InlineData("EchoController_GetOptionalTool", @"{}", @"{""id"":null,""path"":""/api/optional""}")]
    [InlineData("EchoController_GetDefaultsTool", @"{}", @"{""unknown"":99,""page"":2,""text"":""x"",""query"":""""}")]
    [InlineData("EchoController_GetDefaultsTool", @"{""unknown"":1,""page"":3}", @"{""unknown"":1,""page"":3,""text"":""x"",""query"":""?unknown=Closed&page=3""}")]
    [InlineData("EchoController_GetBindRequiredTool", @"{}", @"{""page"":1,""query"":""?page=1""}")]
    [InlineData("EchoController_GetRequiredDefaultTool", @"{}", @"{""size"":1,""query"":""?size=1""}")]
    [InlineData("EchoController_GetMustBindTool", @"{}", @"{""page"":1,""query"":""?page=1""}")]
    [InlineData("EchoController_GetAccessTool", @"{""access"":3}", @"{""access"":3,""query"":""?access=Read%2C%20Write""}")]
    [InlineData("EchoController_GetFileNameTool", @"{""name"":""report""}", @"{""name"":""report"",""ext"":""txt"",""path"":""/api/names/report.txt""}")]
    [InlineData("EchoController_GetOptionalFileNameTool", @"{""name"":""report""}", @"{""name"":""report"",""ext"":null,""path"":""/api/optional-names/report""}")]
    [InlineData("EchoController_GetOptionalFileNameTool", @"{""name"":""report"",""ext"":""""}", @"{""name"":""report"",""ext"":null,""path"":""/api/optional-names/report""}")]
    [InlineData("EchoController_GetOptionalFileNameTool", @"{""name"":""report"",""ext"":""csv""}", @"{""name"":""report"",""ext"":""csv"",""path"":""/api/optional-names/report.csv""}")]
    [InlineData("EchoController_GetOptionalFileSizeTool", @"{""name"":""report""}", @"{""name"":""report"",""ext"":null,""path"":""/api/optional-names/report/size""}")]
    [InlineData("EchoController_GetDefaultRouteTool", @"{}", @"{""status"":99,""path"":""/api/default-route/""}")]
    [InlineData("EchoController_GetDefaultRouteTool", @"{""status"":1}", @"{""status"":1,""path"":""/api/default-route/Closed""}")]
    [InlineData("EchoController_PostDefaultBodyTool", @"{}", @"{""status"":99}")]
    [InlineData("EchoController_GetNanTool", @"{}", @"{""isNaN"":true,""inf"":true,""negInf"":true,""query"":""""}")]
    [InlineData("EchoController_GetNanTool", @"{""value"":1.5}", @"{""isNaN"":false,""inf"":true,""negInf"":true,""query"":""?value=1.5""}")]
    [InlineData("PlainEchoController_PostSearchTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno""}")]
    [InlineData("PlainEchoController_GetSearchTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno""}")]
    [InlineData("PlainEchoController_PutShipmentTool", @"{""shipment"":{""id"":9,""note"":""fragile""}}", @"{""id"":9,""note"":""fragile""}")]
    [InlineData("PlainEchoController_PostSimpleTool", @"{""name"":""a"",""count"":2}", @"{""name"":""a"",""count"":2}")]
    [InlineData("PlainEchoController_PutShipTool", @"{""id"":7,""shipment"":{""id"":9,""note"":""x""}}", @"{""id"":7,""shipmentId"":9,""note"":""x""}")]
    [InlineData("PlainEchoController_GetSameTool", @"{""a"":{""id"":7,""note"":""na""},""b"":{""id"":9,""note"":""nb""}}", @"{""aId"":7,""aNote"":""na"",""bId"":9,""bNote"":""nb""}")]
    [InlineData("PlainEchoController_GetRecordNamedTool", @"{""filter"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3,""extra"":0}")]
    [InlineData("PlainEchoController_GetRecordPropertyNamedTool", @"{""filter"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3,""extra"":0}")]
    [InlineData("PlainEchoController_GetPrivateGetterTool", @"{""filter"":{""city"":""Brno"",""secret"":""s1""}}", @"{""city"":""Brno"",""query"":""?filter.City=Brno""}")]
    [InlineData("PlainEchoController_GetRecordQueryIdTool", @"{""id"":7,""filter"":{""id"":9,""city"":""Brno""}}", @"{""id"":7,""filterId"":9,""city"":""Brno""}")]
    [InlineData("OrderEchoController_GetByIdTool", @"{""id"":{""value"":""" + OrderGuid + @"""}}", @"{""id"":{""value"":""" + OrderGuid + @"""}}")]
    [InlineData("OrderEchoController_PostSearchTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno""}")]
    [InlineData("OrderEchoController_FindTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno"",""overridden"":true}")]
    [InlineData("OrderEchoController_FindNamedTool", @"{""town"":""Brno""}", @"{""town"":""Brno"",""overridden"":true}")]
    [InlineData("OrderEchoController_MaskTool", @"{""city"":""Brno""}", @"{""city"":""Brno""}")]
    [InlineData("ItemsController_GetTool", @"{""id"":""" + OrderGuid + @"""}", @"{""id"":""" + OrderGuid + @"""}")]
    [InlineData("ItemsController_DeleteTool", @"{""id"":""" + OrderGuid + @"""}", @"{""deleted"":""" + OrderGuid + @"""}")]
    [InlineData("ItemsController_FindTool", @"{""id"":7}", @"{""id"":7}")]
    [InlineData("ItemsController_DeleteNamedTool", @"{""name"":""a.b""}", @"{""deleted"":""a.b""}")]
    [InlineData("ItemsController_DeleteNamedTool", @"{""name"":""...""}", @"{""deleted"":""...""}")]
    [InlineData("ItemsController_GetFileTool", @"{""path"":""..a/b..""}", @"{""path"":""..a/b..""}")]
    [InlineData("ItemsController_GetFileTool", @"{}", @"{""path"":null}")]
    [InlineData("ItemsController_GetLabelTool", @"{}", @"{""label"":null}")]
    [InlineData("ItemsController_GetMultiTool", @"{""id"":7}", @"{""id"":7,""path"":""/api/items/multi/7""}")]
    [InlineData("ItemsController_GetMultiTool", @"{}", @"{""id"":null,""path"":""/api/items/multi/""}")]
    [InlineData("ItemsController_DeleteOptTool", @"{""id"":""" + OrderGuid + @"""}", @"{""deleted"":""" + OrderGuid + @"""}")]
    [InlineData("ItemsController_DeleteFileTool", @"{""path"":""a/b""}", @"{""deleted"":""a/b""}")]
    [InlineData("EchoController_GetBinderNamedTool", @"{""search"":""brno""}", @"{""search"":""brno"",""query"":""?q=brno""}")]
    [InlineData("EchoController_GetQueryBindPrefixTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno"",""query"":""?f.City=Brno""}")]
    [InlineData("EchoController_GetRouteNamedTool", @"{""id"":7}", @"{""id"":7,""path"":""/api/route-named/7""}")]
    [InlineData("EchoController_DeleteRouteNamedTool", @"{""code"":""p-1""}", @"{""deleted"":""p-1""}")]
    [InlineData("EchoController_GetBinderRouteTool", @"{""value"":""A-7""}", @"{""value"":""A-7"",""path"":""/api/binder-route/A-7"",""query"":""?code=A-7""}")]
    [InlineData("PlainEchoController_GetBinderRouteTool", @"{""value"":""A-7""}", @"{""value"":""A-7"",""path"":""/plain/binder-route/A-7"",""query"":""""}")]
    [InlineData("EchoController_GetRouteObjectTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno""}}", @"{""order"":""00000000-0000-0000-0000-000000000000"",""city"":""Brno"",""query"":""?o.c=Brno""}")]
    [InlineData("PlainEchoController_GetBindPrefixTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno"",""query"":""?f.City=Brno""}")]
    [InlineData("PlainEchoController_GetTypePrefixTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno"",""query"":""?pf.City=Brno""}")]
    [InlineData("PlainEchoController_GetBindPrefixShipmentTool", @"{""shipment"":{""id"":9,""note"":""x""}}", @"{""id"":9,""note"":""x"",""query"":""?s.Id=9&s.Note=x""}")]
    [InlineData("PlainEchoController_GetDupTool", @"{""x"":1,""id"":2}", @"{""x"":1,""id"":2}")]
    [InlineData("PlainEchoController_DeleteDupTool", @"{""other"":1,""id"":2}", @"{""deleted"":2,""other"":1}")]
    [InlineData("PlainEchoController_GetDupPropTool", @"{""q"":1,""s"":{""id"":2}}", @"{""q"":1,""sId"":2}")]
    [InlineData("EchoController_GetApiDupTool", @"{""value"":""V"",""code"":""C""}", @"{""value"":""V"",""code"":""C""}")]
    [InlineData("EchoController_GetQueryObjectTool", @"{""shipment"":{""id"":9,""note"":""x""}}", @"{""id"":9,""note"":""x"",""path"":""/api/query-object/9""}")]
    [InlineData("EchoController_PutTwoTool", @"{""id"":5,""body"":{""id"":""" + OrderGuid + @"""}}", @"{""id"":5,""bodyId"":""" + OrderGuid + @""",""routeId"":""" + OrderGuid + @"""}")]
    [InlineData("PlainEchoController_GetEmptyPrefixTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno"",""query"":""?City=Brno""}")]
    [InlineData("PlainEchoController_GetNestedPrefixTool", @"{""w"":{""inner"":{""city"":""Brno""}}}", @"{""city"":""Brno"",""query"":""?w.pf.City=Brno""}")]
    public async Task Tool_DeliversItsArguments_AsMvcBindsThem(string toolClassName, string arguments, string expectedEcho)
    {
        var (statusCode, body) = await _host.InvokeToolAsync(toolClassName, arguments);

        Assert.True(statusCode == StatusCodes.Status200OK, $"{toolClassName}: {statusCode} {body}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedEcho), JsonNode.Parse(body)), $"{toolClassName}: expected {expectedEcho}, MVC bound {body}");
    }

    [Theory]
    [InlineData("EchoController_GetAccessTool", @"{""access"":8}")]
    [InlineData("EchoController_GetAccessTool", @"{""access"":0}")]
    [InlineData("EchoController_GetDefaultsTool", @"{""unknown"":5}")]
    public async Task Mvc_RefusesAnEnumValueAsText_ThatNoMemberNorCombinationOfFlagsMakes(string toolClassName, string arguments)
    {
        // So a tool that cannot tell whether MVC requires a value leaves out such a default rather than send it.
        var (statusCode, body) = await _host.InvokeToolAsync(toolClassName, arguments);

        Assert.True(statusCode == StatusCodes.Status400BadRequest, $"{toolClassName}: {statusCode} {body}");
    }

    [Theory]
    [InlineData("ItemsController_DeleteTool")]
    [InlineData("ItemsController_GetTool")]
    [InlineData("ItemsController_FindTool")]
    [InlineData("ItemsController_DeleteOptTool")]
    [InlineData("ItemsController_DeleteFileTool")]
    public async Task Tool_RefusesACallThatLeavesOutARequiredRouteValue_WithoutCallingTheApi(string toolClassName)
    {
        // The URL without the value is another action's: DELETE api/items/ runs DeleteAll, which [McpIgnore] keeps out of MCP.
        var requestCount = _host.RequestCount;

        var exception = await Record.ExceptionAsync(() => _host.CallToolAsync(toolClassName, "{}"));

        Assert.True(requestCount == _host.RequestCount, $"{toolClassName} called the API, which answered {_host.LastResponse}");
        Assert.IsType<ArgumentException>(exception);
    }

    [Theory]
    [InlineData("ItemsController_DeleteTool", @"{""id"":null}")]
    [InlineData("ItemsController_GetTool", @"{""id"":null}")]
    [InlineData("ItemsController_FindTool", @"{""id"":null}")]
    [InlineData("EchoController_GetFileNameTool", @"{""name"":""""}")]
    [InlineData("ItemsController_DeleteNamedTool", @"{""name"":""""}")]
    [InlineData("ItemsController_DeleteNamedTool", @"{""name"":"".""}")]
    [InlineData("ItemsController_DeleteNamedTool", @"{""name"":""..""}")]
    [InlineData("EchoController_GetFileNameTool", @"{""name"":"".""}")]
    [InlineData("EchoController_GetOptionalFileNameTool", @"{""name"":""report"",""ext"":"".""}")]
    [InlineData("ItemsController_GetLabelTool", @"{""label"":""..""}")]
    [InlineData("ItemsController_GetFileTool", @"{""path"":""../secret""}")]
    [InlineData("ItemsController_GetFileTool", @"{""path"":""a/./b""}")]
    [InlineData("ItemsController_GetFileTool", @"{""path"":""..""}")]
    [InlineData("ItemsController_DeleteOptTool", @"{""id"":null}")]
    [InlineData("ItemsController_DeleteFileTool", @"{""path"":""""}")]
    [InlineData("ItemsController_DeleteFileTool", @"{""path"":null}")]
    public async Task Tool_RefusesARouteValueThatChangesThePath_WithoutCallingTheApi(string toolClassName, string arguments)
    {
        // DELETE api/items/named/.. is DELETE api/items/, DeleteAll's URL, and files/../secret reaches Secret, both kept
        // out of MCP.
        var requestCount = _host.RequestCount;

        var result = await _host.CallToolAsync(toolClassName, arguments);

        Assert.True(requestCount == _host.RequestCount, $"{toolClassName} called the API, which answered {_host.LastResponse}");
        Assert.True(result.IsError, $"{toolClassName}: {GetText(result)}");
    }

    [Theory]
    [InlineData(@"{""tags"":[""\""q\""""]}")]
    [InlineData(@"{""tags"":[""x,\""y"",""z""]}")]
    [InlineData(@"{""tags"":[""a"",""""]}")]
    [InlineData(@"{""tags"":[""a"","" \t ""]}")]
    public async Task Tool_RefusesAHeaderArrayItemMvcWouldReadBackOtherwise_WithoutCallingTheApi(string arguments)
    {
        // MVC splits the header at its commas, drops an empty item and removes or splits at a quote.
        var requestCount = _host.RequestCount;

        var result = await _host.CallToolAsync("EchoController_GetHeaderTool", arguments);

        Assert.True(requestCount == _host.RequestCount, $"The tool called the API, which answered {_host.LastResponse}");
        Assert.True(result.IsError);
        Assert.Equal(
            "The argument 'tags' is sent as the request header 'X-Tags', one value per item, and an item cannot be empty and can hold only visible ASCII characters other than '\"', spaces and tabs.",
            GetText(result));
    }

    [Fact]
    public async Task Tool_WritesFormNumbersAndDates_InTheCultureMvcReadsTheFormWith()
    {
        // MVC reads a form with the request's culture and the query string and headers with the invariant one, so under
        // de-DE 1234.25 written invariantly arrives as 123425. The app's request localization defaults to de-DE, and the
        // tool call runs under the culture the MCP request got, the same default.
        const string arguments = @"{""amount"":1234.25,""ratio"":-0.5,""day"":""2026-09-28"",""delta"":-12,""amounts"":[1.5,2.25],""headerAmount"":1.25,""queryAmount"":1.25}";
        const string expected = @"{""amount"":1234.25,""ratio"":-0.5,""day"":""2026-09-28"",""delta"":-12,""amounts"":[1.5,2.25],""headerAmount"":1.25,""queryAmount"":1.25}";

        var (germanStatus, germanBody) = await _host.InvokeToolAsync("EchoController_PostAmountsTool", arguments, MvcHost.GermanCulture);
        var (statusCode, body) = await _host.InvokeToolAsync("EchoController_PostAmountsTool", arguments);

        Assert.True(germanStatus == StatusCodes.Status200OK, $"{germanStatus} {germanBody}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(germanBody)), $"de-DE: MVC bound {germanBody}");
        Assert.True(statusCode == StatusCodes.Status200OK, $"{statusCode} {body}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(body)), $"invariant: MVC bound {body}");
    }

    [Fact]
    public async Task Tool_NamesTheCultureItWritesTheFormIn_SoTheAppReadsTheFormInIt()
    {
        // The MCP client asked for en-US, which the app supports, so the tool call runs under it and writes 1234.25 as
        // "1,234.25". The tool's own request named no culture, so the app read the form in its default, de-DE.
        const string arguments = @"{""amount"":1234.25,""ratio"":-0.5,""day"":""2026-11-13"",""delta"":-12,""amounts"":[1.5,2.25]}";
        const string expected = @"{""amount"":1234.25,""ratio"":-0.5,""day"":""2026-11-13"",""delta"":-12,""amounts"":[1.5,2.25],""headerAmount"":null,""queryAmount"":null}";

        var (statusCode, body) = await _host.InvokeToolAsync("EchoController_PostAmountsTool", arguments, MvcHost.EnglishCulture);
        var (cultureStatus, cultureBody) = await _host.InvokeToolAsync("EchoController_PostLocalizedTool", @"{""amount"":1.5}", MvcHost.EnglishCulture);

        Assert.True(statusCode == StatusCodes.Status200OK, $"{statusCode} {body}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(body)), $"en-US: MVC bound {body}");
        Assert.True(cultureStatus == StatusCodes.Status200OK, $"{cultureStatus} {cultureBody}");
        Assert.Equal(@"{""language"":""en-US"",""amount"":1.5,""culture"":""en-US""}", cultureBody);
    }

    [Theory]
    [InlineData("EchoController_PostLocalizedTool", @"{""amount"":1.25,""language"":""de-DE""}", @"{""language"":""de-DE"",""amount"":1.25,""culture"":""de-DE""}")]
    [InlineData("EchoController_PostLocalizedTool", @"{""amount"":1.25}", @"{""language"":""de-DE"",""amount"":1.25,""culture"":""de-DE""}")]
    [InlineData("EchoController_PostLocalizedQueryTool", @"{""amount"":1.25,""culture"":""de-DE""}", @"{""culture"":""de-DE"",""amount"":1.25,""requestCulture"":""de-DE""}")]
    [InlineData("EchoController_PostLocalizedQueryTool", @"{""amount"":1.25}", @"{""culture"":null,""amount"":1.25,""requestCulture"":""de-DE""}")]
    public async Task Tool_SendsAFormWithACultureArgument_ThatNamesTheCultureItWritesTheFormIn(string toolClassName, string arguments, string expected)
    {
        var (statusCode, body) = await _host.InvokeToolAsync(toolClassName, arguments, MvcHost.GermanCulture);

        Assert.True(statusCode == StatusCodes.Status200OK, $"{statusCode} {body}");
        Assert.Equal(expected, body);
    }

    [Theory]
    [InlineData("EchoController_PostLocalizedTool", @"{""amount"":1.25,""language"":""en-US""}", "The argument 'language' is sent as the request header 'Accept-Language', which")]
    [InlineData("EchoController_PostLocalizedQueryTool", @"{""amount"":1.25,""culture"":""en-US""}", "The query value 'culture' of the tool's request")]
    public async Task Tool_RefusesACultureArgumentNamingAnotherCultureThanTheFormIsWrittenIn_WithoutCallingTheApi(string toolClassName, string arguments, string subject)
    {
        // With the AI's en-US the app read the 1,25 the tool wrote in the MCP request's de-DE as 125.
        var requestCount = _host.GermanRequestCount;

        var result = await _host.CallToolAsync(toolClassName, arguments, MvcHost.GermanCulture);

        Assert.True(requestCount == _host.GermanRequestCount, $"The tool called the API, which answered {_host.GermanLastResponse}");
        Assert.True(result.IsError);
        Assert.StartsWith(subject, GetText(result));
        Assert.Contains("the tool writes the form's numbers and dates in the culture of the MCP request, 'de-DE'.", GetText(result));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("")]
    public async Task Tool_SendsACultureArgumentAsItIs_WhenTheAppRunsNoRequestLocalization(string serverCultureName)
    {
        // The app reads the form in the server's culture, which the tool call runs under and writes the form in, so the
        // AI's language or culture is the action's own data, also under invariant globalization, whose culture has no name.
        var serverCulture = CultureInfo.GetCultureInfo(serverCultureName);
        (string ToolClassName, string Arguments, string Expected)[] calls =
        [
            ("EchoController_PostTranslateTool", @"{""text"":""hello"",""language"":""de-DE""}", $@"{{""language"":""de-DE"",""text"":""hello"",""culture"":""{serverCultureName}""}}"),
            ("EchoController_PostNamesTool", @"{""name"":""Ann"",""culture"":""fr-FR""}", $@"{{""culture"":""fr-FR"",""name"":""Ann"",""requestCulture"":""{serverCultureName}""}}"),
            ("EchoController_PostLocalizedTool", @"{""amount"":1.5,""language"":""de-DE""}", $@"{{""language"":""de-DE"",""amount"":1.5,""culture"":""{serverCultureName}""}}")
        ];

        foreach (var (toolClassName, arguments, expected) in calls)
        {
            var (statusCode, body, result) = await _host.CallToolWithoutLocalizationAsync(toolClassName, arguments, serverCulture);

            Assert.True(result.IsError != true, $"{toolClassName}: {GetText(result)}");
            Assert.True(statusCode == StatusCodes.Status200OK, $"{toolClassName}: {statusCode} {body}");
            Assert.Equal(expected, body);
        }
    }

    [Theory]
    [InlineData("EchoController_PostTranslateTool", @"{""text"":""hello"",""language"":""en-US""}", @"{""language"":""en-US"",""text"":""hello"",""culture"":""en-US""}")]
    [InlineData("EchoController_PostNamesTool", @"{""name"":""Ann"",""culture"":""en-US""}", @"{""culture"":""en-US"",""name"":""Ann"",""requestCulture"":""en-US""}")]
    public async Task Tool_SendsACultureArgumentAsItIs_ForAFormOfTextAlone_UnderRequestLocalization(string toolClassName, string arguments, string expected)
    {
        // The app reads the text in en-US, as the AI asked, and a text reads the same in every culture.
        var (statusCode, body) = await _host.InvokeToolAsync(toolClassName, arguments, MvcHost.GermanCulture);

        Assert.True(statusCode == StatusCodes.Status200OK, $"{statusCode} {body}");
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task Tool_WritesAFormDate_InTheCalendarOfTheCultureItWritesTheFormIn()
    {
        // fa-IR writes dates in the Persian calendar, 1405/08/22, which MVC reads back in the same culture.
        var (statusCode, body) = await _host.InvokeToolAsync("EchoController_PostDatedTool", @"{""a"":{""amount"":1.5,""day"":""2026-11-13""}}", MvcHost.PersianCulture);

        Assert.True(statusCode == StatusCodes.Status200OK, $"{statusCode} {body}");
        Assert.Equal(@"{""amount"":1.5,""day"":""2026-11-13""}", body);
    }

    [Fact]
    public async Task Tool_RefusesAFormDateOutsideTheCalendarOfTheCultureItWritesTheFormIn_WithoutCallingTheApi()
    {
        // A date left out is 0001-01-01, before the Persian calendar starts, so writing it threw and the AI saw only
        // "An error occurred invoking".
        var requestCount = _host.GermanRequestCount;

        var result = await _host.CallToolAsync("EchoController_PostDatedTool", @"{""a"":{""amount"":1.5}}", MvcHost.PersianCulture);

        Assert.True(requestCount == _host.GermanRequestCount, $"The tool called the API, which answered {_host.GermanLastResponse}");
        Assert.True(result.IsError);
        Assert.Equal(
            "The argument 'a' holds a date that the tool cannot write in the calendar of the MCP request's culture, in which it writes the form. Give a date that calendar holds; a date left out counts as 0001-01-01.",
            GetText(result));
    }

    [Fact]
    public async Task Tool_RefusesAHeaderValueAnHttpHeaderCannotHold_WithoutCallingTheApi()
    {
        var requestCount = _host.RequestCount;

        var result = await _host.CallToolAsync("EchoController_GetHeaderTool", @"{""tenant"":""a\r\nX-Injected: 1""}");

        Assert.True(requestCount == _host.RequestCount, $"The tool called the API, which answered {_host.LastResponse}");
        Assert.True(result.IsError);
        Assert.Equal("The argument 'tenant' is sent as the request header 'X-Tenant', which can hold only visible ASCII characters, spaces and tabs.", GetText(result));
    }

    [Fact]
    public async Task Tool_NamesTheArgument_WhenTheClientSendsNullForARequiredRouteValue()
    {
        var result = await _host.CallToolAsync("ItemsController_DeleteTool", @"{""id"":null}");

        Assert.True(result.IsError);
        Assert.Equal("The argument 'id' fills the route value {id} of the API's URL, which cannot be empty, '.' or '..'.", GetText(result));
    }

    [Fact]
    public async Task Tool_NamesTheArgumentByItsOwnName_WhenItFillsTheRouteValueItsBindingNameNames()
    {
        // The input schema names the argument code; {productCode} is where [FromRoute(Name = "productCode")] sends it.
        var requestCount = _host.RequestCount;

        var result = await _host.CallToolAsync("EchoController_DeleteRouteNamedTool", @"{""code"":""""}");

        Assert.True(requestCount == _host.RequestCount, $"The tool called the API, which answered {_host.LastResponse}");
        Assert.True(result.IsError);
        Assert.Equal("The argument 'code' fills the route value {productCode} of the API's URL, which cannot be empty, '.' or '..'.", GetText(result));
    }

    [Theory]
    [InlineData("ItemsController_DeleteTool", "id")]
    [InlineData("ItemsController_GetTool", "id")]
    [InlineData("ItemsController_FindTool", "id")]
    [InlineData("ItemsController_DeleteOptTool", "id")]
    [InlineData("ItemsController_DeleteFileTool", "path")]
    [InlineData("EchoController_DeleteRouteNamedTool", "code")]
    public void Tool_RequiresTheArgumentThatFillsARequiredRouteValue_WhateverItsType(string toolClassName, string argumentName)
    {
        var inputSchema = _host.GetInputSchema(toolClassName);

        Assert.Equal([argumentName], inputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Tool_LeavesOutARouteValue_ThatAnotherRouteOfTheActionIsServedWithout()
    {
        var inputSchema = _host.GetInputSchema("ItemsController_GetMultiTool");

        var result = await _host.CallToolAsync("ItemsController_GetMultiTool", "{}");

        Assert.False(inputSchema.TryGetProperty("required", out _), inputSchema.GetRawText());
        Assert.NotEqual(true, result.IsError);
        Assert.Equal((StatusCodes.Status200OK, @"{""id"":null,""path"":""/api/items/multi/""}"), _host.LastResponse);
    }

    [Fact]
    public void Tool_RequiresTheArgumentsItsActionRequires_WhenAnOptionalOneComesBeforeThem()
    {
        var inputSchema = _host.GetInputSchema("EchoController_GetOrderedTool");

        Assert.Equal(["name", "orderId"], inputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["count", "name", "orderId"], inputSchema.GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
    }

    [Fact]
    public void Tool_OfAnActionWithANaNOrInfiniteDefault_HasAnInputSchemaTheSdkCanWrite()
    {
        // JSON has no NaN or infinity, so the MCP SDK throws writing such a default into the schema, and the host with it.
        var properties = _host.GetInputSchema("EchoController_GetNanTool").GetProperty("properties");

        Assert.Equal(["inf", "negInf", "value"], properties.EnumerateObject().Select(x => x.Name).Order());
    }

    [Theory]
    [InlineData("UncalledRouteController_GetTool", @"{""id"":7,""slug"":null,""path"":""/api/uncalled/7""}", true)]
    [InlineData("PlainUncalledRouteController_GetTool", @"{""id"":7,""slug"":""red-mug"",""path"":""/plain/uncalled/7""}", false)]
    public async Task Tool_LosesTheValueMcp014Names_AndOnlyThat(string toolClassName, string expectedEcho, bool isReported)
    {
        // The tool sends slug in the query string of {id}: [ApiController] binds slug from the route alone, which that
        // URL lacks, while a controller without it binds slug from the query string.
        var result = await _host.CallToolAsync(toolClassName, @"{""id"":7,""slug"":""red-mug""}");

        Assert.True(result.IsError != true, GetText(result));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedEcho), JsonNode.Parse(_host.LastResponse.Body)), $"{toolClassName}: expected {expectedEcho}, MVC bound {_host.LastResponse.Body}");
        Assert.Equal(isReported, _host.GeneratorDiagnostics.Any(x => x.Id == "MCP014" && x.GetMessage().Contains($"'{toolClassName.Replace("_GetTool", "")}.Get'", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Mcp014BinderController_GetTool", @"{""id"":7,""s"":""red""}", @"{""id"":7,""s"":""red""}", "/api/mcp014-binder/7/red", @"{""id"":7,""s"":null}", false)]
    [InlineData("Mcp014FilterController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/api/mcp014-filter/7/red", @"{""id"":7,""slug"":""red"",""page"":0}", true)]
    [InlineData("Mcp014PlainFilterController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/plain/mcp014-filter/7/red", @"{""id"":7,""slug"":""red"",""page"":0}", true)]
    [InlineData("Mcp014TagsController_GetTool", @"{""id"":7,""tags"":[""a"",""b""]}", @"{""id"":7,""tags"":[]}", "/plain/mcp014-tags/7/a", @"{""id"":7,""tags"":[""a""]}", true)]
    [InlineData("Mcp014KeyController_GetTool", @"{""id"":7,""key"":{""id"":7}}", @"{""id"":7,""keyId"":0}", "/api/mcp014-key/7", @"{""id"":null,""keyId"":7}", true)]
    [InlineData("Mcp014NamedKeyController_GetTool", @"{""key"":{""version"":3}}", @"{""version"":0}", "/api/mcp014-named-key/3", @"{""version"":0}", false)]
    [InlineData("Mcp014NeverSlugController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/plain/mcp014-neverslug/7/red", @"{""id"":7,""slug"":null,""page"":0}", false)]
    [InlineData("Mcp014CustomNeverSlugController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/plain/mcp014-customneverslug/7/red", @"{""id"":7,""slug"":null,""page"":0}", false)]
    [InlineData("Mcp014NeverTypeSlugController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":0}", "/plain/mcp014-nevertypeslug/7/red", @"{""id"":7,""slug"":null,""page"":0}", false)]
    [InlineData("Mcp014OptionalSlugOfNeverTypeController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":0}", "/plain/mcp014-optionalslugofnevertype/7/red", @"{""id"":7,""slug"":""red"",""page"":0}", true)]
    [InlineData("Mcp014ReadOnlySlugController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/plain/mcp014-readonlyslug/7/red", @"{""id"":7,""slug"":null,""page"":0}", false)]
    [InlineData("Mcp014PrivateSetSlugController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/plain/mcp014-privatesetslug/7/red", @"{""id"":7,""slug"":null,""page"":0}", false)]
    [InlineData("Mcp014InitSlugController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/plain/mcp014-initslug/7/red", @"{""id"":7,""slug"":""red"",""page"":0}", true)]
    [InlineData("Mcp014SlugRecordController_GetTool", @"{""id"":7,""f"":{""slug"":""red"",""page"":2}}", @"{""id"":7,""slug"":null,""page"":2}", "/plain/mcp014-slugrecord/7/red", @"{""id"":7,""slug"":""red"",""page"":0}", true)]
    [InlineData("Mcp014ReadOnlyTagsController_GetTool", @"{""id"":7,""f"":{""tags"":[""a""],""page"":2}}", @"{""id"":7,""tags"":[],""page"":2}", "/plain/mcp014-readonlytags/7/a", @"{""id"":7,""tags"":[""a""],""page"":0}", true)]
    public async Task Mcp014_IsReportedExactly_WhereTheToolLosesAValueThatARouteItDoesNotCallCarries(
        string toolClassName, string arguments, string expectedToolEcho, string uncalledRoute, string expectedRouteEcho, bool isReported)
    {
        // What MVC binds through the tool, and at the route the tool does not call, which carries the value where
        // MCP014 says the tool loses it, and does not where MVC binds it under another key or from the query string.
        var result = await _host.CallToolAsync(toolClassName, arguments);
        var toolEcho = _host.LastResponse.Body;
        var (statusCode, routeEcho) = await _host.GetAsync(uncalledRoute);

        Assert.True(result.IsError != true, GetText(result));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedToolEcho), JsonNode.Parse(toolEcho)), $"{toolClassName}: expected {expectedToolEcho}, MVC bound {toolEcho}");
        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedRouteEcho), JsonNode.Parse(routeEcho)), $"{uncalledRoute}: expected {expectedRouteEcho}, MVC bound {routeEcho}");
        Assert.Equal(isReported, _host.GeneratorDiagnostics.Any(x => x.Id == "MCP014" && x.GetMessage().Contains($"'{toolClassName.Replace("_GetTool", "")}.Get'", StringComparison.Ordinal)));
    }

    [Fact]
    public void EveryGeneratedTool_IsBuiltByTheMcpSdk()
    {
        var toolClassNames = _host.ToolClassNames;

        Assert.NotEmpty(toolClassNames);
        Assert.All(toolClassNames, x => _host.GetInputSchema(x));
    }

    private static string GetText(CallToolResult result)
    {
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(x => x.Text));
    }

    /// <summary>
    /// The test controllers with their generated tools, served by MVC on a loopback port the OS picks.
    /// </summary>
    public sealed class MvcHost : IAsyncLifetime
    {
        private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// The culture of the second app, whose request localization defaults to it, and which reads a decimal comma.
        /// </summary>
        public static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");

        /// <summary>
        /// The other culture the second app supports, which reads a decimal point.
        /// </summary>
        public static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

        /// <summary>
        /// A culture the second app supports whose calendar, the Persian one, starts in 622.
        /// </summary>
        public static readonly CultureInfo PersianCulture = CultureInfo.GetCultureInfo("fa-IR");

        private readonly RecordingHandler _serverCultureRecorder = new();
        private readonly RecordingHandler _germanRecorder = new();
        private Assembly _assembly = null!;
        private WebApplication _app = null!;
        private string _address = null!;
        private HttpClient _client = null!;
        private RecordingInvoker _invoker = null!;
        private HttpClient _serverCultureClient = null!;
        private CultureInfo? _serverCulture;
        private WebApplication _germanApp = null!;
        private string _germanAddress = null!;
        private HttpClient _germanClient = null!;

        public async Task InitializeAsync()
        {
            (_assembly, GeneratorDiagnostics) = CompileWithGeneratedTools(SimpleTypeSources.Usings + SimpleTypeSources.Types + Controllers);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddControllers().AddApplicationPart(_assembly);
            _app = builder.Build();
            _app.Use(RunInTheServerCulture);
            _app.MapControllers();
            await _app.StartAsync();

            _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _client = new HttpClient { BaseAddress = new Uri(_address) };
            _invoker = new RecordingInvoker(_client);
            _serverCultureClient = new HttpClient(_serverCultureRecorder);

            var germanBuilder = WebApplication.CreateBuilder();
            germanBuilder.Logging.ClearProviders();
            germanBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            germanBuilder.Services.AddControllers().AddApplicationPart(_assembly);
            _germanApp = germanBuilder.Build();
            _germanApp.UseRequestLocalization(new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture(GermanCulture),
                SupportedCultures = [GermanCulture, EnglishCulture, PersianCulture],
                SupportedUICultures = [GermanCulture, EnglishCulture, PersianCulture]
            });
            _germanApp.MapControllers();
            await _germanApp.StartAsync();

            _germanAddress = _germanApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _germanClient = new HttpClient(_germanRecorder);
        }

        public async Task DisposeAsync()
        {
            _client.Dispose();
            _serverCultureClient.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _germanClient.Dispose();
            await _germanApp.StopAsync();
            await _germanApp.DisposeAsync();
        }

        /// <summary>
        /// Calls a generated tool through <see cref="McpHttpInvoker"/> against the app whose request localization defaults
        /// to <see cref="GermanCulture"/>, from an MCP request that it gave the culture, which the tool call runs under.
        /// </summary>
        public async Task<(int StatusCode, string Body)> InvokeToolAsync(string toolClassName, string arguments, CultureInfo culture)
        {
            await CallToolAsync(toolClassName, arguments, culture);

            return _germanRecorder.LastResponse;
        }

        /// <summary>
        /// Calls a generated tool as <see cref="InvokeToolAsync(string, string, CultureInfo)"/> does, and returns the tool's
        /// result.
        /// </summary>
        public Task<CallToolResult> CallToolAsync(string toolClassName, string arguments, CultureInfo culture)
        {
            var mcpRequest = new DefaultHttpContext();
            mcpRequest.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(new RequestCulture(culture), provider: null));

            return CallToolThroughTheInvokerAsync(toolClassName, arguments, culture, _germanAddress, _germanClient, mcpRequest);
        }

        /// <summary>
        /// Calls a generated tool through <see cref="McpHttpInvoker"/> against the first app, which runs no request
        /// localization, but every request in the server's culture, as a server does in its own: the MCP request and the
        /// tool call too.
        /// </summary>
        public async Task<(int StatusCode, string Body, CallToolResult Result)> CallToolWithoutLocalizationAsync(string toolClassName, string arguments, CultureInfo serverCulture)
        {
            _serverCulture = serverCulture;
            try
            {
                var result = await CallToolThroughTheInvokerAsync(toolClassName, arguments, serverCulture, _address, _serverCultureClient, new DefaultHttpContext());

                return (_serverCultureRecorder.LastResponse.StatusCode, _serverCultureRecorder.LastResponse.Body, result);
            }
            finally
            {
                _serverCulture = null;
            }
        }

        private async Task<CallToolResult> CallToolThroughTheInvokerAsync(
            string toolClassName,
            string arguments,
            CultureInfo culture,
            string address,
            HttpClient client,
            HttpContext mcpRequest)
        {
            var invoker = new McpHttpInvoker(
                client,
                new FixedBaseUrl(address),
                new FixedHttpContextAccessor(mcpRequest),
                NullLogger<McpHttpInvoker>.Instance,
                Options.Create(new McpScopeOptions()));
            var previousCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = culture;
            try
            {
                return await RunToolAsync(toolClassName, arguments, invoker);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private Task RunInTheServerCulture(HttpContext context, RequestDelegate next)
        {
            if (_serverCulture is not null)
            {
                CultureInfo.CurrentCulture = _serverCulture;
                CultureInfo.CurrentUICulture = _serverCulture;
            }

            return next(context);
        }

        /// <summary>
        /// Calls a generated tool with arguments deserialized from JSON, as the MCP server does, and returns what the
        /// action answered.
        /// </summary>
        public Task<(int StatusCode, string Body)> InvokeToolAsync(string toolClassName, string arguments)
        {
            return InvokeToolAsync(toolClassName, arguments, _invoker);
        }

        private async Task<(int StatusCode, string Body)> InvokeToolAsync(string toolClassName, string arguments, RecordingInvoker invoker)
        {
            await RunToolAsync(toolClassName, arguments, invoker);

            return invoker.LastResponse;
        }

        /// <summary>
        /// Runs a generated tool's method, and returns its result, or an empty one from a tool with an output schema,
        /// which returns what the action answered instead.
        /// </summary>
        private async Task<CallToolResult> RunToolAsync(string toolClassName, string arguments, IMcpHttpInvoker invoker)
        {
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            var argumentValues = JsonNode.Parse(arguments)!.AsObject();
            var parameterValues = toolMethod
                .GetParameters()
                .Select(x => x.ParameterType == typeof(IMcpHttpInvoker) ? invoker
                    : x.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                    : argumentValues.TryGetPropertyValue(x.Name!, out var argument) ? argument?.Deserialize(x.ParameterType, ArgumentOptions)
                    : GetDefaultArgument(x))
                .ToArray();

            var run = (Task)toolMethod.Invoke(null, parameterValues)!;
            await run;

            return run.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(run) as CallToolResult ?? new CallToolResult();
        }

        /// <summary>
        /// Calls a generated tool through the MCP SDK, as a client's <c>tools/call</c> reaches it: the SDK reads the
        /// arguments, and throws for a call that leaves out one the tool requires, before the tool runs.
        /// </summary>
        public async Task<CallToolResult> CallToolAsync(string toolClassName, string arguments)
        {
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            await using var services = new ServiceCollection().AddSingleton<IMcpHttpInvoker>(_invoker).BuildServiceProvider();
            var tool = McpServerTool.Create(toolMethod, options: new McpServerToolCreateOptions { Services = services });
            await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions(), serviceProvider: services);
            var context = new RequestContext<CallToolRequestParams>(
                server,
                new JsonRpcRequest { Method = RequestMethods.ToolsCall },
                new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments) });

            return await tool.InvokeAsync(context, CancellationToken.None);
        }

        public int RequestCount => _invoker.RequestCount;

        /// <summary>
        /// Sends a GET request to the app, as a client of the route does, outside any tool.
        /// </summary>
        public async Task<(int StatusCode, string Body)> GetAsync(string path)
        {
            using var response = await _client.GetAsync(path);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public int GermanRequestCount => _germanRecorder.RequestCount;

        public (int StatusCode, string Body) GermanLastResponse => _germanRecorder.LastResponse;

        public (int StatusCode, string Body) LastResponse => _invoker.LastResponse;

        /// <summary>
        /// The diagnostics the generator reported for the controllers.
        /// </summary>
        public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; private set; }

        public IReadOnlyList<string> ToolClassNames => _assembly.GetTypes()
            .Where(x => x.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Select(x => x.Name)
            .ToList();

        /// <summary>
        /// The input schema the MCP SDK gives a generated tool, with the invoker bound from the services, as in the app.
        /// </summary>
        public JsonElement GetInputSchema(string toolClassName)
        {
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            using var services = new ServiceCollection().AddSingleton<IMcpHttpInvoker>(_invoker).BuildServiceProvider();

            return McpServerTool.Create(toolMethod, options: new McpServerToolCreateOptions { Services = services }).ProtocolTool.InputSchema;
        }

        /// <summary>
        /// The value the MCP SDK passes for an argument the call leaves out: the parameter's default, which reflection gives
        /// an enum's as its number.
        /// </summary>
        private static object? GetDefaultArgument(ParameterInfo parameter)
        {
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

            return !parameter.HasDefaultValue ? null
                : parameter.DefaultValue is { } value && type.IsEnum ? Enum.ToObject(type, value)
                : parameter.DefaultValue;
        }

        private static (Assembly Assembly, ImmutableArray<Diagnostic> GeneratorDiagnostics) CompileWithGeneratedTools(string source)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
            var compilation = CSharpCompilation.Create(
                $"ToolsAgainstMvc{Guid.NewGuid():N}",
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var generatorConfig = new InMemoryAdditionalText("Mcp/generator.json", @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [] }");

            CSharpGeneratorDriver
                .Create(new McpToolGenerator())
                .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(generatorConfig))
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

            using var assemblyStream = new MemoryStream();
            var emitResult = outputCompilation.Emit(assemblyStream);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

            // The default context resolves the converters [TypeConverter] names, and MVC finds the controllers there.
            assemblyStream.Position = 0;
            return (AssemblyLoadContext.Default.LoadFromStream(assemblyStream), generatorDiagnostics);
        }
    }

    /// <summary>
    /// Sends what <see cref="McpHttpInvoker"/> sends, and keeps the count and the last answer.
    /// </summary>
    private sealed class RecordingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public int RequestCount { get; private set; }

        public (int StatusCode, string Body) LastResponse { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = await base.SendAsync(request, cancellationToken);
            LastResponse = ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));

            return response;
        }
    }

    private sealed class FixedBaseUrl(string address) : ISelfBaseUrlProvider
    {
        public string GetBaseUrl() => address.TrimEnd('/');
    }

    /// <summary>
    /// The MCP request a tool call runs in.
    /// </summary>
    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => context;
            set => throw new NotSupportedException();
        }
    }

    private sealed class RecordingInvoker(HttpClient client) : IMcpHttpRequestInvoker
    {
        public (int StatusCode, string Body) LastResponse { get; private set; }

        public int RequestCount { get; private set; }

        public Task<CallToolResult> GetAsync(string route, CancellationToken ct = default) => SendAsync(HttpMethod.Get, route, null, ct);

        public Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Post, route, jsonBody, ct);

        public Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Put, route, jsonBody, ct);

        public Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, route, jsonBody, ct);

        public Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, route, null, ct);

        public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<CallToolResult> SendAsync(McpHttpRequest request, CancellationToken ct = default)
        {
            return SendAsync(request.Method, request.Route, request.JsonBody, ct, request.FormBody, request.Headers);
        }

        private async Task<CallToolResult> SendAsync(
            HttpMethod method,
            string route,
            string? jsonBody,
            CancellationToken ct,
            string? formBody = null,
            IReadOnlyList<KeyValuePair<string, string>>? headers = null)
        {
            using var request = new HttpRequestMessage(method, route);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
            }

            if (formBody is not null)
            {
                request.Content = new StringContent(formBody, Encoding.UTF8, MediaTypeNames.Application.FormUrlEncoded);
            }

            foreach (var (name, value) in headers ?? [])
            {
                Assert.True(request.Headers.TryAddWithoutValidation(name, value), name);
            }

            RequestCount++;
            using var response = await client.SendAsync(request, ct);
            LastResponse = ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));

            return new CallToolResult();
        }
    }

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }
}
