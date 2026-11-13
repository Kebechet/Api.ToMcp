using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// The flow analysis that proves an output schema need not allow null binds the action's whole body, so the generator
/// runs it only for a tool that gets an output schema: not with <c>"structuredOutput": false</c>, and not for an action
/// the config leaves out. It runs in a step of its own that reads the compilation after the config, so an edit in another
/// file that changes what a body returns still reaches the schema. See https://github.com/Kebechet/Api.ToMcp/issues/27.
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

    private const string ProductsGetId = "M:TestApi.ProductsController.Get~Microsoft.AspNetCore.Mvc.ActionResult{TestApi.Product}";
    private const string ProductsNewId = "M:TestApi.ProductsController.New~Microsoft.AspNetCore.Mvc.ActionResult{TestApi.Product}";
    private const string OrdersGetId = "M:TestApi.OrdersController.Get~Microsoft.AspNetCore.Mvc.ActionResult{TestApi.Order}";

    [Fact]
    public void Generator_AnalyzesTheBodiesOfToolsWithAnOutputSchema_EachDeclarationOnce()
    {
        var driver = CreateTrackingDriver(AllExceptExcludedConfig).RunGenerators(CreateCompilation(CrudControllers));

        // The two controllers inherit each action from one declaration, whose body is the same for both.
        var bodies = new[]
        {
            "M:TestApi.CrudController`1.Count~Microsoft.AspNetCore.Mvc.ActionResult{System.Int32}",
            "M:TestApi.CrudController`1.Create(`0)~Microsoft.AspNetCore.Mvc.ActionResult{`0}",
            "M:TestApi.CrudController`1.GetAll~Microsoft.AspNetCore.Mvc.ActionResult{System.Collections.Generic.List{`0}}"
        };
        Assert.Equal(bodies, GetBodiesToAnalyze(driver).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(bodies, GetBodiesNeverEmpty(driver).Bodies.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(6, GetGeneratedSources(driver).Keys.Count(x => x.EndsWith("Tool.g.cs")));
    }

    [Fact]
    public void Generator_AnalyzesNoBody_WhenStructuredOutputIsOff()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""structuredOutput"": false }";

        var driver = CreateTrackingDriver(config).RunGenerators(CreateCompilationOfFiles((ControllersFile, ControllersWithProvableBodies), (CatalogFile, CatalogSource)));

        Assert.Empty(GetBodiesToAnalyze(driver));
        Assert.Empty(GetBodiesNeverEmpty(driver).Bodies);
        Assert.Equal(3, GetGeneratedSources(driver).Keys.Count(x => x.EndsWith("Tool.g.cs")));
    }

    [Fact]
    public void Generator_AnalyzesOnlyTheBodiesOfTheActionsTheConfigSelects()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""ProductsController.Get""] }";

        var driver = CreateTrackingDriver(config).RunGenerators(CreateCompilationOfFiles((ControllersFile, ControllersWithProvableBodies), (CatalogFile, CatalogSource)));

        Assert.Equal(new[] { ProductsGetId }, GetBodiesToAnalyze(driver));
        Assert.Equal(new[] { ProductsGetId }, GetBodiesNeverEmpty(driver).Bodies);
    }

    [Fact]
    public void Generator_AnalyzesTheBodiesInTheirOwnStep_AfterAnEditToAFileWithoutControllers()
    {
        var compilation = CreateCompilationOfFiles((ControllersFile, ControllersWithProvableBodies), (CatalogFile, CatalogSource), (FileWithoutControllers, ClockSource));
        var driver = CreateTrackingDriver(AllExceptExcludedConfig).RunGenerators(compilation);

        driver = driver.RunGenerators(InsertText(compilation, FileWithoutControllers, ClockSource.Length, "// edited\n"));

        // The plan, and with it the list of bodies, is reused; the analysis runs again on the edited compilation and
        // proves what it proved before, so no tool is generated again.
        AssertStepReasons(driver, TrackingNames.BodiesToAnalyze, IncrementalStepRunReason.Cached);
        var (bodies, reason) = GetBodiesNeverEmpty(driver);
        Assert.Equal(IncrementalStepRunReason.Unchanged, reason);
        Assert.Equal(new[] { OrdersGetId, ProductsGetId, ProductsNewId }, bodies.OrderBy(x => x, StringComparer.Ordinal));
        AssertSourceOutputReasons(driver, TrackingNames.ToolSources, IncrementalStepRunReason.Cached);
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
        Assert.Equal(new[] { OrdersGetId, ProductsNewId }, GetBodiesNeverEmpty(driver).Bodies.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(NullableOutput, GetGeneratedSources(driver)["ProductsController_GetTool.g.cs"]);
        var toolReasons = GetStepOutputs(driver, TrackingNames.ToolSources).ToDictionary(x => ((ToolSourceModel)x.Value).ClassName, x => x.Reason);
        Assert.Equal(IncrementalStepRunReason.Modified, toolReasons["ProductsController_GetTool"]);
        Assert.Equal(IncrementalStepRunReason.Unchanged, toolReasons["ProductsController_NewTool"]);
        Assert.Equal(IncrementalStepRunReason.Unchanged, toolReasons["OrdersController_GetTool"]);
    }

    [Fact]
    public void Generator_ProvesTheBodies_InACompilationNotBuiltInParallel()
    {
        // /parallel- makes the analysis run on one thread.
        var compilation = CreateCompilationOfFiles((ControllersFile, ControllersWithProvableBodies), (CatalogFile, CatalogSource));
        compilation = compilation.WithOptions(compilation.Options.WithConcurrentBuild(false));

        var driver = CreateTrackingDriver(AllExceptExcludedConfig).RunGenerators(compilation);

        Assert.Equal(new[] { OrdersGetId, ProductsGetId, ProductsNewId }, GetBodiesNeverEmpty(driver).Bodies.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(StrictOutput, GetGeneratedSources(driver)["ProductsController_GetTool.g.cs"]);
    }

    [Fact]
    public void Generator_ProvesTheBody_WhenAParameterTypeIsOneTheGeneratorCannotSee()
    {
        // Source generators do not see each other's output, so a type another generator makes is an error type here.
        var source = StructuredOutputSource("public ActionResult<Product> Get([FromServices] IMadeByAnotherGenerator service) => new Product();");

        var generated = GetGeneratedSources(CreateDriver(AllExceptExcludedConfig).RunGenerators(CreateCompilation(source)));

        Assert.Contains(StrictOutput, generated[ToolClassName + ".g.cs"]);
    }

    [Fact]
    public void Generator_ProvesTheBody_WhenAParameterTypeIsReachedThroughAnExternAlias()
    {
        var library = CreateCompilation("namespace Lib { public sealed class Service { } }").WithAssemblyName("Lib");
        using var stream = new MemoryStream();
        Assert.True(library.Emit(stream).Success);
        var reference = MetadataReference.CreateFromImage(stream.ToArray(), new MetadataReferenceProperties(aliases: ImmutableArray.Create("V1")));
        var source = "extern alias V1;\n" + StructuredOutputSource("public ActionResult<Product> Get([FromServices] V1::Lib.Service service) => new Product();");
        var compilation = CreateCompilation(source).AddReferences(reference);
        Assert.DoesNotContain(compilation.GetDiagnostics(), x => x.Severity == DiagnosticSeverity.Error);

        var generated = GetGeneratedSources(CreateDriver(AllExceptExcludedConfig).RunGenerators(compilation));

        Assert.Contains(StrictOutput, generated[ToolClassName + ".g.cs"]);
    }

    [Fact]
    public void Generator_ProvesEachBody_OfTwoFileLocalClassesDeclaringTheSameHandler()
    {
        // Both handlers have one documentation ID, as a file-local class's name does not show in it; each tool is decided
        // by its own handler's body.
        var withAValue = MinimalApiUsings + MinimalApiModels + @"
namespace TestApi
{
    file static class FileHandlers { public static Order Get() => new Order(); }
    public static class EndpointsA { public static void Map(WebApplication app) => app.MapGet(""/a"", FileHandlers.Get).WithName(""A""); }
}";
        var withNull = MinimalApiUsings + @"
namespace TestApi
{
    file static class FileHandlers { public static Order Get() => null!; }
    public static class EndpointsB { public static void Map(WebApplication app) => app.MapGet(""/b"", FileHandlers.Get).WithName(""B""); }
}";
        var compilation = CreateCompilationOfFiles(("A.cs", withAValue), ("B.cs", withNull));
        Assert.DoesNotContain(compilation.GetDiagnostics(), x => x.Severity == DiagnosticSeverity.Error);

        var generated = GetGeneratedSources(CreateDriver(MinimalApisConfig).RunGenerators(compilation));

        Assert.Contains("McpGeneratedTool(IsOutputNullable = false)", generated["FileHandlers_ATool.g.cs"]);
        Assert.Contains("McpGeneratedTool(IsOutputNullable = true)", generated["FileHandlers_BTool.g.cs"]);
    }

    [Theory]
    [InlineData("public static Order Get() => new Order();", "false")]
    [InlineData("public static Order Get() => null!;", "true")]
    public void Endpoint_WithAMethodHandler_GetsAStrictOutputSchema_OnlyWhenItsBodyAlwaysReturnsAValue(string handler, string isOutputNullable)
    {
        var source = MinimalApiUsings + MinimalApiModels + $@"
namespace TestApi
{{
    public static class Endpoints
    {{
        public static void Map(WebApplication app) => app.MapGet(""/orders"", Handlers.Get);
    }}

    public static class Handlers
    {{
        {handler}
    }}
}}
";

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Handlers_GetTool");

        Assert.Contains("UseStructuredContent = true, OutputSchemaType = typeof(global::TestApi.Order),", tool);
        Assert.Contains($"McpGeneratedTool(IsOutputNullable = {isOutputNullable})", tool);
    }

    /// <summary>
    /// The method declarations, by documentation ID, whose bodies the plan asks the analysis to read.
    /// </summary>
    private static string[] GetBodiesToAnalyze(GeneratorDriver driver)
    {
        var output = Assert.Single(GetStepOutputs(driver, TrackingNames.BodiesToAnalyze));

        return ((EquatableArray<BodyToAnalyzeModel>)output.Value).Select(x => x.MethodId).ToArray();
    }

    /// <summary>
    /// The method declarations, by documentation ID, whose bodies the analysis step proved to never succeed without a body,
    /// and why that step ran.
    /// </summary>
    private static (string[] Bodies, IncrementalStepRunReason Reason) GetBodiesNeverEmpty(GeneratorDriver driver)
    {
        var output = Assert.Single(GetStepOutputs(driver, TrackingNames.BodiesNeverEmpty));

        return (((EquatableArray<BodyToAnalyzeModel>)output.Value).Select(x => x.MethodId).ToArray(), output.Reason);
    }
}
