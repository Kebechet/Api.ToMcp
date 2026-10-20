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

    public sealed class DocsRouteAttribute : Attribute, IRouteTemplateProvider
    {
        public string Template => ""docs/[controller]"";

        public int? Order => null;

        public string? Name => null;
    }

    public sealed class GetOnlyAttribute : Attribute, IActionHttpMethodProvider
    {
        public IEnumerable<string> HttpMethods { get; } = new[] { ""GET"" };
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
    [InlineData("[ApiRoute] public abstract class ApiControllerBase : ControllerBase { } public class OrdersController : ApiControllerBase", "OrdersController_FindTool", "/api/orders")]
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
}
";

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
            "No MCP tool is generated for action 'ProductsController.Get': the route template of the [SharedRoute] attribute on ProductsController is set in code the generator cannot read, such as a constructor in a referenced assembly",
            warnings[0].GetMessage());
        Assert.Equal("Get", GetLocatedText(warnings[0].Location, source));
        Assert.Equal(
            "No MCP tool is generated for action 'OrdersController.Get': the route template of its [SharedGet] attribute is set in code the generator cannot read, such as a constructor in a referenced assembly",
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
