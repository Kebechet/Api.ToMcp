using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Api.ToMcp.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// End-to-end tests that run the real <see cref="McpToolGenerator"/> over sample source via a
/// generator driver and assert on the emitted code, exercising the whole pipeline
/// (config parsing, controller scanning, action analysis, and emission) together.
/// </summary>
public class McpToolGeneratorTests
{
    private const string Usings = @"
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
";

    private const string AspNetStubs = @"
namespace Microsoft.AspNetCore.Mvc
{
    public class ControllerBase { }
    public class ApiControllerAttribute : Attribute { }
    public class HttpGetAttribute : Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
    public class HttpPostAttribute : Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
    public class RouteAttribute : Attribute { public RouteAttribute(string template) { } }
}
";

    private const string AllExceptExcludedConfig =
        @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [] }";

    [Fact]
    public void Generator_EmitsToolAndRegistration_ForController()
    {
        const string controller = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        public Task<string> GetById(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + AspNetStubs + controller, AllExceptExcludedConfig);

        Assert.Contains("public static class ProductsController_GetByIdTool", generated);
        Assert.Contains("$\"/api/products/{routeid}\"", generated);
        Assert.Contains("typeof(ProductsController_GetByIdTool)", generated);
    }

    [Fact]
    public void Generator_EmitsMethodLevelRouteTemplate_ThroughFullPipeline()
    {
        const string controller = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        [Route(""search"")]
        public Task<string> Search() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + AspNetStubs + controller, AllExceptExcludedConfig);

        Assert.Contains("public static class ProductsController_SearchTool", generated);
        Assert.Contains("$\"/api/products/search\"", generated);
    }

    [Fact]
    public void Generator_ExcludesEndpoint_WhenListedInExcludeConfig()
    {
        const string controller = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        public Task<string> GetAll() => Task.FromResult(string.Empty);
    }
}
";
        const string config =
            @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [""Products.GetAll""] }";

        var generated = RunGenerator(Usings + AspNetStubs + controller, config);

        Assert.DoesNotContain("ProductsController_GetAllTool", generated);
    }

    private static string RunGenerator(string source, string generatorJson)
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

        var driver = CSharpGeneratorDriver
            .Create(new McpToolGenerator())
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("Mcp/generator.json", generatorJson)))
            .RunGenerators(compilation);

        var runResult = driver.GetRunResult();

        return string.Join(
            "\n",
            runResult.Results
                .SelectMany(r => r.GeneratedSources)
                .Select(s => s.SourceText.ToString()));
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly string _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = text;
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(_text, Encoding.UTF8);
    }
}
