using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// The MCP SDK registers tools by name with <c>TryAdd</c>, so of several generated tools with one name it keeps one and
/// drops the others without an error. The generator still generates them all, and warns.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string DuplicateToolNameId = "MCP007";

    [Fact]
    public void Generator_Warns_WhenABaseActionsToolNameNamesTheToolOfEveryControllerThatInheritsIt()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class CrudController<T> : ControllerBase where T : new()
    {
        [Api.ToMcp.Abstractions.McpExpose(ToolName = ""list_items"")]
        [HttpGet]
        public ActionResult<List<T>> GetAll() => new List<T>();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : CrudController<Product>
    {
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : CrudController<Order>
    {
    }
}
";
        var warning = GetSingleDuplicateToolNameWarning(source, AllExceptExcludedConfig);

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(
            "Tool name 'list_items' is given to 2 tools: ProductsController.GetAll (GET /api/products), OrdersController.GetAll (GET /api/orders). " +
            "The MCP SDK keeps only one tool per name and drops the others without an error.",
            warning.GetMessage());
        // Both tools come from the one base action that declares the name.
        Assert.Equal("GetAll", GetLocatedText(warning.Location, source));
        Assert.Empty(warning.AdditionalLocations);

        // The warning only makes the collision visible: both tools are still generated.
        var generated = RunGenerator(source, AllExceptExcludedConfig);
        Assert.Contains("[McpServerTool(Name = \"list_items\", ", GetToolClass(generated, "ProductsController_GetAllTool"));
        Assert.Contains("[McpServerTool(Name = \"list_items\", ", GetToolClass(generated, "OrdersController_GetAllTool"));
    }

    [Fact]
    public void Generator_Warns_WhenTwoActionsSetTheSameToolName()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    using Api.ToMcp.Abstractions;

    [ApiController]
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [McpExpose(ToolName = ""export"")]
        [HttpGet(""export"")]
        public ActionResult<Product> Export() => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class InvoicesController : ControllerBase
    {
        [McpExpose(ToolName = ""export"")]
        [HttpPost(""download"")]
        public ActionResult<Order> Download() => new Order();
    }
}
";
        var warning = GetSingleDuplicateToolNameWarning(source, AllExceptExcludedConfig);

        Assert.Contains(
            "Tool name 'export' is given to 2 tools: ReportsController.Export (GET /api/reports/export), InvoicesController.Download (POST /api/invoices/download).",
            warning.GetMessage());
        Assert.Equal("Export", GetLocatedText(warning.Location, source));
        Assert.Equal("Download", GetLocatedText(Assert.Single(warning.AdditionalLocations), source));
    }

    [Fact]
    public void Generator_Warns_WhenOverloadsGetTheSameDerivedToolName()
    {
        // Overloads get tool classes of their own (ProductsController_GetTool_2), but the same tool name.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<List<Product>> Get() => new List<Product>();

        [HttpGet(""{id}"")]
        public ActionResult<Product> Get(Guid id) => new Product();
    }
}
";
        var warning = GetSingleDuplicateToolNameWarning(source, AllExceptExcludedConfig);

        Assert.Contains(
            "Tool name 'Products_Get' is given to 2 tools: ProductsController.Get (GET /api/products), ProductsController.Get (GET /api/products/{id}).",
            warning.GetMessage());

        var generated = RunGenerator(source, AllExceptExcludedConfig);
        Assert.Contains("class ProductsController_GetTool", generated);
        Assert.Contains("class ProductsController_GetTool_2", generated);
    }

    [Fact]
    public void Generator_Warns_WhenTheNamingFormatGivesTwoActionsTheSameToolName()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""naming"": { ""toolNameFormat"": ""{Action}"" } }";
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
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
        var warning = GetSingleDuplicateToolNameWarning(source, config);

        Assert.Contains("Tool name 'Get' is given to 2 tools: ProductsController.Get (GET /api/products), OrdersController.Get (GET /api/orders).", warning.GetMessage());
    }

    [Fact]
    public void Generator_DoesNotWarn_WhenEveryGeneratedToolHasANameOfItsOwn()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    using Api.ToMcp.Abstractions;

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<List<Product>> Get() => new List<Product>();

        // An overload kept apart by a name of its own.
        [McpExpose(ToolName = ""Products_GetById"")]
        [HttpGet(""{id}"")]
        public ActionResult<Product> Get(Guid id) => new Product();

        // The SDK compares names ordinally, so a name that differs only in case is another tool.
        [McpExpose(ToolName = ""products_get"")]
        [HttpGet(""first"")]
        public ActionResult<Product> GetFirst() => new Product();

        // Only generated tools count, and an ignored action gets none.
        [McpIgnore]
        [HttpGet(""legacy/{code}"")]
        public ActionResult<Product> Get(string code) => new Product();
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet]
        public ActionResult<List<Order>> Get() => new List<Order>();
    }
}
";
        AssertCompiles(source, AllExceptExcludedConfig);

        Assert.DoesNotContain(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == DuplicateToolNameId);
    }

    private const string ActionsWithTheSameToolName = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    using Api.ToMcp.Abstractions;

    [ApiController]
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [McpExpose(ToolName = ""export"")]
        [HttpGet(""csv"")]
        public ActionResult<Product> ExportCsv() => new Product();

        [McpExpose(ToolName = ""export"")]
        [HttpGet(""pdf"")]
        public ActionResult<Product> ExportPdf() => new Product();
    }
}
";

    [Fact]
    public void Generator_LetsAPragmaSuppressTheWarning_InTheFileOfTheActionItPointsAt()
    {
        // The compiler reads #pragma warning disable from the syntax tree of the warning's location, so the warning must
        // point into one.
        Assert.Contains(GetGeneratorDiagnostics(ActionsWithTheSameToolName, AllExceptExcludedConfig), x => x.Id == DuplicateToolNameId && !x.IsSuppressed);

        var diagnostics = GetGeneratorDiagnostics("#pragma warning disable MCP007\n" + ActionsWithTheSameToolName, AllExceptExcludedConfig);

        Assert.DoesNotContain(diagnostics, x => x.Id == DuplicateToolNameId && !x.IsSuppressed);
    }

    [Fact]
    public void Generator_LetsAFileSeveritySuppressTheWarning_InTheFileOfTheActionItPointsAt()
    {
        // The compiler hands an .editorconfig section's severity over per syntax tree, looked up by the warning's location.
        var compilation = CreateCompilation(ActionsWithTheSameToolName);
        var severities = new FileSeverityProvider(compilation.SyntaxTrees.Single(), DuplicateToolNameId, ReportDiagnostic.Suppress);
        compilation = compilation.WithOptions(compilation.Options.WithSyntaxTreeOptionsProvider(severities));

        var diagnostics = CreateDriver(AllExceptExcludedConfig).RunGenerators(compilation).GetRunResult().Diagnostics;

        Assert.DoesNotContain(diagnostics, x => x.Id == DuplicateToolNameId && !x.IsSuppressed);
    }

    private static Diagnostic GetSingleDuplicateToolNameWarning(string source, string generatorJson)
    {
        return Assert.Single(GetGeneratorDiagnostics(source, generatorJson), x => x.Id == DuplicateToolNameId);
    }

    private static ImmutableArray<Diagnostic> GetGeneratorDiagnostics(string source, string generatorJson)
    {
        return CreateDriver(generatorJson)
            .RunGenerators(CreateCompilation(source))
            .GetRunResult()
            .Diagnostics;
    }

    /// <summary>
    /// The text of the source that the location points at, after checking that the line and column it reports, which a
    /// build prints, are those of its span.
    /// </summary>
    private static string GetLocatedText(Location location, string source)
    {
        Assert.NotEqual(Location.None, location);
        var sourceText = SourceText.From(source);
        Assert.Equal(sourceText.Lines.GetLinePositionSpan(location.SourceSpan), location.GetLineSpan().Span);

        return sourceText.ToString(location.SourceSpan);
    }

    /// <summary>
    /// One diagnostic's severity for one file, as an <c>.editorconfig</c> section sets it.
    /// </summary>
    private sealed class FileSeverityProvider : SyntaxTreeOptionsProvider
    {
        private readonly SyntaxTree _tree;
        private readonly string _diagnosticId;
        private readonly ReportDiagnostic _severity;

        public FileSeverityProvider(SyntaxTree tree, string diagnosticId, ReportDiagnostic severity)
        {
            _tree = tree;
            _diagnosticId = diagnosticId;
            _severity = severity;
        }

        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
        {
            return GeneratedKind.Unknown;
        }

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = _severity;
            return tree == _tree && diagnosticId == _diagnosticId;
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = default;
            return false;
        }
    }
}
