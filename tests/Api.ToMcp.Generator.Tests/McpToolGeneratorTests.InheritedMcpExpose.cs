using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// <c>[McpExpose]</c> and <c>[McpIgnore]</c> are declared <c>Inherited = true</c>, so they apply where reflection would
/// find them: on the action, else the action it overrides, and on the controller, else its nearest base class. Roslyn's
/// <c>GetAttributes()</c> returns only what a symbol declares itself. https://github.com/Kebechet/Api.ToMcp/issues/22
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string SelectedOnlyWithoutIncludes = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [] }";

    [Fact]
    public void Generator_ExposesEveryControllerDerivedFromABaseClass_ThatCarriesMcpExpose()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [Api.ToMcp.Abstractions.McpExpose]
    public abstract class AdminControllerBase : ControllerBase
    {
        [HttpGet(""audit"")]
        public ActionResult<Product> GetAudit() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class UsersController : AdminControllerBase
    {
        [HttpGet]
        public ActionResult<List<Product>> GetAll() => new List<Product>();
    }

    public class AdminsController : UsersController
    {
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
        AssertCompiles(source, SelectedOnlyWithoutIncludes);

        var generated = RunGenerator(source, SelectedOnlyWithoutIncludes);

        Assert.Contains("class UsersController_GetAllTool", generated);
        Assert.Contains("class UsersController_GetAuditTool", generated);
        Assert.Equal("var route = $\"/api/admins\";", GetRouteLine(generated, "AdminsController_GetAllTool"));
        Assert.Contains("class AdminsController_GetAuditTool", generated);
        Assert.DoesNotContain("OrdersController_", generated);
    }

    [Fact]
    public void Generator_TakesExposureNameDescriptionTitleScopeAndHints_FromTheNearestMcpExposeOfAnOverriddenAction()
    {
        // The nearest [McpExpose] replaces the base's as a whole, as with reflection: an override's own attribute drops
        // every property of the base's, those it does not set included.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    using Api.ToMcp.Abstractions;
    using Api.ToMcp.Abstractions.Scopes;

    public abstract class MirrorControllerBase : ControllerBase
    {
        [McpExpose(ToolName = ""sync_mirror"", Description = ""Syncs the mirror with its source."", Title = ""Sync the mirror"", Scope = McpScope.Read)]
        [HttpPost(""sync"")]
        public virtual ActionResult<Product> Sync() => new Product();

        [McpExpose(ToolName = ""push_mirror"", Description = ""Pushes the mirror to its source."")]
        [HttpPost(""push"")]
        public virtual ActionResult<Product> Push() => new Product();

        [McpExpose(ToolName = ""pull_mirror"", Title = ""Pull from the source"", Scope = McpScope.Read)]
        [HttpPost(""pull"")]
        public virtual ActionResult<Product> Pull() => new Product();

        [McpExpose(ToolName = ""mirror_status"", Description = ""Gets the mirror's status."")]
        [HttpGet(""status"")]
        public ActionResult<Product> GetStatus() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class MirrorsController : MirrorControllerBase
    {
        [HttpPost(""sync"")]
        public override ActionResult<Product> Sync() => new Product();

        [McpExpose(ToolName = ""push_mirror_now"")]
        [HttpPost(""push"")]
        public override ActionResult<Product> Push() => new Product();

        /// <summary>Pulls the source into the mirror.</summary>
        [McpExpose(Destructive = true)]
        public override ActionResult<Product> Pull() => new Product();
    }
}
";
        AssertCompiles(source, SelectedOnlyWithoutIncludes);

        var generated = RunGenerator(source, SelectedOnlyWithoutIncludes);

        var sync = GetToolClass(generated, "MirrorsController_SyncTool");
        Assert.Contains("[McpServerTool(Name = \"sync_mirror\", Title = \"Sync the mirror\", ", sync);
        Assert.Contains("[Description(\"Syncs the mirror with its source.\")]", sync);
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);", GetScopeCheck(generated, "MirrorsController_SyncTool"));

        var push = GetToolClass(generated, "MirrorsController_PushTool");
        Assert.Contains("[McpServerTool(Name = \"push_mirror_now\", ", push);
        Assert.Contains("[Description(\"Invokes MirrorsController.Push\")]", push);

        Assert.Equal(
            "[McpServerTool(Name = \"Mirrors_Pull\", Title = \"Pull\", UseStructuredContent = true, OutputSchemaType = typeof(global::TestApi.Product), ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]",
            GetToolAttributeLine(generated, "MirrorsController_PullTool"));
        Assert.Contains("[Description(\"Pulls the source into the mirror.\")]", GetToolClass(generated, "MirrorsController_PullTool"));
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", GetScopeCheck(generated, "MirrorsController_PullTool"));

        var status = GetToolClass(generated, "MirrorsController_GetStatusTool");
        Assert.Contains("[McpServerTool(Name = \"mirror_status\", ", status);
        Assert.Contains("[Description(\"Gets the mirror's status.\")]", status);
    }

    [Fact]
    public void Generator_LetsAnOverrideKeepTheMcpIgnore_OfTheActionItReplaces()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class CatalogControllerBase : ControllerBase
    {
        [Api.ToMcp.Abstractions.McpIgnore]
        [HttpDelete(""{id}"")]
        public virtual ActionResult<Product> Purge(Guid id) => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : CatalogControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();

        [HttpDelete(""{id}"")]
        public override ActionResult<Product> Purge(Guid id) => new Product();
    }
}
";
        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Contains("class ProductsController_GetTool", generated);
        Assert.DoesNotContain("Purge", generated);
    }
}
