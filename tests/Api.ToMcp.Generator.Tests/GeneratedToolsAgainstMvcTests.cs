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

        // The AI never sets these headers, so the tool takes no argument for them and sends the request's own.
        [HttpGet(""own-headers"")]
        public object GetOwnHeaders([FromHeader(Name = ""Authorization"")] string? auth, [FromHeader(Name = ""X-MS-CLIENT-PRINCIPAL-ID"")] string? userId, [FromHeader(Name = ""X-Forwarded-For"")] string? clientIp)
            => new { auth, userId, clientIp };

        [HttpPost(""amounts"")]
        public object PostAmounts([FromForm] decimal amount, [FromForm] double ratio, [FromForm] DateOnly day, [FromForm] int delta, [FromForm] decimal[]? amounts, [FromHeader(Name = ""X-Amount"")] decimal? headerAmount, [FromQuery] decimal? queryAmount)
            => new { amount, ratio, day, delta, amounts, headerAmount, queryAmount };

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

        private Assembly _assembly = null!;
        private WebApplication _app = null!;
        private HttpClient _client = null!;
        private RecordingInvoker _invoker = null!;
        private WebApplication _germanApp = null!;
        private HttpClient _germanClient = null!;
        private RecordingInvoker _germanInvoker = null!;

        public async Task InitializeAsync()
        {
            _assembly = CompileWithGeneratedTools(SimpleTypeSources.Usings + SimpleTypeSources.Types + Controllers);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddControllers().AddApplicationPart(_assembly);
            _app = builder.Build();
            _app.MapControllers();
            await _app.StartAsync();

            var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _client = new HttpClient { BaseAddress = new Uri(address) };
            _invoker = new RecordingInvoker(_client);

            var germanBuilder = WebApplication.CreateBuilder();
            germanBuilder.Logging.ClearProviders();
            germanBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            germanBuilder.Services.AddControllers().AddApplicationPart(_assembly);
            _germanApp = germanBuilder.Build();
            _germanApp.UseRequestLocalization(new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture(GermanCulture),
                SupportedCultures = [GermanCulture],
                SupportedUICultures = [GermanCulture]
            });
            _germanApp.MapControllers();
            await _germanApp.StartAsync();

            var germanAddress = _germanApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _germanClient = new HttpClient { BaseAddress = new Uri(germanAddress) };
            _germanInvoker = new RecordingInvoker(_germanClient);
        }

        public async Task DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _germanClient.Dispose();
            await _germanApp.StopAsync();
            await _germanApp.DisposeAsync();
        }

        /// <summary>
        /// Calls a generated tool as <see cref="InvokeToolAsync(string, string)"/> does, under the culture, against the app
        /// whose request localization defaults to <see cref="GermanCulture"/>.
        /// </summary>
        public async Task<(int StatusCode, string Body)> InvokeToolAsync(string toolClassName, string arguments, CultureInfo culture)
        {
            var previousCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = culture;
            try
            {
                return await InvokeToolAsync(toolClassName, arguments, _germanInvoker);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
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
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            var argumentValues = JsonNode.Parse(arguments)!.AsObject();
            var parameterValues = toolMethod
                .GetParameters()
                .Select(x => x.ParameterType == typeof(IMcpHttpInvoker) ? invoker
                    : x.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                    : argumentValues.TryGetPropertyValue(x.Name!, out var argument) ? argument?.Deserialize(x.ParameterType, ArgumentOptions)
                    : GetDefaultArgument(x))
                .ToArray();

            await (Task)toolMethod.Invoke(null, parameterValues)!;

            return invoker.LastResponse;
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

        public (int StatusCode, string Body) LastResponse => _invoker.LastResponse;

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

        private static Assembly CompileWithGeneratedTools(string source)
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
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            using var assemblyStream = new MemoryStream();
            var emitResult = outputCompilation.Emit(assemblyStream);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

            // The default context resolves the converters [TypeConverter] names, and MVC finds the controllers there.
            assemblyStream.Position = 0;
            return AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
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
