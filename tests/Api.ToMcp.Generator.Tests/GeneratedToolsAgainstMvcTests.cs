using System;
using System.Collections.Immutable;
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

        [HttpPost(""file-query"")]
        public object PostFileQuery(FileQuery q) => new { q.City, hasAttachment = q.Attachment is not null };

        [HttpPost(""file-list-query"")]
        public object PostFileListQuery(FileListQuery q) => new { q.City };

        [HttpGet(""nan"")]
        public object GetNan(double value = double.NaN, float inf = float.PositiveInfinity, double negInf = double.NegativeInfinity)
            => new { isNaN = double.IsNaN(value), inf = float.IsPositiveInfinity(inf), negInf = double.IsNegativeInfinity(negInf), query = Request.QueryString.Value };

        // MVC refuses 99, which no member of OrderStatus has, from text, but applies it as the default of a value not sent.
        [HttpGet(""defaults"")]
        public object GetDefaults(OrderStatus unknown = (OrderStatus)99, int page = 2, string text = ""x"")
            => new { unknown, page, text, query = Request.QueryString.Value };

        [HttpGet(""default-route/{status?}"")]
        public object GetDefaultRoute(OrderStatus status = (OrderStatus)99) => new { status, path = Request.Path.Value };

        [HttpPost(""default-body"")]
        public object PostDefaultBody([FromBody] OrderStatus status = (OrderStatus)99) => new { status };

        [HttpGet(""optional/{id?}"")]
        public object GetOptional() => new { id = RouteData.Values[""id""], path = Request.Path.Value };
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
    [InlineData("EchoController_PostFileQueryTool", @"{""q"":{""city"":""Brno""}}", @"{""city"":""Brno"",""hasAttachment"":false}")]
    [InlineData("EchoController_PostFileListQueryTool", @"{""q"":{""city"":""Brno""}}", @"{""city"":""Brno""}")]
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
    public async Task Tool_DeliversItsArguments_AsMvcBindsThem(string toolClassName, string arguments, string expectedEcho)
    {
        var (statusCode, body) = await _host.InvokeToolAsync(toolClassName, arguments);

        Assert.True(statusCode == StatusCodes.Status200OK, $"{toolClassName}: {statusCode} {body}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedEcho), JsonNode.Parse(body)), $"{toolClassName}: expected {expectedEcho}, MVC bound {body}");
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

    /// <summary>
    /// The test controllers with their generated tools, served by MVC on a loopback port the OS picks.
    /// </summary>
    public sealed class MvcHost : IAsyncLifetime
    {
        private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web);

        private Assembly _assembly = null!;
        private WebApplication _app = null!;
        private HttpClient _client = null!;
        private RecordingInvoker _invoker = null!;

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
        }

        public async Task DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        /// <summary>
        /// Calls a generated tool with arguments deserialized from JSON, as the MCP server does, and returns what the
        /// action answered.
        /// </summary>
        public async Task<(int StatusCode, string Body)> InvokeToolAsync(string toolClassName, string arguments)
        {
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            var argumentValues = JsonNode.Parse(arguments)!.AsObject();
            var parameterValues = toolMethod
                .GetParameters()
                .Select(x => x.ParameterType == typeof(IMcpHttpInvoker) ? _invoker
                    : x.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                    : argumentValues.TryGetPropertyValue(x.Name!, out var argument) ? argument?.Deserialize(x.ParameterType, ArgumentOptions)
                    : GetDefaultArgument(x))
                .ToArray();

            await (Task)toolMethod.Invoke(null, parameterValues)!;

            return _invoker.LastResponse;
        }

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

    private sealed class RecordingInvoker(HttpClient client) : IMcpHttpInvoker
    {
        public (int StatusCode, string Body) LastResponse { get; private set; }

        public Task<CallToolResult> GetAsync(string route, CancellationToken ct = default) => SendAsync(HttpMethod.Get, route, null, ct);

        public Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Post, route, jsonBody, ct);

        public Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Put, route, jsonBody, ct);

        public Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, route, jsonBody, ct);

        public Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, route, null, ct);

        public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default) => Task.CompletedTask;

        private async Task<CallToolResult> SendAsync(HttpMethod method, string route, string? jsonBody, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, route);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
            }

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
