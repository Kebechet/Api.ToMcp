using System;
using System.Linq;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// The flow analysis that proves an output schema need not allow null binds the action's whole body, so the generator
/// runs it only for a tool that gets an output schema: not with <c>"structuredOutput": false</c>, and not for an action
/// the config leaves out. It reads the compilation after the config, so an edit in another file that changes what a body
/// returns still reaches the schema. See https://github.com/Kebechet/Api.ToMcp/issues/27.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string CatalogFile = "Catalog.cs";

    private const string CatalogSource = @"#nullable enable
namespace TestApi
{
    public static class Catalog
    {
        public static Product Find() => new Product();
    }
}
";

    private const string ControllersWithProvableBodies = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => Ok(Catalog.Find());

        [HttpGet(""new"")]
        public ActionResult<Product> New() => new Product();
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

    [Fact]
    public void Generator_AnalyzesTheBodiesOfToolsWithAnOutputSchema_EachDeclarationOnce()
    {
        var driver = CreateTrackingDriver(AllExceptExcludedConfig).RunGenerators(CreateCompilation(CrudControllers));

        // The two controllers inherit each action from one declaration, whose body is the same for both.
        Assert.Equal(
            new[]
            {
                "M:TestApi.CrudController`1.Count~Microsoft.AspNetCore.Mvc.ActionResult{System.Int32}",
                "M:TestApi.CrudController`1.Create(`0)~Microsoft.AspNetCore.Mvc.ActionResult{`0}",
                "M:TestApi.CrudController`1.GetAll~Microsoft.AspNetCore.Mvc.ActionResult{System.Collections.Generic.List{`0}}"
            },
            GetBodiesToAnalyze(driver).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(6, GetGeneratedSources(driver).Keys.Count(x => x.EndsWith("Tool.g.cs")));
    }

    [Fact]
    public void Generator_AnalyzesNoBody_WhenStructuredOutputIsOff()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""structuredOutput"": false }";

        var driver = CreateTrackingDriver(config).RunGenerators(CreateCompilationOfFiles((ControllersFile, ControllersWithProvableBodies), (CatalogFile, CatalogSource)));

        Assert.Empty(GetBodiesToAnalyze(driver));
        Assert.Equal(3, GetGeneratedSources(driver).Keys.Count(x => x.EndsWith("Tool.g.cs")));
    }

    [Fact]
    public void Generator_AnalyzesOnlyTheBodiesOfTheActionsTheConfigSelects()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""ProductsController.Get""] }";

        var driver = CreateTrackingDriver(config).RunGenerators(CreateCompilationOfFiles((ControllersFile, ControllersWithProvableBodies), (CatalogFile, CatalogSource)));

        Assert.Equal(new[] { "M:TestApi.ProductsController.Get~Microsoft.AspNetCore.Mvc.ActionResult{TestApi.Product}" }, GetBodiesToAnalyze(driver));
    }

    [Fact]
    public void Generator_AnalyzesTheBodyAgain_AfterAnEditInAnotherFileChangesWhatItReturns()
    {
        var compilation = CreateCompilationOfFiles((ControllersFile, ControllersWithProvableBodies), (CatalogFile, CatalogSource));
        var driver = CreateTrackingDriver(AllExceptExcludedConfig).RunGenerators(compilation);
        Assert.Contains(StrictOutput, GetGeneratedSources(driver)["ProductsController_GetTool.g.cs"]);

        driver = driver.RunGenerators(ReplaceText(compilation, CatalogFile, "public static Product Find() => new Product();", "public static Product? Find() => null;"));

        // No controller changed, so the plan is reused; the analysis reads the edited compilation, and only the tool whose
        // body returns what Find() returns is generated again.
        AssertStepReasons(driver, TrackingNames.Plan, IncrementalStepRunReason.Cached);
        Assert.Contains(NullableOutput, GetGeneratedSources(driver)["ProductsController_GetTool.g.cs"]);
        var toolReasons = GetStepOutputs(driver, TrackingNames.ToolSources).ToDictionary(x => ((ToolSourceModel)x.Value).ClassName, x => x.Reason);
        Assert.Equal(IncrementalStepRunReason.Modified, toolReasons["ProductsController_GetTool"]);
        Assert.Equal(IncrementalStepRunReason.Unchanged, toolReasons["ProductsController_NewTool"]);
        Assert.Equal(IncrementalStepRunReason.Unchanged, toolReasons["OrdersController_GetTool"]);
    }

    /// <summary>
    /// The method declarations, by documentation ID, whose bodies the run analyzed.
    /// </summary>
    private static string[] GetBodiesToAnalyze(GeneratorDriver driver)
    {
        var output = Assert.Single(GetStepOutputs(driver, TrackingNames.BodiesToAnalyze));

        return ((EquatableArray<string>)output.Value).ToArray();
    }
}
