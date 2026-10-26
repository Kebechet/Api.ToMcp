using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// MVC reads an action's route and HTTP methods from any attribute implementing <c>IRouteTemplateProvider</c> and
/// <c>IActionHttpMethodProvider</c>, so from a class derived from <c>[Route]</c> or an <c>[Http*]</c> attribute as well as
/// from MVC's own. See https://github.com/Kebechet/Api.ToMcp/issues/46.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string RouteAttributeUsings = SimpleTypeSources.Usings + @"using Microsoft.AspNetCore.Mvc.Routing;
";

    private const string CustomRouteAttributes = @"
namespace TestApi
{
    public sealed class ApiRouteAttribute : RouteAttribute
    {
        public ApiRouteAttribute() : base(""api/[controller]"")
        {
        }
    }

    public sealed class VersionedRouteAttribute : RouteAttribute
    {
        public VersionedRouteAttribute(string template) : base(""api/v1/"" + template)
        {
        }
    }

    public class SectionRouteAttribute(string section) : RouteAttribute($""sections/{section}"");

    public sealed class NumberedRouteAttribute : RouteAttribute
    {
        public NumberedRouteAttribute(int version) : base(""api/v"" + version + ""/[controller]"")
        {
        }
    }

    public sealed class InterpolatedRouteAttribute : RouteAttribute
    {
        public InterpolatedRouteAttribute(long version, char separator) : base($""api/v{version}{separator}{true}/[controller]"")
        {
        }
    }

    public sealed class PaddedRouteAttribute : RouteAttribute
    {
        public PaddedRouteAttribute(int version) : base(""api/v"" + version.ToString(""D2"") + ""/[controller]"")
        {
        }
    }

    public enum ApiVersion
    {
        V1 = 1,
        V2 = 2
    }

    [Flags]
    public enum ApiArea
    {
        None = 0,
        Admin = 1,
        Public = 2
    }

    public enum Stage
    {
        Beta = 1,
        Preview = 1
    }

    public sealed class EnumRouteAttribute : RouteAttribute
    {
        public EnumRouteAttribute(ApiVersion version) : base($""api/{version}/[controller]"")
        {
        }
    }

    public sealed class ObjectRouteAttribute : RouteAttribute
    {
        public ObjectRouteAttribute(object version) : base(""api/"" + version + ""/[controller]"")
        {
        }
    }

    public sealed class ConstantEnumRouteAttribute : RouteAttribute
    {
        public ConstantEnumRouteAttribute() : base(""api/"" + ApiVersion.V2 + ""/[controller]"")
        {
        }
    }

    public sealed class AreaRouteAttribute : RouteAttribute
    {
        public AreaRouteAttribute(ApiArea area) : base($""{area}/[controller]"")
        {
        }
    }

    public sealed class StageRouteAttribute : RouteAttribute
    {
        public StageRouteAttribute(Stage stage) : base($""{stage}/[controller]"")
        {
        }
    }

    public sealed class WidenedRouteAttribute : RouteAttribute
    {
        public WidenedRouteAttribute(int version) : base(""api/v"" + (long)version + ""/[controller]"")
        {
        }
    }

    public sealed class LetterCodeRouteAttribute : RouteAttribute
    {
        public LetterCodeRouteAttribute(char letter) : base(""api/"" + (int)letter + ""/[controller]"")
        {
        }
    }

    public sealed class NarrowedRouteAttribute : RouteAttribute
    {
        public NarrowedRouteAttribute(int version) : base(""api/v"" + (byte)version + ""/[controller]"")
        {
        }
    }

    public sealed class LetterRouteAttribute : RouteAttribute
    {
        public LetterRouteAttribute(int code) : base(""api/"" + (char)code + ""/[controller]"")
        {
        }
    }

    public sealed class NullableEnumRouteAttribute : RouteAttribute
    {
        public NullableEnumRouteAttribute(ApiVersion version) : base($""n/{(ApiVersion?)version}/[controller]"")
        {
        }
    }

    public sealed class NullableNumberRouteAttribute : RouteAttribute
    {
        public NullableNumberRouteAttribute(int version) : base(""api/v"" + (long?)version + ""/[controller]"")
        {
        }
    }

    public sealed class EnumNumberRouteAttribute : RouteAttribute
    {
        public EnumNumberRouteAttribute(ApiVersion version) : base(""l/"" + (long)version + ""/[controller]"")
        {
        }
    }

    public sealed class NullableEnumNumberRouteAttribute : RouteAttribute
    {
        public NullableEnumNumberRouteAttribute(ApiVersion version) : base($""l/{(int?)version}/[controller]"")
        {
        }
    }

    public sealed class NarrowedNullableRouteAttribute : RouteAttribute
    {
        public NarrowedNullableRouteAttribute(int version) : base(""api/v"" + (byte?)version + ""/[controller]"")
        {
        }
    }

    public sealed class NarrowedEnumRouteAttribute : RouteAttribute
    {
        public NarrowedEnumRouteAttribute(ApiVersion version) : base(""api/v"" + (short)version + ""/[controller]"")
        {
        }
    }

    public sealed class UnsignedEnumRouteAttribute : RouteAttribute
    {
        public UnsignedEnumRouteAttribute(ApiVersion version) : base(""api/v"" + (uint)version + ""/[controller]"")
        {
        }
    }

    public sealed class RatioRouteAttribute : RouteAttribute
    {
        public RatioRouteAttribute(double ratio) : base($""api/r{ratio}/[controller]"")
        {
        }
    }

    public sealed class GetByIdAttribute : HttpGetAttribute
    {
        public GetByIdAttribute() : base(""{id}"")
        {
        }
    }

    public sealed class FetchAttribute : HttpGetAttribute
    {
        public FetchAttribute(string template) : this(template, ""fetch"")
        {
        }

        private FetchAttribute(string template, string name) : base(template)
        {
            Name = name;
        }
    }

    public sealed class ChangeAttribute : HttpPutAttribute
    {
    }

    public sealed class ReadAttribute : HttpMethodAttribute
    {
        public ReadAttribute(string template) : base(new[] { ""GET"" }, template)
        {
        }
    }

    public sealed class WrappedDocsRouteAttribute : Attribute, IRouteTemplateProvider
    {
        public string Template => (""wrapped/[controller]"")!;

        public int? Order => null;

        public string? Name => null;
    }

    public sealed class DocsRouteAttribute : Attribute, IRouteTemplateProvider
    {
        public string Template => ""docs/[controller]"";

        public int? Order => null;

        public string? Name => null;
    }

    // MVC reads the template after the application's named arguments have set it.
    public sealed class CustomGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public string? Template { get; set; } = ""fallback"";

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class AssignedGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public AssignedGetAttribute(string template)
        {
            Template = template;
        }

        public string? Template { get; } = ""fallback"";

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class GetOnlyAttribute : Attribute, IActionHttpMethodProvider
    {
        public IEnumerable<string> HttpMethods { get; } = new[] { ""GET"" };
    }

    public sealed class StoredGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _template = ""fallback"";

        public StoredGetAttribute()
        {
            _template = ""constructed"";
        }

        public string? Template
        {
            get { return _template; }
            set { this._template = value; }
        }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class BangGetterAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _template;

        public string? Template { get => _template!; set => _template = value!; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class ParenthesizedGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _template;

        public string? Template { get { return (_template); } set { _template = (value); } }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class TransformedGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _template;

        public string? Template { get => _template; set => _template = ""v1/"" + value; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public class VirtualGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public virtual string? Template { get; set; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class PrefixedGetAttribute : VirtualGetAttribute
    {
        public override string? Template { get => base.Template; set => base.Template = ""v1/"" + value; }
    }

    public sealed class OverridingGetAttribute : VirtualGetAttribute
    {
        public override string? Template => ""overridden"";
    }

    public sealed class SideSetGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _template;

        public string? Template { get => _template; set => _template = value; }

        public string? Area { get => null; set => _template = value + ""/"" + _template; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    // The constructor sets the template through the virtual property, so through an override's setter, after the
    // override's initializer has run.
    public class BaseCtorGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public BaseCtorGetAttribute()
        {
            Template = ""from-base-ctor"";
        }

        public virtual string? Template { get; set; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class DerivedInitGetAttribute : BaseCtorGetAttribute
    {
        public override string? Template { get; set; } = ""init"";
    }

    // Code sets a property or field in other forms than a plain assignment as well.
    // A constructor runs after the initializers, so one that hands the attribute to other code may change the template.
    public static class LocalRoutes
    {
        public static void Prefix(CtorHelperGetAttribute attribute, string? area) => attribute.Template = area + ""/"" + attribute.Template;

        public static void Mark(object attribute)
        {
        }
    }

    public sealed class CtorHelperGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public CtorHelperGetAttribute()
        {
            LocalRoutes.Prefix(this, ""admin"");
        }

        public string? Template { get; set; } = ""init"";

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public abstract class MarkingBaseAttribute : Attribute
    {
        protected MarkingBaseAttribute()
        {
            LocalRoutes.Mark(this);
        }
    }

    public sealed class MarkedGetAttribute : MarkingBaseAttribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public string? Template { get; set; } = ""init"";

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class CtorValuesGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private readonly string _area;

        public CtorValuesGetAttribute()
        {
            _area = ""admin"" + ""/"" + 1;
            Methods = new[] { ""GET"" };
            Weight = _area == null ? 1 : 2;
        }

        public string? Template { get; set; } = ""init"";

        public IEnumerable<string> Methods { get; }

        public int Weight { get; set; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class TupleCtorGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public TupleCtorGetAttribute()
        {
            (Template, Extra) = (""from-tuple"", 1);
        }

        public string? Template { get; set; } = ""init"";

        public int Extra { get; set; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    // Setters of another named argument that do more than store the value: the generator follows none of them.
    public sealed class CollectionAreaGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private List<string> _tags = new List<string>();

        public string? Template { get; set; }

        public string? Area { get => null; set => _tags = new List<string> { value ?? """" }; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class PatternAreaGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _area;

        public string? Template { get; set; }

        public string? Area { get => _area; set => _area = value is { Length: > 0 } ? value : null; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class ObjectInitAreaGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private Options? _options;

        public string? Template { get; set; }

        public string? Area { get => null; set => _options = new Options { Label = value }; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };

        private sealed class Options
        {
            public string? Label { get; set; }
        }
    }

    public sealed class StoringAreaGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _area;

        public string? Template { get; set; }

        public string? Area { get => _area; set => _area = value; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    // Only the attribute's own constructors run before the named arguments: not a nested class's, nor a lambda one
    // creates, which other code may run later.
    public sealed class NestedPatchGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _template;

        public string? Template { get => _template; set => _template = value; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };

        public void Patch(string? area) => new Patcher(this, area);

        private sealed class Patcher
        {
            public Patcher(NestedPatchGetAttribute attribute, string? area)
            {
                attribute._template = area + ""/"" + attribute._template;
            }
        }
    }

    public sealed class LambdaCtorGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private readonly Action<string?> _prefix;
        private string? _template;

        public LambdaCtorGetAttribute()
        {
            _prefix = area => _template = area + ""/"" + _template;
        }

        public string? Template { get => _template; set => _template = value; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };

        public void Prefix(string? area) => _prefix(area);
    }

    public sealed class RefSideGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        private string? _template;

        public string? Template { get => _template; set => _template = value; }

        public string? Area { get => null; set => Prefix(value, ref _template); }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };

        private static void Prefix(string? area, ref string? template) => template = area + ""/"" + template;
    }

    public class InitBaseGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public virtual string? Template { get; set; } = ""base"";

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    // A named argument sets the property that hides the one MVC reads through the interface.
    public sealed class NewHidingGetAttribute : InitBaseGetAttribute
    {
        public new string? Template { get; set; } = ""init"";
    }

    public sealed class NewReimplementingGetAttribute : InitBaseGetAttribute, IRouteTemplateProvider
    {
        public new string? Template { get; set; } = ""init"";
    }

    // A getter computed from constants alone returns its value whatever sets the property.
    public sealed class ConstantOverrideGetAttribute : BaseCtorGetAttribute
    {
        public override string? Template => ""constant"";
    }

    public class SideBaseGetAttribute : Attribute, IRouteTemplateProvider, IActionHttpMethodProvider
    {
        public virtual string? Template { get; set; }

        public string? Area { get => null; set => Template = value + ""/"" + Template; }

        public int? Order => null;

        public string? Name => null;

        public IEnumerable<string> HttpMethods => new[] { ""GET"" };
    }

    public sealed class SideDerivedGetAttribute : SideBaseGetAttribute
    {
        public override string? Template { get; set; }
    }
}
";

    [Theory]
    [InlineData("[GetById] public IActionResult Find(int id) => Ok();", "GET", "/api/products/{routeid}")]
    [InlineData("[Fetch(\"lookup/{code}\")] public IActionResult Find(string code) => Ok();", "GET", "/api/products/lookup/{routecode}")]
    [InlineData("[Change] public IActionResult Find() => Ok();", "PUT", "/api/products")]
    [InlineData("[Read(\"read\")] public IActionResult Find() => Ok();", "GET", "/api/products/read")]
    [InlineData("[GetOnly] [Route(\"only\")] public IActionResult Find() => Ok();", "GET", "/api/products/only")]
    [InlineData("[AcceptVerbs(\"get\", \"head\", Route = \"verbs\")] public IActionResult Find() => Ok();", "GET", "/api/products/verbs")]
    [InlineData("[AcceptVerbs(\"HEAD\", \"PUT\", \"POST\")] public IActionResult Find() => Ok();", "PUT", "/api/products")]
    [InlineData("[HttpHead(\"head\")] [HttpGet(\"get\")] public IActionResult Find() => Ok();", "GET", "/api/products/get")]
    [InlineData("[HttpPost] [HttpGet(\"get\")] public IActionResult Find() => Ok();", "GET", "/api/products/get")]
    [InlineData("[HttpGet] [Route(\"search\")] public IActionResult Find() => Ok();", "GET", "/api/products/search")]
    [InlineData("[CustomGet(Template = \"actual\")] public IActionResult Find() => Ok();", "GET", "/api/products/actual")]
    [InlineData("[CustomGet] public IActionResult Find() => Ok();", "GET", "/api/products/fallback")]
    [InlineData("[StoredGet(Template = \"stored\")] public IActionResult Find() => Ok();", "GET", "/api/products/stored")]
    [InlineData("[VirtualGet(Template = \"stored\")] public IActionResult Find() => Ok();", "GET", "/api/products/stored")]
    [InlineData("[BangGetter(Template = \"stored\")] public IActionResult Find() => Ok();", "GET", "/api/products/stored")]
    [InlineData("[ParenthesizedGet(Template = \"stored\")] public IActionResult Find() => Ok();", "GET", "/api/products/stored")]
    [InlineData("[OverridingGet] public IActionResult Find() => Ok();", "GET", "/api/products/overridden")]
    [InlineData("[ConstantOverrideGet] public IActionResult Find() => Ok();", "GET", "/api/products/constant")]
    [InlineData("[StoringAreaGet(Template = \"items\", Area = \"admin\")] public IActionResult Find() => Ok();", "GET", "/api/products/items")]
    [InlineData("[CtorValuesGet] public IActionResult Find() => Ok();", "GET", "/api/products/init")]
    [InlineData("[NewHidingGet(Template = \"hidden\")] public IActionResult Find() => Ok();", "GET", "/api/products/base")]
    [InlineData("[NewHidingGet] public IActionResult Find() => Ok();", "GET", "/api/products/base")]
    [InlineData("[NewReimplementingGet(Template = \"own\")] public IActionResult Find() => Ok();", "GET", "/api/products/own")]
    [InlineData("[HttpGet(Name = \"find\")] [Route(\"{id}\")] public IActionResult Find(int id) => Ok();", "GET", "/api/products/{routeid}")]
    [InlineData("[HttpGet] [HttpGet(\"{id}\")] public IActionResult Find(int id) => Ok();", "GET", "/api/products/{routeid}")]
    public void Generator_ReadsTheRouteAndHttpMethod_FromAnyAttributeMvcReadsThemFrom(string action, string expectedMethod, string expectedRoute)
    {
        var tool = GenerateCompilingRouteAttributeTool("[ApiController] [ApiRoute] public class ProductsController : ControllerBase", action, "ProductsController_FindTool");

        Assert.Equal($"var route = $\"{expectedRoute}\";", GetRouteLine(tool, "ProductsController_FindTool"));
        Assert.Contains($"await invoker.{expectedMethod[0]}{expectedMethod.Substring(1).ToLowerInvariant()}Async(route, ", tool);
    }

    [Theory]
    [InlineData("[VersionedRoute(\"orders\")] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/v1/orders")]
    [InlineData("[SectionRoute(\"orders\")] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/sections/orders")]
    [InlineData("[DocsRoute] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/docs/orders")]
    [InlineData("[WrappedDocsRoute] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/wrapped/orders")]
    [InlineData("[ApiRoute] public abstract class ApiControllerBase : ControllerBase { } public class OrdersController : ApiControllerBase", "OrdersController_FindTool", "/api/orders")]
    [InlineData("[NumberedRoute(2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/v2/orders")]
    [InlineData("[InterpolatedRoute(5000000000, '-')] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/v5000000000-True/orders")]
    [InlineData("[EnumRoute(ApiVersion.V2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/V2/orders")]
    [InlineData("[ObjectRoute(ApiVersion.V2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/V2/orders")]
    [InlineData("[ConstantEnumRoute] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/V2/orders")]
    [InlineData("[AreaRoute(ApiArea.Admin)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/Admin/orders")]
    [InlineData("[WidenedRoute(2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/v2/orders")]
    [InlineData("[LetterCodeRoute('A')] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/65/orders")]
    [InlineData("[NullableEnumRoute(ApiVersion.V2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/n/V2/orders")]
    [InlineData("[NullableNumberRoute(2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/api/v2/orders")]
    [InlineData("[EnumNumberRoute(ApiVersion.V2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/l/2/orders")]
    [InlineData("[NullableEnumNumberRoute(ApiVersion.V2)] public class OrdersController : ControllerBase", "OrdersController_FindTool", "/l/2/orders")]
    public void Generator_ReadsAControllersRoute_FromAnyAttributeMvcReadsItFrom(string controllerDeclaration, string toolClassName, string expectedRoute)
    {
        var tool = GenerateCompilingRouteAttributeTool(controllerDeclaration, "[HttpGet] public IActionResult Find() => Ok();", toolClassName);

        Assert.Equal($"var route = $\"{expectedRoute}\";", GetRouteLine(tool, toolClassName));
    }

    [Theory]
    [InlineData("[HttpHead] public IActionResult Find() => Ok();")]
    [InlineData("[HttpOptions(\"options\")] public IActionResult Find() => Ok();")]
    [InlineData("[AcceptVerbs(\"HEAD\", \"TRACE\")] public IActionResult Find() => Ok();")]
    [InlineData("[Route(\"any\")] public IActionResult Find() => Ok();")]
    public void Generator_GivesNoToolToAnAction_ServedOnlyAtHttpMethodsAToolCannotSend(string action)
    {
        // The invoker sends GET, POST, PUT, PATCH and DELETE; MVC serves an action with a [Route] alone for every method.
        var source = RouteAttributeUsings + CustomRouteAttributes + @"
namespace TestApi
{
    [ApiController]
    [ApiRoute]
    public class ProductsController : ControllerBase
    {
        " + action + @"
    }
}
";
        Assert.DoesNotContain("ProductsController_FindTool", RunGenerator(source, AllExceptExcludedConfig));
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);
    }

    [Theory]
    [InlineData("[PaddedRoute(2)]", "the generator cannot read the route template of the [PaddedRoute] attribute on OrdersController, as it is computed by 'version.ToString(\"D2\")', which the generator cannot evaluate")]
    [InlineData("[RatioRoute(1.5)]", "the generator cannot read the route template of the [RatioRoute] attribute on OrdersController, as it is computed from 'ratio', a double, which C# writes in the culture the app runs under")]
    [InlineData("[NumberedRoute(-1)]", "the generator cannot read the route template of the [NumberedRoute] attribute on OrdersController, as it is computed from 'version', a negative number, which C# writes with the minus sign of the culture the app runs under")]
    [InlineData("[ApiRoute(42)]", "the generator cannot read the route template of the [ApiRoute] attribute on OrdersController, as an argument of the attribute does not compile")]
    [InlineData("[NarrowedRoute(300)]", "the generator cannot read the route template of the [NarrowedRoute] attribute on OrdersController, as it is computed by '(byte)version', which the generator cannot evaluate")]
    [InlineData("[LetterRoute(65)]", "the generator cannot read the route template of the [LetterRoute] attribute on OrdersController, as it is computed by '(char)code', which the generator cannot evaluate")]
    [InlineData("[NarrowedNullableRoute(300)]", "the generator cannot read the route template of the [NarrowedNullableRoute] attribute on OrdersController, as it is computed by '(byte?)version', which the generator cannot evaluate")]
    [InlineData("[NarrowedEnumRoute(ApiVersion.V2)]", "the generator cannot read the route template of the [NarrowedEnumRoute] attribute on OrdersController, as it is computed by '(short)version', which the generator cannot evaluate")]
    [InlineData("[UnsignedEnumRoute(ApiVersion.V2)]", "the generator cannot read the route template of the [UnsignedEnumRoute] attribute on OrdersController, as it is computed by '(uint)version', which the generator cannot evaluate")]
    [InlineData("[EnumRoute((ApiVersion)7)]", "the generator cannot read the route template of the [EnumRoute] attribute on OrdersController, as it is computed from 'version', a value of ApiVersion that no member has alone")]
    [InlineData("[AreaRoute(ApiArea.Admin | ApiArea.Public)]", "the generator cannot read the route template of the [AreaRoute] attribute on OrdersController, as it is computed from 'area', a value of ApiArea that no member has alone")]
    [InlineData("[StageRoute(Stage.Beta)]", "the generator cannot read the route template of the [StageRoute] attribute on OrdersController, as it is computed from 'stage', a value of Stage that no member has alone")]
    public void Generator_SaysWhyItCannotReadTheTemplateOfARouteAttributeInTheProject(string attribute, string reason)
    {
        // MVC builds the template when the app starts, in the app's culture, so the generator writes a number only where
        // every culture writes it alike.
        var source = RouteAttributeUsings + CustomRouteAttributes + @"
namespace TestApi
{
    " + attribute + @"
    public class OrdersController : ControllerBase
    {
        [HttpGet]
        public IActionResult Find() => Ok();
    }
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);

        Assert.Equal($"No MCP tool is generated for action 'OrdersController.Find': {reason}", warning.GetMessage());
    }

    [Theory]
    [InlineData("[AssignedGet(\"actual\")]", "the generator cannot read the route template of its [AssignedGet] attribute, as AssignedGetAttribute.Template is set by code other than its initializer, which the generator does not follow")]
    [InlineData("[DerivedInitGet]", "the generator cannot read the route template of its [DerivedInitGet] attribute, as DerivedInitGetAttribute.Template is set by code other than its initializer, which the generator does not follow")]
    [InlineData("[TupleCtorGet]", "the generator cannot read the route template of its [TupleCtorGet] attribute, as TupleCtorGetAttribute.Template is set by code other than its initializer, which the generator does not follow")]
    [InlineData("[CtorHelperGet]", "the generator cannot read the route template of its [CtorHelperGet] attribute, as the constructor of CtorHelperGetAttribute does more than assign values to the attribute's fields and auto-properties, which the generator does not follow")]
    [InlineData("[MarkedGet]", "the generator cannot read the route template of its [MarkedGet] attribute, as the constructor of MarkingBaseAttribute does more than assign values to the attribute's fields and auto-properties, which the generator does not follow")]
    public void Generator_SaysWhyItCannotReadTheTemplateOfARouteAttribute_ThatCodeOtherThanItsInitializerSets(string attribute, string reason)
    {
        // A constructor overwrites the initializer, so the initializer is not the template MVC reads; a base class's
        // constructor does so through the property it declares, which the attribute's overrides.
        var source = RouteAttributeUsings + CustomRouteAttributes + @"
namespace TestApi
{
    [ApiController]
    [ApiRoute]
    public class ProductsController : ControllerBase
    {
        " + attribute + @"
        public IActionResult Find() => Ok();
    }
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);

        Assert.Equal($"No MCP tool is generated for action 'ProductsController.Find': {reason}", warning.GetMessage());
    }

    [Theory]
    [InlineData("[TransformedGet(Template = \"items\")]", "the generator cannot read the route template of its [TransformedGet] attribute, as the setter of TransformedGetAttribute.Template does more than store the value for its getter to return, which the generator does not follow")]
    [InlineData("[PrefixedGet(Template = \"items\")]", "the generator cannot read the route template of its [PrefixedGet] attribute, as the setter of PrefixedGetAttribute.Template does more than store the value for its getter to return, which the generator does not follow")]
    [InlineData("[SideSetGet(Template = \"items\", Area = \"admin\")]", "the generator cannot read the route template of its [SideSetGet] attribute, as the value SideSetGetAttribute.Template returns is set by code other than its setter and the constructors, which the generator does not follow")]
    [InlineData("[SideDerivedGet(Template = \"items\", Area = \"admin\")]", "the generator cannot read the route template of its [SideDerivedGet] attribute, as the value SideDerivedGetAttribute.Template returns is set by code other than its setter and the constructors, which the generator does not follow")]
    [InlineData("[RefSideGet(Template = \"items\", Area = \"admin\")]", "the generator cannot read the route template of its [RefSideGet] attribute, as the value RefSideGetAttribute.Template returns is set by code other than its setter and the constructors, which the generator does not follow")]
    [InlineData("[NestedPatchGet(Template = \"items\")]", "the generator cannot read the route template of its [NestedPatchGet] attribute, as the value NestedPatchGetAttribute.Template returns is set by code other than its setter and the constructors, which the generator does not follow")]
    [InlineData("[LambdaCtorGet(Template = \"items\")]", "the generator cannot read the route template of its [LambdaCtorGet] attribute, as the value LambdaCtorGetAttribute.Template returns is set by code other than its setter and the constructors, which the generator does not follow")]
    [InlineData("[CollectionAreaGet(Template = \"items\", Area = \"admin\")]", "the generator cannot read the route template of its [CollectionAreaGet] attribute, as the setter of CollectionAreaGetAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[PatternAreaGet(Template = \"items\", Area = \"admin\")]", "the generator cannot read the route template of its [PatternAreaGet] attribute, as the setter of PatternAreaGetAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[ObjectInitAreaGet(Template = \"items\", Area = \"admin\")]", "the generator cannot read the route template of its [ObjectInitAreaGet] attribute, as the setter of ObjectInitAreaGetAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    public void Generator_SaysWhyItCannotReadTheTemplateANamedArgumentSets_WhenThePropertyMayNotKeepIt(string attribute, string reason)
    {
        // MVC serves the template the getter returns, which a setter that adds to its value, say, makes /v1/items.
        var source = RouteAttributeUsings + CustomRouteAttributes + @"
namespace TestApi
{
    [ApiController]
    [ApiRoute]
    public class ProductsController : ControllerBase
    {
        " + attribute + @"
        public IActionResult Find() => Ok();
    }
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);

        Assert.Equal($"No MCP tool is generated for action 'ProductsController.Find': {reason}", warning.GetMessage());
    }

    private const string ReferencedRouteAttributes = @"
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;

namespace Shared
{
    public sealed class SharedRouteAttribute : RouteAttribute
    {
        public SharedRouteAttribute() : base(""shared/[controller]"")
        {
        }
    }

    public sealed class SharedGetAttribute : HttpGetAttribute
    {
        public SharedGetAttribute(string template) : base(template)
        {
        }
    }

    public sealed class SharedCustomRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        public string? Template { get; set; }

        public int? Order => null;

        public string? Name => null;
    }

    public sealed class SharedTransformedRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        private string? _template;

        public string? Template { get => _template; set => _template = ""v1/"" + value; }

        public int? Order => null;

        public string? Name => null;
    }

    public sealed class SharedSideSetRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        public string? Template { get; set; }

        public string? Area { get => null; set => Template = value + ""/"" + Template; }

        public int Weight { get; set; }

        public int? Order => null;

        public string? Name => null;
    }

    public class SharedSideVirtualRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        public virtual string? Template { get; set; }

        public virtual string? Area { get => null; set => Template = value + ""/"" + Template; }

        public int? Order => null;

        public string? Name => null;
    }

    public static class SharedRoutes
    {
        public static void Prefix(object attribute, string? area)
        {
            var template = attribute.GetType().GetProperty(""Template"")!;
            template.SetValue(attribute, area + ""/"" + template.GetValue(attribute));
        }
    }

    public abstract class SharedPlainBaseAttribute : System.Attribute
    {
    }

    public class SharedAutoRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        public string? Template { get; set; }

        public int? Order => null;

        public string? Name => null;
    }

    public class SharedBaseCtorRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        public SharedBaseCtorRouteAttribute()
        {
            Template = ""from-base-ctor"";
        }

        public virtual string? Template { get; set; }

        public int? Order => null;

        public string? Name => null;
    }
}
";

    private const string AttributesDerivedFromReferencedOnes = @"
namespace TestApi
{
    public sealed class LocalInitOverrideRouteAttribute : Shared.SharedBaseCtorRouteAttribute
    {
        public override string? Template { get; set; } = ""init"";
    }

    public sealed class LocalAutoOverrideRouteAttribute : Shared.SharedBaseCtorRouteAttribute
    {
        public override string? Template { get; set; }
    }

    public sealed class LocalConstantOverrideRouteAttribute : Shared.SharedBaseCtorRouteAttribute
    {
        public override string? Template => ""items"";
    }

    public sealed class LocalSideOverSharedAutoRouteAttribute : Shared.SharedAutoRouteAttribute
    {
        public string? Area { get => null; set => Template = value + ""/"" + Template; }
    }

    public sealed class LocalPlainOverSharedAutoRouteAttribute : Shared.SharedAutoRouteAttribute
    {
    }

    // The setter of the project's Area runs the referenced one, which sets Template.
    public sealed class LocalForwardingAreaRouteAttribute : Shared.SharedSideVirtualRouteAttribute
    {
        public override string? Area { get => base.Area; set => base.Area = value; }
    }

    public sealed class LocalOwnAreaRouteAttribute : Shared.SharedSideVirtualRouteAttribute
    {
        public override string? Area { get; set; }
    }

    public sealed class LocalHelperAreaRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        public string? Template { get; set; } = ""init"";

        public string? Area { get => null; set => Shared.SharedRoutes.Prefix(this, value); }

        public int? Order => null;

        public string? Name => null;
    }

    public sealed class LocalTrimmingZoneRouteAttribute : Shared.SharedAutoRouteAttribute
    {
        private string? _zone;

        public string? Zone { get => _zone; set => _zone = value?.Trim(); }
    }

    public sealed class LocalStoringZoneRouteAttribute : Shared.SharedAutoRouteAttribute
    {
        private string? _zone;

        public string? Zone { get => _zone; set => _zone = value; }
    }

    // The attribute reaches referenced code that sets Template by ways other than this passed on directly.
    public sealed class LocalSelfVarAreaRouteAttribute : Shared.SharedAutoRouteAttribute
    {
        public string? Area { get => null; set { var self = this; Shared.SharedRoutes.Prefix(self, value); } }
    }

    public sealed class LocalIndirectAreaRouteAttribute : Shared.SharedAutoRouteAttribute
    {
        public string? Area { get => null; set => Apply(value); }

        private void Apply(string? area) => Shared.SharedRoutes.Prefix(this, area);
    }

    public sealed class LocalCtorHelperRouteAttribute : System.Attribute, IRouteTemplateProvider
    {
        public LocalCtorHelperRouteAttribute()
        {
            Shared.SharedRoutes.Prefix(this, ""admin"");
        }

        public string? Template { get; set; } = ""init"";

        public int? Order => null;

        public string? Name => null;
    }

    public sealed class LocalInitOverSharedPlainBaseRouteAttribute : Shared.SharedPlainBaseAttribute, IRouteTemplateProvider
    {
        public string? Template { get; set; } = ""init"";

        public int? Order => null;

        public string? Name => null;
    }

    public sealed class LocalFieldSelfAreaRouteAttribute : Shared.SharedAutoRouteAttribute
    {
        private readonly object _self;

        public LocalFieldSelfAreaRouteAttribute()
        {
            _self = this;
        }

        public string? Area { get => null; set => Shared.SharedRoutes.Prefix(_self, value); }
    }
}
";

    [Theory]
    [InlineData("[LocalInitOverrideRoute]", null, "the generator cannot read the route template of its [LocalInitOverrideRoute] attribute, as the referenced assembly 'Shared' holds no code of SharedBaseCtorRouteAttribute, which may set LocalInitOverrideRouteAttribute.Template")]
    [InlineData("[LocalAutoOverrideRoute(Template = \"items\")]", "/api/products/items", null)]
    [InlineData("[LocalConstantOverrideRoute]", "/api/products/items", null)]
    [InlineData("[LocalSideOverSharedAutoRoute(Template = \"items\", Area = \"admin\")]", null, "the generator cannot read the route template of its [LocalSideOverSharedAutoRoute] attribute, as the value SharedAutoRouteAttribute.Template returns is set by code other than its setter and the constructors, which the generator does not follow")]
    [InlineData("[LocalPlainOverSharedAutoRoute(Template = \"items\")]", "/api/products/items", null)]
    [InlineData("[LocalForwardingAreaRoute(Template = \"items\", Area = \"admin\")]", null, "the generator cannot read the route template of its [LocalForwardingAreaRoute] attribute, as the setter of LocalForwardingAreaRouteAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[LocalOwnAreaRoute(Template = \"items\", Area = \"admin\")]", "/api/products/items", null)]
    [InlineData("[LocalHelperAreaRoute(Template = \"items\", Area = \"admin\")]", null, "the generator cannot read the route template of its [LocalHelperAreaRoute] attribute, as the setter of LocalHelperAreaRouteAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[LocalHelperAreaRoute(Area = \"admin\")]", null, "the generator cannot read the route template of its [LocalHelperAreaRoute] attribute, as the setter of LocalHelperAreaRouteAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[LocalTrimmingZoneRoute(Template = \"items\", Zone = \" east \")]", null, "the generator cannot read the route template of its [LocalTrimmingZoneRoute] attribute, as the setter of LocalTrimmingZoneRouteAttribute.Zone, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[LocalStoringZoneRoute(Template = \"items\", Zone = \" east \")]", "/api/products/items", null)]
    [InlineData("[LocalSelfVarAreaRoute(Template = \"items\", Area = \"admin\")]", null, "the generator cannot read the route template of its [LocalSelfVarAreaRoute] attribute, as the setter of LocalSelfVarAreaRouteAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[LocalIndirectAreaRoute(Template = \"items\", Area = \"admin\")]", null, "the generator cannot read the route template of its [LocalIndirectAreaRoute] attribute, as the setter of LocalIndirectAreaRouteAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[LocalCtorHelperRoute]", null, "the generator cannot read the route template of its [LocalCtorHelperRoute] attribute, as the constructor of LocalCtorHelperRouteAttribute does more than assign values to the attribute's fields and auto-properties, which the generator does not follow")]
    [InlineData("[LocalInitOverSharedPlainBaseRoute]", null, "the generator cannot read the route template of its [LocalInitOverSharedPlainBaseRoute] attribute, as the referenced assembly 'Shared' holds no code of SharedPlainBaseAttribute, which may set LocalInitOverSharedPlainBaseRouteAttribute.Template")]
    [InlineData("[LocalFieldSelfAreaRoute(Template = \"items\", Area = \"admin\")]", null, "the generator cannot read the route template of its [LocalFieldSelfAreaRoute] attribute, as the setter of LocalFieldSelfAreaRouteAttribute.Area, which a named argument runs, does more than store its value, which the generator does not follow")]
    [InlineData("[Shared.SharedSideSetRoute(Template = \"items\", Area = \"admin\")]", null, "the generator cannot read the route template of its [SharedSideSetRoute] attribute, as the referenced assembly 'Shared' holds no code of SharedSideSetRouteAttribute.Area, whose setter a named argument runs, and which may set SharedSideSetRouteAttribute.Template")]
    [InlineData("[Shared.SharedSideSetRoute(Template = \"items\", Weight = 2)]", "/api/products/items", null)]
    [InlineData("[Shared.SharedSideSetRoute(Template = \"items\")]", "/api/products/items", null)]
    public void Generator_ReadsTheTemplateOfARouteAttribute_OnlyWhereNoCodeOfAReferencedAssemblyMaySetIt(string attribute, string? expectedRoute, string? reason)
    {
        // A referenced base class's constructor may set the template through the override, after its initializer; it runs
        // before the named arguments, though. The setter of another named argument, which runs with them, may set it as
        // well, unless the compiler wrote it for an auto-property.
        var source = RouteAttributeUsings + AttributesDerivedFromReferencedOnes + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        " + attribute + @"
        public IActionResult Find() => Ok();
    }
}
";
        var compilation = CreateCompilation(source).AddReferences(CompileToReference(RouteAttributeUsings + ReferencedRouteAttributes, "Shared"));

        CreateDriver(AllExceptExcludedConfig).RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var generated = string.Join("\n", outputCompilation.SyntaxTrees.Skip(1).Select(x => x.ToString()));
        var warnings = diagnostics.Where(x => x.Id == UnbuildableRouteId).Select(x => x.GetMessage()).ToList();

        if (reason is null)
        {
            Assert.Empty(warnings);
            Assert.Equal($"var route = $\"{expectedRoute}\";", GetRouteLine(generated, "ProductsController_FindTool"));
        }
        else
        {
            Assert.Equal($"No MCP tool is generated for action 'ProductsController.Find': {reason}", Assert.Single(warnings));
        }
    }

    [Fact]
    public void Generator_ReadsTheTemplateANamedArgumentSets_OnlyThroughAReferencedAutoProperty()
    {
        // Metadata holds no accessor code, but the compiler marks the accessors it writes for an auto-property.
        const string source = RouteAttributeUsings + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        [Shared.SharedCustomRoute(Template = ""items"")]
        public IActionResult List() => Ok();

        [HttpGet]
        [Shared.SharedTransformedRoute(Template = ""items"")]
        public IActionResult Find() => Ok();
    }
}
";
        var compilation = CreateCompilation(source).AddReferences(CompileToReference(RouteAttributeUsings + ReferencedRouteAttributes, "Shared"));

        CreateDriver(AllExceptExcludedConfig).RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var generated = string.Join("\n", outputCompilation.SyntaxTrees.Skip(1).Select(x => x.ToString()));
        var warning = Assert.Single(diagnostics, x => x.Id == UnbuildableRouteId);

        Assert.Equal(
            "No MCP tool is generated for action 'ProductsController.Find': the generator cannot read the route template of its [SharedTransformedRoute] attribute, as the referenced assembly 'Shared' holds no code of SharedTransformedRouteAttribute.Template",
            warning.GetMessage());
        Assert.Equal("var route = $\"/api/products/items\";", GetRouteLine(generated, "ProductsController_ListTool"));
    }

    [Fact]
    public void Generator_RecognizesRouteAttributesFromAReferencedAssembly_AndWarnsThatItCannotReadTheirTemplate()
    {
        // Metadata holds no constructor body, so the template a referenced attribute passes to MVC's stays unknown. A tool
        // built without it would call /{id} or no endpoint at all, as the generator used to.
        const string source = RouteAttributeUsings + @"
namespace TestApi
{
    [ApiController]
    [Shared.SharedRoute]
    public class ProductsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        public IActionResult Get(int id) => Ok();

        [HttpGet(""/absolute/{id}"")]
        public IActionResult GetAbsolute(int id) => Ok();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [Shared.SharedGet(""{id}"")]
        public IActionResult Get(int id) => Ok();

        [Shared.SharedGet(""{id}"")]
        [HttpPost(""create"")]
        public IActionResult Create() => Ok();
    }
}
";
        var compilation = CreateCompilation(source).AddReferences(CompileToReference(RouteAttributeUsings + ReferencedRouteAttributes, "Shared"));

        CreateDriver(AllExceptExcludedConfig).RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var errors = diagnostics.Concat(outputCompilation.GetDiagnostics()).Where(x => x.Severity == DiagnosticSeverity.Error);
        var generated = string.Join("\n", outputCompilation.SyntaxTrees.Skip(1).Select(x => x.ToString()));
        var warnings = diagnostics.Where(x => x.Id == UnbuildableRouteId).OrderBy(x => x.Location.SourceSpan.Start).ToList();

        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
        Assert.Equal(2, warnings.Count);
        Assert.Equal(
            "No MCP tool is generated for action 'ProductsController.Get': the generator cannot read the route template of the [SharedRoute] attribute on ProductsController, as the referenced assembly 'Shared' holds no code of the constructor of SharedRouteAttribute",
            warnings[0].GetMessage());
        Assert.Equal("Get", GetLocatedText(warnings[0].Location, source));
        Assert.Equal(
            "No MCP tool is generated for action 'OrdersController.Get': the generator cannot read the route template of its [SharedGet] attribute, as the referenced assembly 'Shared' holds no code of the constructor of SharedGetAttribute",
            warnings[1].GetMessage());
        Assert.DoesNotContain("class ProductsController_GetTool", generated);
        Assert.DoesNotContain("class OrdersController_GetTool", generated);
        // An absolute template replaces the controller's, and MVC serves the action at every route of its own, so a known
        // one is enough.
        Assert.Equal("var route = $\"/absolute/{routeid}\";", GetRouteLine(generated, "ProductsController_GetAbsoluteTool"));
        Assert.Equal("var route = $\"/api/orders/create\";", GetRouteLine(generated, "OrdersController_CreateTool"));
    }

    private static string GenerateCompilingRouteAttributeTool(string controllerDeclaration, string action, string toolClassName)
    {
        var source = RouteAttributeUsings + CustomRouteAttributes + $@"
namespace TestApi
{{
    {controllerDeclaration}
    {{
        {action}
    }}
}}
";
        var errors = CompileWithGeneratedTools(source, AllExceptExcludedConfig);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));

        return GetToolClass(RunGenerator(source, AllExceptExcludedConfig), toolClassName);
    }
}
