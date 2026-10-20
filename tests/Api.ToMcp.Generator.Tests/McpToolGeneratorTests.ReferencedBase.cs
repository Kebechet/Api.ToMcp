using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// A base controller from a referenced assembly, such as a shared package, read from its metadata and XML docs alone.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string ReferencedCrudController = @"
using System;
using System.Collections.Generic;
using Api.ToMcp.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Shared
{
    [ApiController]
    [Route(""api/[controller]"")]
    public abstract class CrudController<T> : ControllerBase where T : class, new()
    {
        /// <summary>Lists every item.</summary>
        [HttpGet]
        public ActionResult<List<T>> GetAll() => new List<T>();

        /// <summary>Gets an item.</summary>
        /// <param name=""id"">The item's identifier.</param>
        [McpExpose(ToolName = ""get_item"")]
        [HttpGet(""{id}"")]
        public virtual ActionResult<T> Get(Guid id) => new T();
    }
}
";

    [Fact]
    public void Generator_GivesControllersDerivedFromABaseInAReferencedAssembly_TheirInheritedTools()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public class ProductsController : Shared.CrudController<Product>
    {
    }

    public class OrdersController : Shared.CrudController<Order>
    {
        public override ActionResult<Order> Get(Guid id) => new Order();
    }
}
";
        var compilation = CreateCompilation(source).AddReferences(CompileToReference(ReferencedCrudController, "Shared"));

        CreateDriver(AllExceptExcludedConfig).RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var errors = diagnostics.Concat(outputCompilation.GetDiagnostics()).Where(x => x.Severity == DiagnosticSeverity.Error);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));

        var generated = string.Join(
            "\n",
            CreateDriver(AllExceptExcludedConfig).RunGenerators(compilation).GetRunResult().GeneratedTrees.Select(x => x.GetText().ToString()));

        var getAll = GetToolClass(generated, "ProductsController_GetAllTool");
        Assert.Contains("OutputSchemaType = typeof(global::System.Collections.Generic.List<global::TestApi.Product>)", getAll);
        Assert.Contains("[Description(\"Lists every item.\")]", getAll);
        // Metadata has no body to prove a value from.
        Assert.Contains(NullableOutput, getAll);
        Assert.Equal("var route = $\"/api/products\";", GetRouteLine(generated, "ProductsController_GetAllTool"));

        var get = GetToolClass(generated, "ProductsController_GetTool");
        Assert.Contains("[McpServerTool(Name = \"get_item\", ", get);
        Assert.Contains("[Description(\"The item's identifier.\")] System.Guid id", get);
        Assert.Equal("var route = $\"/api/products/{routeid}\";", GetRouteLine(generated, "ProductsController_GetTool"));

        // The override in source inherits the name and docs of the base action in metadata.
        var overriddenGet = GetToolClass(generated, "OrdersController_GetTool");
        Assert.Contains("[McpServerTool(Name = \"get_item\", ", overriddenGet);
        Assert.Contains("OutputSchemaType = typeof(global::TestApi.Order)", overriddenGet);
        Assert.Contains("[Description(\"Gets an item.\")]", overriddenGet);
        Assert.Equal("var route = $\"/api/orders/{routeid}\";", GetRouteLine(generated, "OrdersController_GetTool"));

        Assert.DoesNotMatch(@"\bCrudController_", generated);

        // The inherited name collides. Of the two actions, only the override is declared in this compilation.
        var warning = Assert.Single(diagnostics, x => x.Id == DuplicateToolNameId);
        Assert.Contains("ProductsController.Get (GET /api/products/{id}), OrdersController.Get (GET /api/orders/{id})", warning.GetMessage());
        Assert.Equal("Get", GetLocatedText(warning.Location));
        Assert.Equal(source.IndexOf("Get(Guid id)", StringComparison.Ordinal), warning.Location.SourceSpan.Start);
        Assert.Empty(warning.AdditionalLocations);
    }

    [Fact]
    public void Generator_ReportsADuplicateToolName_WithoutALocation_WhenOnlyAReferencedAssemblyDeclaresIt()
    {
        const string source = StructuredOutputUsings + StructuredOutputModels + @"
namespace TestApi
{
    public class ProductsController : Shared.CrudController<Product>
    {
    }

    public class OrdersController : Shared.CrudController<Order>
    {
    }
}
";
        var compilation = CreateCompilation(source).AddReferences(CompileToReference(ReferencedCrudController, "Shared"));

        var diagnostics = CreateDriver(AllExceptExcludedConfig).RunGenerators(compilation).GetRunResult().Diagnostics;

        var warning = Assert.Single(diagnostics, x => x.Id == DuplicateToolNameId);
        Assert.Contains("Tool name 'get_item' is given to 2 tools: ProductsController.Get (GET /api/products/{id}), OrdersController.Get (GET /api/orders/{id}).", warning.GetMessage());
        Assert.Equal(Location.None, warning.Location);
        Assert.Empty(warning.AdditionalLocations);
    }

    /// <summary>
    /// The source compiled into an assembly that the test compilation references by its image and XML docs, as a package
    /// or a project reference is.
    /// </summary>
    private static MetadataReference CompileToReference(string source, string assemblyName)
    {
        var compilation = CreateCompilation(source).WithAssemblyName(assemblyName);

        using var image = new MemoryStream();
        using var documentation = new MemoryStream();
        var result = compilation.Emit(image, xmlDocumentationStream: documentation);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

        return MetadataReference.CreateFromImage(image.ToArray(), documentation: new InMemoryDocumentationProvider(documentation.ToArray()));
    }

    /// <summary>
    /// An assembly's XML documentation file, served to Roslyn as the compiler reads the one next to a referenced assembly.
    /// </summary>
    private sealed class InMemoryDocumentationProvider : DocumentationProvider
    {
        private const string MemberElementName = "member";
        private const string MemberNameAttributeName = "name";

        private readonly Dictionary<string, string> _membersById;

        public InMemoryDocumentationProvider(byte[] documentationFile)
        {
            _membersById = XDocument.Load(new MemoryStream(documentationFile))
                .Descendants(MemberElementName)
                .ToDictionary(x => (string)x.Attribute(MemberNameAttributeName)!, x => x.ToString());
        }

        protected override string? GetDocumentationForSymbol(string documentationMemberID, CultureInfo preferredCulture, CancellationToken cancellationToken = default)
        {
            return _membersById.TryGetValue(documentationMemberID, out var documentation) ? documentation : null;
        }

        public override bool Equals(object? obj)
        {
            return ReferenceEquals(this, obj);
        }

        public override int GetHashCode()
        {
            return RuntimeHelpers.GetHashCode(this);
        }
    }
}
