using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// A partial controller is one controller, however many files declare it.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Fact]
    public void Generator_GivesAPartialControllerSplitOverTwoFiles_OneSetOfTools()
    {
        const string firstPart = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public partial class PartsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<List<Product>> GetAll() => new List<Product>();
    }
}
";
        const string secondPart = StructuredOutputUsings + @"
namespace TestApi
{
    public partial class PartsController
    {
        [HttpGet(""{id}"")]
        public ActionResult<Product> GetById(Guid id) => new Product();
    }
}
";
        var compilation = CreateCompilation(firstPart, secondPart);

        CreateDriver(AllExceptExcludedConfig).RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var errors = diagnostics.Concat(outputCompilation.GetDiagnostics()).Where(x => x.Severity == DiagnosticSeverity.Error);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
        Assert.DoesNotContain(diagnostics, x => x.Id == DuplicateToolNameId);

        var generated = string.Join(
            "\n",
            CreateDriver(AllExceptExcludedConfig).RunGenerators(compilation).GetRunResult().GeneratedTrees.Select(x => x.GetText().ToString()));

        Assert.Equal(1, CountToolClasses(generated, "PartsController_GetAllTool"));
        Assert.Equal(1, CountToolClasses(generated, "PartsController_GetByIdTool"));
        Assert.DoesNotMatch(@"Tool_\d", generated);
        Assert.Single(generated.Split('\n'), x => x.Contains("typeof(PartsController_GetAllTool)"));
    }
}
