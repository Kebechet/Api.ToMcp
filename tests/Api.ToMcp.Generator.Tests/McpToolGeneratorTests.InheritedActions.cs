using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Controllers and actions as MVC discovers them: a concrete controller serves the public actions it inherits from its
/// base classes, under its own name, and an abstract base serves nothing. A tool for the base calls a route that 404s,
/// while the endpoints that work get none. https://github.com/Kebechet/Api.ToMcp/issues/28
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string CrudControllers = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public abstract class CrudController<T> : ControllerBase where T : class, new()
    {
        /// <summary>Lists every item.</summary>
        [HttpGet]
        public ActionResult<List<T>> GetAll() => new List<T>();

        [HttpGet(""[action]"")]
        public ActionResult<int> Count() => 0;

        /// <param name=""item"">The item to create.</param>
        [HttpPost]
        public ActionResult<T> Create([FromBody] T item) => item;
    }

    public class ProductCrudController : CrudController<Product>
    {
    }

    public class OrderCrudController : CrudController<Order>
    {
    }
}
";

    [Fact]
    public void Generator_GivesEachConcreteController_TheActionsItInheritsFromAnAbstractBase()
    {
        AssertCompiles(CrudControllers, AllExceptExcludedConfig);

        var generated = RunGenerator(CrudControllers, AllExceptExcludedConfig);

        // MVC fills [controller] and [action] from the controller that serves the request.
        Assert.Equal("var route = $\"/api/productcrud\";", GetRouteLine(generated, "ProductCrudController_GetAllTool"));
        Assert.Equal("var route = $\"/api/ordercrud\";", GetRouteLine(generated, "OrderCrudController_GetAllTool"));
        Assert.Equal("var route = $\"/api/productcrud/count\";", GetRouteLine(generated, "ProductCrudController_CountTool"));
        Assert.Contains("[McpServerTool(Name = \"OrderCrud_Create\", ", GetToolClass(generated, "OrderCrudController_CreateTool"));
        Assert.Contains("typeof(ProductCrudController_GetAllTool)", generated);
        Assert.Contains("typeof(OrderCrudController_GetAllTool)", generated);

        Assert.DoesNotMatch(@"\bCrudController_", generated);
        Assert.DoesNotContain("/api/crud", generated);
    }

    [Fact]
    public void Generator_SubstitutesTheTypeArguments_OfActionsInheritedFromAGenericBase()
    {
        var generated = RunGenerator(CrudControllers, AllExceptExcludedConfig);

        var getAll = GetToolClass(generated, "ProductCrudController_GetAllTool");
        Assert.Contains("OutputSchemaType = typeof(global::System.Collections.Generic.List<global::TestApi.Product>)", getAll);
        Assert.Contains("[Description(\"Lists every item.\")]", getAll);
        // The base action's body is still read: it is proven to answer with a list.
        Assert.Contains(StrictOutput, getAll);

        var create = GetToolClass(generated, "OrderCrudController_CreateTool");
        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Order)", create);
        Assert.Contains("[Description(\"The item to create.\")] TestApi.Order item", create);
        Assert.Contains("[Description(\"Invokes OrderCrudController.Create\")]", create);
    }

    [Fact]
    public void Generator_GivesAnOverriddenAction_OneToolFromTheMostDerivedClass()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public abstract class CatalogControllerBase : ControllerBase
    {
        /// <summary>Gets an item of the catalog.</summary>
        [HttpGet(""{id}"")]
        public virtual ActionResult<Product> Get(Guid id) => new Product();

        [HttpGet(""featured"")]
        public virtual ActionResult<Product> GetFeatured() => new Product();

        [HttpDelete(""{id}"")]
        public abstract ActionResult<Product> Remove(Guid id);
    }

    public class ShopController : CatalogControllerBase
    {
        /// <summary>Gets an item of the shop.</summary>
        [HttpGet(""item/{id}"")]
        public override ActionResult<Product> Get(Guid id) => new Product();

        // MVC reads [HttpGet] from the base action, as it is inherited.
        public override ActionResult<Product> GetFeatured() => new Product();

        public override ActionResult<Product> Remove(Guid id) => new Product();
    }

    public class OutletController : ShopController
    {
        public sealed override ActionResult<Product> Get(Guid id) => new Product();
    }

    public abstract class ListControllerBase<T> : ControllerBase where T : new()
    {
        [HttpGet(""list"")]
        public virtual ActionResult<List<T>> GetList() => new List<T>();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ListControllerBase<Order>
    {
        public override ActionResult<List<Order>> GetList() => new List<Order>();
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Equal(1, CountToolClasses(generated, "ShopController_GetTool"));
        Assert.Equal("var route = $\"/api/shop/item/{routeid}\";", GetRouteLine(generated, "ShopController_GetTool"));
        Assert.Contains("[Description(\"Gets an item of the shop.\")]", GetToolClass(generated, "ShopController_GetTool"));
        Assert.Equal("var route = $\"/api/shop/featured\";", GetRouteLine(generated, "ShopController_GetFeaturedTool"));
        Assert.Equal("var route = $\"/api/shop/{routeid}\";", GetRouteLine(generated, "ShopController_RemoveTool"));

        Assert.Equal(1, CountToolClasses(generated, "OutletController_GetTool"));
        // MVC reads the route of an override that declares none from the action as first declared, skipping the
        // overrides in between (GetBaseDefinition): ShopController's "item/{id}" does not apply here.
        Assert.Equal("var route = $\"/api/outlet/{routeid}\";", GetRouteLine(generated, "OutletController_GetTool"));
        Assert.Equal(1, CountToolClasses(generated, "OutletController_GetFeaturedTool"));
        Assert.Equal(1, CountToolClasses(generated, "OutletController_RemoveTool"));

        Assert.Equal(1, CountToolClasses(generated, "OrdersController_GetListTool"));
        Assert.Equal("var route = $\"/api/orders/list\";", GetRouteLine(generated, "OrdersController_GetListTool"));

        Assert.DoesNotContain("CatalogControllerBase_", generated);
        Assert.DoesNotContain("ListControllerBase_", generated);
        Assert.DoesNotMatch(@"Tool_\d", generated);
    }

    [Fact]
    public void Generator_RoutesAnOverride_ByTheRouteAttributesItDeclares_AsAWhole()
    {
        // MVC takes [Route] and the [Http*] attributes together, from the override if it declares any of them, and only
        // otherwise from the action it overrides.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class ReportsControllerBase : ControllerBase
    {
        [HttpGet(""daily"")]
        public virtual ActionResult<Product> GetDaily() => new Product();

        [HttpGet(""weekly"")]
        public virtual ActionResult<Product> GetWeekly() => new Product();

        [HttpGet(""yearly"")]
        public virtual ActionResult<Product> GetYearly() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class AltsController : ReportsControllerBase
    {
        // Served at api/alts/alt for every HTTP method, so it names none for a tool to call, like a [Route] alone on an
        // action that overrides nothing.
        [Route(""alt"")]
        public override ActionResult<Product> GetDaily() => new Product();

        // Served for HEAD only, which the generator does not call.
        [HttpHead(""weekly"")]
        public override ActionResult<Product> GetWeekly() => new Product();

        public override ActionResult<Product> GetYearly() => new Product();
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.DoesNotContain("AltsController_GetDailyTool", generated);
        Assert.DoesNotContain("/api/alts/daily", generated);
        Assert.DoesNotContain("AltsController_GetWeeklyTool", generated);
        Assert.Equal("var route = $\"/api/alts/yearly\";", GetRouteLine(generated, "AltsController_GetYearlyTool"));
    }

    [Fact]
    public void Generator_DescribesAnOverride_ByTheDocsOfTheActionItOverrides_WhereItHasNoneOfItsOwn()
    {
        // The description the base action's author wrote is the one an override without docs, or with <inheritdoc/>, means.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class ReportsControllerBase : ControllerBase
    {
        /// <summary>Gets the daily report.</summary>
        /// <param name=""day"">The day to report on.</param>
        [HttpGet(""daily"")]
        public virtual ActionResult<Product> GetDaily(DateOnly day) => new Product();

        /// <summary>Gets the weekly report.</summary>
        /// <param name=""week"">The week to report on.</param>
        [HttpGet(""weekly"")]
        public virtual ActionResult<Product> GetWeekly(int week) => new Product();

        /// <summary>Gets the monthly report.</summary>
        /// <param name=""month"">The month to report on.</param>
        /// <param name=""region"">The region to report on.</param>
        [HttpGet(""monthly"")]
        public virtual ActionResult<Product> GetMonthly(int month, string region) => new Product();
    }

    public abstract class SalesControllerBase : ReportsControllerBase
    {
        /// <inheritdoc />
        public override ActionResult<Product> GetDaily(DateOnly day) => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class SalesController : SalesControllerBase
    {
        /// <inheritdoc />
        public override ActionResult<Product> GetDaily(DateOnly day) => new Product();

        // A parameter's docs follow it by position, as an override may rename it.
        public override ActionResult<Product> GetWeekly(int number) => new Product();

        /// <summary>Gets the sales of a month.</summary>
        /// <param name=""region"">The sales region.</param>
        public override ActionResult<Product> GetMonthly(int month, string region) => new Product();
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        var daily = GetToolClass(generated, "SalesController_GetDailyTool");
        Assert.Contains("[Description(\"Gets the daily report.\")]", daily);
        Assert.Contains("[Description(\"The day to report on.\")] System.DateOnly day", daily);

        var weekly = GetToolClass(generated, "SalesController_GetWeeklyTool");
        Assert.Contains("[Description(\"Gets the weekly report.\")]", weekly);
        Assert.Contains("[Description(\"The week to report on.\")] int number", weekly);

        // The override's own docs win, and a parameter it leaves undocumented keeps the base's.
        var monthly = GetToolClass(generated, "SalesController_GetMonthlyTool");
        Assert.Contains("[Description(\"Gets the sales of a month.\")]", monthly);
        Assert.Contains("[Description(\"The month to report on.\")] int month", monthly);
        Assert.Contains("[Description(\"The sales region.\")] string region", monthly);
        Assert.DoesNotContain("Invokes SalesController", generated);
    }

    [Fact]
    public void Generator_NamesTheToolOfAnAbstractBaseWithALiteralRoute_AfterTheControllerThatServesIt()
    {
        // The base's own tool worked here, as the route names no [controller]; it comes back under the derived controller.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/products"")]
    public abstract class ProductsControllerBase : ControllerBase
    {
        [HttpGet]
        public ActionResult<List<Product>> GetAll() => new List<Product>();
    }

    public class ProductsController : ProductsControllerBase
    {
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Equal("var route = $\"/api/products\";", GetRouteLine(generated, "ProductsController_GetAllTool"));
        Assert.Contains("[McpServerTool(Name = \"Products_GetAll\", ", GetToolClass(generated, "ProductsController_GetAllTool"));
        Assert.DoesNotContain("ProductsControllerBase_", generated);
    }

    [Fact]
    public void Generator_SkipsNonActionsAndNonControllers_AsMvcDoes()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class AuditedControllerBase : ControllerBase
    {
        [NonAction]
        [HttpGet(""audit"")]
        public virtual ActionResult<Product> GetAudit() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : AuditedControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();

        // [NonAction] is inherited from the action an override replaces.
        [HttpGet(""audit"")]
        public override ActionResult<Product> GetAudit() => new Product();

        [NonAction]
        [HttpGet(""internal"")]
        public ActionResult<Product> GetInternal() => new Product();
    }

    [NonController]
    [ApiController]
    [Route(""api/[controller]"")]
    public class HelpersController : ControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();
    }

    // [NonController] is inherited.
    public class MoreHelpersController : HelpersController
    {
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Contains("class ProductsController_GetTool", generated);
        Assert.DoesNotContain("ProductsController_GetAuditTool", generated);
        Assert.DoesNotContain("ProductsController_GetInternalTool", generated);
        Assert.DoesNotContain("AuditedControllerBase_", generated);
        Assert.DoesNotContain("HelpersController_", generated);
    }

    [Fact]
    public void Generator_SkipsClassesAndMethods_MvcDoesNotServe()
    {
        // MVC takes only public top-level classes without type parameters, and only instance methods without type
        // parameters.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    internal class InternalController : ControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();
    }

    public class Outer
    {
        [ApiController]
        [Route(""api/[controller]"")]
        public class NestedController : ControllerBase
        {
            [HttpGet]
            public ActionResult<Product> Get() => new Product();
        }
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OpenController<T> : ControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class PocoController
    {
        [HttpGet]
        public Product Get() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();

        [HttpGet(""static"")]
        public static Product GetStatic() => new Product();

        [HttpGet(""generic"")]
        public ActionResult<Product> GetGeneric<T>() => new Product();
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Contains("class ProductsController_GetTool", generated);
        Assert.Contains("class PocoController_GetTool", generated);
        Assert.DoesNotContain("ProductsController_GetStaticTool", generated);
        Assert.DoesNotContain("ProductsController_GetGenericTool", generated);
        Assert.DoesNotContain("InternalController_", generated);
        Assert.DoesNotContain("NestedController_", generated);
        Assert.DoesNotContain("OpenController_", generated);
    }

    [Fact]
    public void Generator_SkipsObjectsMethodsAndTheDisposeImplementation_AsMvcDoes()
    {
        // MVC takes no action from a method first declared on object, or from the one that implements IDisposable.Dispose
        // for the class that first declares it. A method that hides either with new is an action of its own.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase, IDisposable
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();

        [HttpGet(""str"")]
        public override string ToString() => ""products"";

        [HttpGet(""hash"")]
        public override int GetHashCode() => 1;

        [HttpPost(""dispose"")]
        public void Dispose()
        {
        }
    }

    public abstract class ResourceControllerBase : ControllerBase, IDisposable
    {
        public virtual void Dispose()
        {
        }
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class FilesController : ResourceControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();

        [HttpDelete]
        public override void Dispose()
        {
        }
    }

    // MVC's Controller implements IDisposable itself.
    [Route(""api/[controller]"")]
    public class ReportsController : Controller
    {
        [HttpGet(""text"")]
        public new string ToString() => ""reports"";

        [HttpPost(""dispose"")]
        public new void Dispose()
        {
        }
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Contains("class ProductsController_GetTool", generated);
        Assert.DoesNotContain("ProductsController_ToStringTool", generated);
        Assert.DoesNotContain("ProductsController_GetHashCodeTool", generated);
        Assert.DoesNotContain("ProductsController_DisposeTool", generated);
        Assert.Contains("class FilesController_GetTool", generated);
        Assert.DoesNotContain("FilesController_DisposeTool", generated);
        Assert.Equal("var route = $\"/api/reports/text\";", GetRouteLine(generated, "ReportsController_ToStringTool"));
        Assert.Equal("var route = $\"/api/reports/dispose\";", GetRouteLine(generated, "ReportsController_DisposeTool"));
    }

    [Fact]
    public void Generator_TakesEveryClassWithApiController_WhateverItsName()
    {
        // MVC takes a class whose name does not end in "Controller" when it carries [Controller], and [ApiController]
        // derives from it. Both are inherited, and MVC finds them by type, so an attribute derived from them counts too.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/plain"")]
    public class Plain
    {
        [HttpGet]
        public Product Get() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public abstract class ApiBase
    {
    }

    public class Things : ApiBase
    {
        [HttpGet]
        public Product Get() => new Product();
    }

    public sealed class PublicApiAttribute : ApiControllerAttribute
    {
    }

    [PublicApi]
    [Route(""api/gadgets"")]
    public class Gadgets
    {
        [HttpGet]
        public Product Get() => new Product();
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Equal("var route = $\"/api/plain\";", GetRouteLine(generated, "Plain_GetTool"));
        Assert.Contains("[McpServerTool(Name = \"Plain_Get\", ", GetToolClass(generated, "Plain_GetTool"));
        Assert.Equal("var route = $\"/api/things\";", GetRouteLine(generated, "Things_GetTool"));
        Assert.Equal("var route = $\"/api/gadgets\";", GetRouteLine(generated, "Gadgets_GetTool"));
        Assert.DoesNotContain("ApiBase_", generated);
    }

    [Fact]
    public void Generator_SelectsAnInheritedAction_ByTheConcreteControllersName()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""ProductCrud.GetAll"", ""OrderCrudController"", ""Crud.Count"", ""CrudController""] }";

        var generated = RunGenerator(CrudControllers, config);

        Assert.Contains("class ProductCrudController_GetAllTool", generated);
        Assert.DoesNotContain("ProductCrudController_CountTool", generated);
        Assert.DoesNotContain("ProductCrudController_CreateTool", generated);
        Assert.Contains("class OrderCrudController_GetAllTool", generated);
        Assert.Contains("class OrderCrudController_CountTool", generated);
        Assert.Contains("class OrderCrudController_CreateTool", generated);
    }

    [Fact]
    public void Generator_KeepsHidingTheActionsOfABaseController_ThatCarriesMcpIgnore()
    {
        // [McpIgnore] is inherited: it hid the base's actions while they were listed under the base, and it covers every
        // controller that derives from it.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [Api.ToMcp.Abstractions.McpIgnore]
    public abstract class InternalControllerBase : ControllerBase
    {
        [HttpPost(""reindex"")]
        public ActionResult<Product> Reindex() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : InternalControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet]
        public ActionResult<Order> Get() => new Order();
    }
}
";
        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Contains("class OrdersController_GetTool", generated);
        Assert.DoesNotContain("Reindex", generated);
        Assert.DoesNotContain("ProductsController_", generated);
    }

    [Fact]
    public void Generator_ReadsTheResponseTypesDocsAndBodyOfAnInheritedAction()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public abstract class ReportsControllerBase : ControllerBase
    {
        /// <summary>Gets the daily report.</summary>
        [HttpGet(""daily"")]
        [ProducesResponseType<Product>(StatusCodes.Status200OK)]
        public IActionResult GetDaily() => Ok(new Product());

        [HttpGet(""csv"")]
        [Produces(""text/csv"")]
        public ActionResult<Product> GetCsv() => new Product();

        [HttpGet(""latest"")]
        public ActionResult<Product> GetLatest(bool empty) => empty ? NoContent() : new Product();
    }

    [ApiController]
    public class SalesController : ReportsControllerBase
    {
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        var daily = GetToolClass(generated, "SalesController_GetDailyTool");
        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Product)", daily);
        Assert.Contains("[Description(\"Gets the daily report.\")]", daily);
        Assert.Contains(StrictOutput, daily);
        Assert.Equal("var route = $\"/api/sales/daily\";", GetRouteLine(generated, "SalesController_GetDailyTool"));

        Assert.DoesNotContain("UseStructuredContent", GetToolClass(generated, "SalesController_GetCsvTool"));
        Assert.Contains(NullableOutput, GetToolClass(generated, "SalesController_GetLatestTool"));
    }

    private static void AssertCompiles(string source, string generatorJson)
    {
        var errors = CompileWithGeneratedTools(source, generatorJson);

        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
    }

    private static int CountToolClasses(string generated, string toolClassName)
    {
        return Regex.Matches(generated, $@"\bclass {Regex.Escape(toolClassName)}(_\d+)?\b").Count;
    }
}
