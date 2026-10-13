using System;
using System.Linq;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Output schemas: which response type a generated tool advertises, and when it advertises none. These run against
/// the real ASP.NET Core types, not the stubs, so attributes such as <c>[ProducesResponseType]</c> resolve as in an app.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string StructuredOutputUsings = @"#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
";

    private const string StructuredOutputModels = @"
namespace TestApi
{
    public sealed class Product
    {
        public string Name { get; set; } = """";
    }

    public sealed class Order
    {
    }

#nullable disable
    public static class LegacyRepository
    {
        public static Product Current;

        public static Product CurrentProperty => null;

        public static Product Find() => null;

        public static Task<Product> FindAsync() => Task.FromResult<Product>(null);
    }
#nullable enable
}
";

    private const string ToolClassName = "ProductsController_GetTool";

    [Theory]
    [InlineData("public ActionResult<Product> Get() => new Product();", "global::TestApi.Product")]
    [InlineData("public Task<ActionResult<Product>> Get() => Task.FromResult<ActionResult<Product>>(new Product());", "global::TestApi.Product")]
    [InlineData("public Task<Product> Get() => Task.FromResult(new Product());", "global::TestApi.Product")]
    [InlineData("public ValueTask<Product> Get() => new ValueTask<Product>(new Product());", "global::TestApi.Product")]
    [InlineData("public Product Get() => new Product();", "global::TestApi.Product")]
    [InlineData("public ActionResult<Product?> Get() => new Product();", "global::TestApi.Product")]
    [InlineData("public ActionResult<List<Product>> Get() => new List<Product>();", "global::System.Collections.Generic.List<global::TestApi.Product>")]
    [InlineData("public Task<int> Get() => Task.FromResult(1);", "int")]
    [InlineData("public Task<int?> Get() => Task.FromResult<int?>(1);", "int?")]
    [InlineData("[ProducesResponseType(typeof(Product), 200)] public IActionResult Get() => Ok(new Product());", "global::TestApi.Product")]
    [InlineData("[ProducesResponseType(StatusCodes.Status200OK, Type = typeof(Product))] public IActionResult Get() => Ok(new Product());", "global::TestApi.Product")]
    [InlineData("[ProducesResponseType<Product>(StatusCodes.Status201Created)] public IActionResult Get() => Ok(new Product());", "global::TestApi.Product")]
    [InlineData("[ProducesResponseType(StatusCodes.Status200OK)] public ActionResult<Product> Get() => new Product();", "global::TestApi.Product")]
    [InlineData("[ProducesResponseType(typeof(ProblemDetails), 404)] public ActionResult<Product> Get() => new Product();", "global::TestApi.Product")]
    [InlineData("[ProducesResponseType(typeof(Product), 200)] [ProducesResponseType(typeof(Product), 201)] public IActionResult Get() => Ok(new Product());", "global::TestApi.Product")]
    [InlineData("[Produces(\"application/json\")] public ActionResult<Product> Get() => new Product();", "global::TestApi.Product")]
    [InlineData("[Produces(\"application/json\", \"application/problem+json\")] public ActionResult<Product> Get() => new Product();", "global::TestApi.Product")]
    public void Generator_AdvertisesTheSuccessResponseType_AsTheOutputSchema(string action, string expectedType)
    {
        var tool = GenerateCompilingToolClass(StructuredOutputSource(action), AllExceptExcludedConfig);

        Assert.Contains($"[McpServerTool(Name = \"Products_Get\", UseStructuredContent = true, OutputSchemaType = typeof({expectedType}), ReadOnly = true, ", tool);
        Assert.Contains("[global::Api.ToMcp.Runtime.McpGeneratedTool(IsOutputNullable = ", tool);
        Assert.Contains("public static async Task<object> InvokeAsync(", tool);
        Assert.Contains("return global::Api.ToMcp.Runtime.Services.McpStructuredOutput.FromResult(await invoker.GetAsync(route, cancellationToken));", tool);
    }

    [Theory]
    [InlineData("public Task Get() => Task.CompletedTask;")]
    [InlineData("public void Get() { }")]
    [InlineData("public IActionResult Get() => Ok();")]
    [InlineData("public ActionResult Get() => Ok();")]
    [InlineData("public Task<IActionResult> Get() => Task.FromResult<IActionResult>(Ok());")]
    [InlineData("public ActionResult<string> Get() => \"text\";")]
    [InlineData("public Task<string> Get() => Task.FromResult(\"text\");")]
    [InlineData("public object Get() => new object();")]
    [InlineData("public dynamic Get() => 1;")]
    [InlineData("public System.Text.Json.JsonElement Get() => default;")]
    [InlineData("public System.Text.Json.Nodes.JsonObject Get() => new System.Text.Json.Nodes.JsonObject();")]
    [InlineData("public System.IO.Stream Get() => System.IO.Stream.Null;")]
    [InlineData("public FileContentResult Get() => File(new byte[0], \"text/plain\");")]
    [InlineData("public IAsyncEnumerable<Product> Get() => throw new NotImplementedException();")]
    [InlineData("public IResult Get() => Results.Ok();")]
    [InlineData("[ProducesResponseType(typeof(Product), 200)] [ProducesResponseType(typeof(Order), 201)] public IActionResult Get() => Ok();")]
    [InlineData("[ProducesResponseType(typeof(Product), 200, \"text/csv\")] public IActionResult Get() => Ok();")]
    [InlineData("[Produces(\"text/csv\")] public ActionResult<Product> Get() => new Product();")]
    [InlineData("[Produces(\"application/json\", \"text/csv\")] public ActionResult<Product> Get() => new Product();")]
    [InlineData("[ProducesResponseType(typeof(Product), 200, \"application/json\", \"text/csv\")] public IActionResult Get() => Ok();")]
    [InlineData("[ProducesResponseType(StatusCodes.Status204NoContent)] public ActionResult<Product> Get() => Ok(new Product());")]
    [InlineData("[ProducesResponseType(typeof(Hidden), 200)] public IActionResult Get() => Ok(); private sealed class Hidden { }")]
    [InlineData("[ProducesResponseType(typeof(List<Hidden>), 200)] public IActionResult Get() => Ok(); private sealed class Hidden { }")]
    public void Generator_AdvertisesNoOutputSchema_WhenTheSuccessBodyIsNotReliablyTypedJson(string action)
    {
        var tool = GenerateCompilingToolClass(StructuredOutputSource(action), AllExceptExcludedConfig);

        AssertHasNoOutputSchema(tool);
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_ForAResponseTypeBuiltFromATypeParameter()
    {
        // A generic base controller is scanned as a controller of its own; its T means nothing in the generated tool.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class CrudController<T> : ControllerBase where T : class
    {
        [HttpGet(""list"")]
        public ActionResult<List<T>> GetList() => new List<T>();

        [HttpGet(""array"")]
        public ActionResult<T[]> GetArray() => new T[0];

        [HttpGet(""nested"")]
        public ActionResult<Dictionary<string, List<T>>> GetNested() => new Dictionary<string, List<T>>();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : CrudController<Product>
    {
    }
}
";
        var errors = CompileWithGeneratedTools(source, AllExceptExcludedConfig);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));

        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.DoesNotContain("UseStructuredContent", GetToolClass(generated, "CrudController_GetListTool"));
        Assert.DoesNotContain("UseStructuredContent", GetToolClass(generated, "CrudController_GetArrayTool"));
        Assert.DoesNotContain("UseStructuredContent", GetToolClass(generated, "CrudController_GetNestedTool"));
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_WhenABaseControllerProducesANonJsonContentType()
    {
        // [Produces] is inherited, and MVC applies a base controller's to every action.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [Produces(""text/csv"")]
    public abstract class CsvControllerBase : ControllerBase
    {
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : CsvControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();
    }
}
";

        AssertHasNoOutputSchema(GenerateCompilingToolClass(source, AllExceptExcludedConfig));
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_WhenTheOverriddenActionProducesANonJsonContentType()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class ReportControllerBase : ControllerBase
    {
        [Produces(""text/csv"")]
        public virtual ActionResult<Product> Get() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ReportControllerBase
    {
        [HttpGet]
        public override ActionResult<Product> Get() => new Product();
    }
}
";

        AssertHasNoOutputSchema(GenerateCompilingToolClass(source, AllExceptExcludedConfig));
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_WhenTheControllerProducesANonJsonContentType()
    {
        var source = StructuredOutputSource("public ActionResult<Product> Get() => new Product();", controllerAttributes: "[Produces(\"text/csv\")]");

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        AssertHasNoOutputSchema(tool);
    }

    [Fact]
    public void Generator_LetsTheActionsProducesWin_OverTheControllers()
    {
        var source = StructuredOutputSource(
            "[Produces(\"application/json\")] public ActionResult<Product> Get() => new Product();",
            controllerAttributes: "[Produces(\"text/csv\")]");

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Product)", tool);
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_WhenTheControllerDeclaresAnotherSuccessType()
    {
        // MVC applies a controller's [ProducesResponseType] to every action, so a 201 Order is a success too.
        var source = StructuredOutputSource(
            "[ProducesResponseType(typeof(Product), 200)] public IActionResult Get() => Ok(new Product());",
            controllerAttributes: "[ProducesResponseType(typeof(Order), 201)]");

        AssertHasNoOutputSchema(GenerateCompilingToolClass(source, AllExceptExcludedConfig));
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_WhenTheControllerDeclaresASuccessTypeTheActionDoesNotReturn()
    {
        var source = StructuredOutputSource(
            "public ActionResult<Product> Get() => new Product();",
            controllerAttributes: "[ProducesResponseType(typeof(Order), 200)]");

        AssertHasNoOutputSchema(GenerateCompilingToolClass(source, AllExceptExcludedConfig));
    }

    [Fact]
    public void Generator_LetsTheActionsResponseTypeWin_OverTheControllersForTheSameStatus()
    {
        var source = StructuredOutputSource(
            "[ProducesResponseType(typeof(Product), 200)] public IActionResult Get() => Ok(new Product());",
            controllerAttributes: "[ProducesResponseType(typeof(Order), 200)]");

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Product)", tool);
    }

    [Theory]
    [InlineData("public IActionResult Get() => Ok(new Product());")]
    [InlineData("public ActionResult Get() => Ok(new Product());")]
    [InlineData("public Task<IActionResult> Get() => Task.FromResult<IActionResult>(Ok(new Product()));")]
    public void Generator_TakesTheControllersResponseType_ForAnActionWhoseReturnTypeNamesNoBody(string action)
    {
        // IActionResult says nothing about the body, so the controller's declaration is the only type there is.
        var source = StructuredOutputSource(action, controllerAttributes: "[ProducesResponseType<Product>(StatusCodes.Status200OK)]");

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Product)", tool);
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_WhenTheControllerDeclaresATypeForAnActionReturningAFile()
    {
        var source = StructuredOutputSource(
            "public FileContentResult Get() => File(new byte[0], \"text/plain\");",
            controllerAttributes: "[ProducesResponseType<Product>(StatusCodes.Status200OK)]");

        AssertHasNoOutputSchema(GenerateCompilingToolClass(source, AllExceptExcludedConfig));
    }

    [Fact]
    public void Generator_IgnoresTheControllersErrorResponseTypes()
    {
        var source = StructuredOutputSource(
            "public ActionResult<Product> Get() => new Product();",
            controllerAttributes: "[ProducesResponseType(typeof(ProblemDetails), 400)]");

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Product)", tool);
        Assert.Contains("IsOutputNullable = false", tool);
    }

    [Fact]
    public void Generator_MakesTheOutputSchemaNullable_WhenTheControllerDeclaresANoContentSuccess()
    {
        var source = StructuredOutputSource(
            "public ActionResult<Product> Get() => new Product();",
            controllerAttributes: "[ProducesResponseType(StatusCodes.Status204NoContent)]");

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Product)", tool);
        Assert.Contains("IsOutputNullable = true", tool);
    }

    [Fact]
    public void Generator_ReadsTheResponseTypeOfTheOverriddenAction()
    {
        // [ProducesResponseType] is inherited: MVC reads the base action's for an override that declares none.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class CatalogControllerBase : ControllerBase
    {
        [ProducesResponseType<Product>(StatusCodes.Status200OK)]
        public virtual IActionResult Get() => Ok(new Product());
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : CatalogControllerBase
    {
        [HttpGet]
        public override IActionResult Get() => Ok(new Product());
    }
}
";

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Product)", tool);
    }

    [Fact]
    public void Generator_ReadsTheResponseTypesOfABaseController()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ProducesResponseType(typeof(Order), StatusCodes.Status201Created)]
    public abstract class OrderingControllerBase : ControllerBase
    {
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : OrderingControllerBase
    {
        [HttpGet]
        public ActionResult<Product> Get() => new Product();
    }
}
";

        AssertHasNoOutputSchema(GenerateCompilingToolClass(source, AllExceptExcludedConfig));
    }

    [Fact]
    public void Generator_AdvertisesNoOutputSchema_WhenStructuredOutputIsOffInTheConfig()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""structuredOutput"": false }";

        var tool = GenerateCompilingToolClass(StructuredOutputSource("public ActionResult<Product> Get() => new Product();"), config);

        AssertHasNoOutputSchema(tool);
    }

    [Fact]
    public void Generator_EmitsStructuredOutputToolsThatCompile_AgainstTheRealRuntimeAndAspNetCore()
    {
        const string actions = @"
        [HttpGet(""nullable"")]
        public ActionResult<Product?> GetNullable() => new Product();

        [HttpGet(""nested"")]
        public ActionResult<Dictionary<string, List<Order>>> GetNested() => new Dictionary<string, List<Order>>();

        [HttpGet(""number"")]
        public Task<int> GetNumber() => Task.FromResult(1);

        [HttpGet(""generic-attribute"")]
        [ProducesResponseType<Product>(StatusCodes.Status200OK)]
        public IActionResult GetFromAttribute() => Ok(new Product());

        [HttpPost(""create"")]
        public ActionResult<Product> Create([FromBody] Product product) => product;

        [HttpPut(""{id}"")]
        public ActionResult<Product> Update(Guid id, [FromBody] Product product) => product;

        [HttpPatch(""{id}"")]
        public ActionResult<Product> Patch(Guid id) => new Product();

        [HttpDelete(""{id}"")]
        public ActionResult<Order> Delete(Guid id) => new Order();
";
        var errors = CompileWithGeneratedTools(StructuredOutputSource(actions, isRawBody: true), AllExceptExcludedConfig);

        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
    }

    private static void AssertHasNoOutputSchema(string tool)
    {
        Assert.Contains("[McpServerTool(Name = \"Products_Get\", ReadOnly = true, ", tool);
        Assert.Contains("public static async Task<CallToolResult> InvokeAsync(", tool);
        Assert.Contains("return await invoker.GetAsync(route, cancellationToken);", tool);
        Assert.DoesNotContain("UseStructuredContent", tool);
        Assert.DoesNotContain("McpGeneratedTool", tool);
        Assert.DoesNotContain("McpStructuredOutput", tool);
    }

    private static string StructuredOutputSource(string action, string controllerAttributes = "", bool isRawBody = false)
    {
        var body = isRawBody ? action : $"[HttpGet] {action}";

        return StructuredOutputUsings + StructuredOutputModels + $@"
namespace TestApi
{{
    {controllerAttributes}
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {{
        {body}
    }}
}}
";
    }

    /// <summary>
    /// The emitted tool class, after checking that the source and every generated tool compile: a test source with an
    /// error would otherwise pass a "no output schema" case for the wrong reason.
    /// </summary>
    private static string GenerateCompilingToolClass(string source, string generatorJson)
    {
        var errors = CompileWithGeneratedTools(source, generatorJson);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));

        return GetToolClass(RunGenerator(source, generatorJson), ToolClassName);
    }

    private static string GetToolClass(string generated, string toolClassName)
    {
        var start = generated.IndexOf($"class {toolClassName}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No tool class {toolClassName} in:\n{generated}");
        var end = generated.IndexOf("// <auto-generated />", start, StringComparison.Ordinal);

        return end < 0 ? generated.Substring(start) : generated.Substring(start, end - start);
    }
}
