using System;
using System.IO;
using System.Linq;
using System.Threading;
using Api.ToMcp.Generator.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Api.ToMcp.Generator.Tests.Parsing;

public class ActionMethodAnalyzerTests
{
    // Leading usings shared by every compiled sample; they must precede all namespaces.
    private const string Usings = @"
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
";

    // Minimal stubs so the analyzer (which matches attributes by simple name) sees the
    // same shapes as real ASP.NET Core code, without referencing the framework.
    private const string AspNetStubs = @"
namespace Microsoft.AspNetCore.Mvc
{
    public class ControllerBase { }
    public class HttpGetAttribute : Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
    public class HttpPostAttribute : Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
    public class RouteAttribute : Attribute { public RouteAttribute(string template) { } }
}
";

    [Fact]
    public void GetActions_UsesMethodLevelRouteAttribute_WhenHttpVerbHasNoTemplate()
    {
        const string controllerSource = @"
namespace TestApi
{
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        [Route(""search"")]
        public Task<string> Search() => Task.FromResult(string.Empty);
    }
}
";
        var controller = GetControllerSymbol(Usings + AspNetStubs + controllerSource, "TestApi.ProductsController");

        var actions = ActionMethodAnalyzer.GetActions(controller, "api/[controller]", CancellationToken.None);

        var search = Assert.Single(actions);
        Assert.Equal("/api/products/search", search.RouteTemplate);
    }

    [Fact]
    public void GetActions_PrefersHttpVerbTemplate_OverMethodLevelRouteAttribute()
    {
        const string controllerSource = @"
namespace TestApi
{
    public class ProductsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        [Route(""ignored"")]
        public Task<string> GetById(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        var controller = GetControllerSymbol(Usings + AspNetStubs + controllerSource, "TestApi.ProductsController");

        var actions = ActionMethodAnalyzer.GetActions(controller, "api/[controller]", CancellationToken.None);

        var getById = Assert.Single(actions);
        Assert.Equal("/api/products/{id}", getById.RouteTemplate);
    }

    private static INamedTypeSymbol GetControllerSymbol(string source, string metadataName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, "Compilation errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));

        var symbol = compilation.GetTypeByMetadataName(metadataName);
        Assert.NotNull(symbol);
        return symbol!;
    }
}
