using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// An include or exclude entry that names no controller or action selects or excludes nothing, without a word. A typo
/// is one cause; the other is a base controller's name, which matched the base's own tools before inherited actions
/// were generated under each concrete controller (https://github.com/Kebechet/Api.ToMcp/pull/43), so an exclude entry
/// for it now lets the base's actions through. The generator warns at each such entry (MCP008). See
/// https://github.com/Kebechet/Api.ToMcp/issues/46.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string UnmatchedConfigEntryId = "MCP008";

    private const string NamesNothing = "in generator.json names no controller or action of the project";

    private const string ControllersKeptFromMcp = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        public Task<string> GetById(Guid id) => Task.FromResult(string.Empty);

        [McpIgnore]
        [HttpDelete(""{id}"")]
        public Task<string> Delete(Guid id) => Task.FromResult(string.Empty);

        [HttpGet(""/mcp/products"")]
        public Task<string> ListForMcp() => Task.FromResult(string.Empty);
    }

    [McpIgnore]
    [Route(""api/[controller]"")]
    public class AdminController : ControllerBase
    {
        [HttpPost(""reset"")]
        public Task<string> Reset() => Task.FromResult(string.Empty);
    }

    [Route(""api/[controller]"")]
    public class HealthController : ControllerBase
    {
        public string Describe() => string.Empty;
    }
}
";

    [Theory]
    [InlineData("Prodcts", "Products")]
    [InlineData("products", "Products")]
    [InlineData("ProductController", "ProductsController")]
    [InlineData("ProductsController.getById", "ProductsController.GetById")]
    [InlineData("Products.GetByID", "Products.GetById")]
    [InlineData("Products_GetById", "Products.GetById")]
    public void Generator_WarnsAtAnEntryThatNamesNothing_WithTheNearestName(string entry, string nearestName)
    {
        var config = $@"{{
  ""mode"": ""SelectedOnly"",
  ""include"": [
    ""Products.GetAll"",
    ""{entry}""
  ]
}}";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, config));

        Assert.Equal(UnmatchedConfigEntryId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            $"The include entry '{entry}' {NamesNothing}, so it selects nothing: entries are case-sensitive, and the nearest name in the project is '{nearestName}'",
            diagnostic.GetMessage());
        Assert.Equal("Mcp/generator.json", diagnostic.Location.GetLineSpan().Path);
        // At the entry's string, quotes included.
        Assert.Equal(
            new LinePositionSpan(new LinePosition(4, 4), new LinePosition(4, 4 + entry.Length + 2)),
            diagnostic.Location.GetLineSpan().Span);
    }

    [Fact]
    public void Generator_WarnsAtAnEntryThatNamesNothing_WithExamples_WhenNoNameIsNear()
    {
        const string config = @"{ ""mode"": ""SelectedOnly"", ""include"": [""Products.GetAll"", ""Inventory""] }";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, config));

        Assert.Equal(UnmatchedConfigEntryId, diagnostic.Id);
        Assert.Equal(
            $"The include entry 'Inventory' {NamesNothing}, so it selects nothing: entries are case-sensitive names of controllers, such as 'ProductsController', or of their actions, such as 'ProductsController.GetById'",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("SelectedOnly", "include", "selects")]
    [InlineData("SelectedOnly", "exclude", "excludes")]
    [InlineData("AllExceptExcluded", "include", "selects")]
    [InlineData("AllExceptExcluded", "exclude", "excludes")]
    public void Generator_WarnsAboutTheEntriesOfBothLists_WhicheverTheModeReads(string mode, string list, string verb)
    {
        // The list the mode does not read takes effect once the mode changes, so a dead entry in it is worth fixing too.
        var include = list == "include" ? @"""Products.GetAll"", ""Prodcts""" : @"""Products.GetAll""";
        var exclude = list == "exclude" ? @"""Prodcts""" : "";
        var config = $@"{{ ""mode"": ""{mode}"", ""include"": [{include}], ""exclude"": [{exclude}] }}";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(Usings + ProductsController, config));

        Assert.Equal(UnmatchedConfigEntryId, diagnostic.Id);
        Assert.Equal(
            $"The {list} entry 'Prodcts' {NamesNothing}, so it {verb} nothing: entries are case-sensitive, and the nearest name in the project is 'Products'",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("CrudController", "list the controllers that inherit from it, 'ProductCrudController' and 'OrderCrudController', or their actions, such as 'ProductCrudController.GetAll'")]
    [InlineData("Crud", "list the controllers that inherit from it, 'ProductCrud' and 'OrderCrud', or their actions, such as 'ProductCrud.GetAll'")]
    [InlineData("Crud.Create", "list 'ProductCrud.Create' and 'OrderCrud.Create' instead")]
    [InlineData("CrudController.Create", "list 'ProductCrudController.Create' and 'OrderCrudController.Create' instead")]
    public void Generator_WarnsAtAnEntryThatNamesABaseController_WithTheControllersThatInheritFromIt(string entry, string advice)
    {
        // Upgrading past https://github.com/Kebechet/Api.ToMcp/pull/43, this exclude entry silently stopped hiding
        // Create, which every controller deriving from the base now exposes.
        var config = $@"{{ ""mode"": ""AllExceptExcluded"", ""exclude"": [""{entry}""] }}";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(CrudControllers, config));

        Assert.Equal(UnmatchedConfigEntryId, diagnostic.Id);
        Assert.Equal(
            $"The exclude entry '{entry}' {NamesNothing}, so it excludes nothing: CrudController is a base class, and an inherited action is matched by the name of the controller that inherits it; {advice}",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("Crud", "CrudController", "Create")]
    [InlineData("Read", "ReadController", "GetAll")]
    public void Generator_WarnsAtAnEntryThatNamesABaseController_WithAnActionInheritedFromItOrItsBases(
        string entry,
        string baseClass,
        string exampleAction)
    {
        // The example is the controller's first action declared by the base the entry names or a class it derives from:
        // Count is the controller's own, Create is CrudController's, and GetAll is ReadController's.
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public abstract class ReadController<T> : ControllerBase where T : class, new()
    {
        [HttpGet]
        public ActionResult<List<T>> GetAll() => new List<T>();
    }

    public abstract class CrudController<T> : ReadController<T> where T : class, new()
    {
        [HttpPost]
        public ActionResult<T> Create([FromBody] T item) => item;
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : CrudController<Product>
    {
        [HttpGet(""count"")]
        public ActionResult<int> Count() => 0;
    }
}
";
        var config = $@"{{ ""mode"": ""AllExceptExcluded"", ""exclude"": [""{entry}""] }}";

        var diagnostic = Assert.Single(GetGeneratorDiagnostics(source, config));

        Assert.Equal(UnmatchedConfigEntryId, diagnostic.Id);
        Assert.Equal(
            $"The exclude entry '{entry}' {NamesNothing}, so it excludes nothing: {baseClass} is a base class, and an inherited action is matched by the name of the controller that inherits it; list the controllers that inherit from it, 'Products', or their actions, such as 'Products.{exampleAction}'",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("ProductsController")]
    [InlineData("Products")]
    [InlineData("ProductsController.GetById")]
    [InlineData("Products.GetById")]
    [InlineData("Products.Delete")]
    [InlineData("Products.ListForMcp")]
    [InlineData("Admin")]
    [InlineData("AdminController.Reset")]
    [InlineData("Health")]
    public void Generator_DoesNotWarnAboutAnEntry_ThatNamesAControllerOrAction(string entry)
    {
        // [McpIgnore], an /mcp route or a controller without [Http*] actions keeps a tool from being generated, but the
        // entry is spelled right and names what it means to: the attribute or route decides, as documented.
        var include = $@"{{ ""mode"": ""SelectedOnly"", ""include"": [""Products.GetById"", ""{entry}""] }}";
        var exclude = $@"{{ ""mode"": ""AllExceptExcluded"", ""exclude"": [""{entry}""] }}";

        Assert.Empty(GetGeneratorDiagnostics(Usings + ControllersKeptFromMcp, include));
        Assert.DoesNotContain(GetGeneratorDiagnostics(Usings + ControllersKeptFromMcp, exclude), x => x.Id == UnmatchedConfigEntryId);
    }

    private const string ActionsWithoutAToolMethod = @"
namespace TestApi
{
    public abstract class CatalogControllerBase : ControllerBase
    {
        [HttpHead(""base-head"")]
        public IActionResult BaseHead() => Ok();
    }

    [Route(""api/[controller]"")]
    public class ProductsController : CatalogControllerBase
    {
        [HttpGet(""{id}"")]
        public IActionResult GetById(int id) => Ok();

        [HttpHead(""head"")]
        public IActionResult HeadOnly() => Ok();

        [Route(""any"")]
        public IActionResult Any() => Ok();

        public IActionResult Describe() => Ok();

        [NonAction]
        public IActionResult Helper() => Ok();
    }
}
";

    [Theory]
    [InlineData("Products.HeadOnly")]
    [InlineData("Products.Any")]
    [InlineData("ProductsController.Describe")]
    [InlineData("Products.BaseHead")]
    public void Generator_DoesNotWarnAboutAnEntry_ThatNamesAnActionWithoutAnHttpMethodAToolSends(string entry)
    {
        // MVC serves these actions for HEAD, for every method or by conventional routing, so they are actions, which no
        // tool calls: the entry names what it means to.
        var config = $@"{{ ""mode"": ""AllExceptExcluded"", ""exclude"": [""{entry}""] }}";

        Assert.DoesNotContain(GetGeneratorDiagnostics(Usings + ActionsWithoutAToolMethod, config), x => x.Id == UnmatchedConfigEntryId);
    }

    [Fact]
    public void Generator_WarnsAtAnEntryThatNamesAMethodThatIsNoAction()
    {
        const string config = @"{ ""mode"": ""AllExceptExcluded"", ""exclude"": [""Products.Helper""] }";

        Assert.Single(GetGeneratorDiagnostics(Usings + ActionsWithoutAToolMethod, config), x => x.Id == UnmatchedConfigEntryId);
    }

    [Theory]
    [InlineData("ProductCrudController")]
    [InlineData("ProductCrud.GetAll")]
    [InlineData("OrderCrudController.Count")]
    public void Generator_DoesNotWarnAboutAnEntry_ThatNamesAnInheritedActionByItsController(string entry)
    {
        var config = $@"{{ ""mode"": ""AllExceptExcluded"", ""exclude"": [""{entry}""] }}";

        Assert.Empty(GetGeneratorDiagnostics(CrudControllers, config));
    }

    [Fact]
    public void Generator_WarnsAtEachEntryThatNamesNothing_AtItsOwnPositionInCharacters()
    {
        // Two accented letters of the first entry take two UTF-8 bytes each, and the same entry appears twice.
        const string config = "{\r\n  \"include\": [\"Products.GetAll\", \"Příliš\", \"Prodcts\", \"Prodcts\"]\r\n}";
        var line = config.Split('\n')[1];

        var diagnostics = GetGeneratorDiagnostics(Usings + ProductsController, config)
            .OrderBy(x => x.Location.GetLineSpan().StartLinePosition)
            .ToList();

        Assert.All(diagnostics, x => Assert.Equal(UnmatchedConfigEntryId, x.Id));
        Assert.Equal(
            new[]
            {
                new LinePosition(1, line.IndexOf("\"Př")),
                new LinePosition(1, line.IndexOf("\"Prodcts\"")),
                new LinePosition(1, line.LastIndexOf("\"Prodcts\""))
            },
            diagnostics.Select(x => x.Location.GetLineSpan().StartLinePosition));
        Assert.Equal(new LinePosition(1, line.IndexOf("\"Prodcts\"") + 9), diagnostics[1].Location.GetLineSpan().EndLinePosition);
    }

    [Fact]
    public void Generator_DoesNotWarnAboutANullEntry()
    {
        const string config = @"{ ""mode"": ""SelectedOnly"", ""include"": [null, ""Products.GetAll""] }";

        Assert.Empty(GetGeneratorDiagnostics(Usings + ProductsController, config));
    }

    [Fact]
    public void Generator_WarnsAboutAnEntryThatNamesNothing_AlsoUnderAnUnknownMode()
    {
        // The generator reads the file's lists in SelectedOnly mode then, so their entries still count.
        const string config = @"{ ""mode"": ""Selected"", ""include"": [""Products.GetAll"", ""Prodcts""] }";

        var diagnostics = GetGeneratorDiagnostics(Usings + ProductsController, config);

        Assert.Equal(new[] { InvalidConfigId, UnmatchedConfigEntryId }, diagnostics.Select(x => x.Id).OrderBy(x => x));
    }
}
