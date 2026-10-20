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
public partial class McpToolGeneratorTests
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
    public class ApiVersionAttribute : Attribute
    {
        public ApiVersionAttribute(string version) { }
        public ApiVersionAttribute(double version) { }
        public ApiVersionAttribute(int major, int minor) { }
    }
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
        Assert.Contains("async Task<CallToolResult> InvokeAsync", generated);
        Assert.Contains("$\"/api/products/{routeid}\"", generated);
        Assert.Contains("typeof(ProductsController_GetByIdTool)", generated);
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", generated);
        Assert.Contains("invoker.GetAsync(route, cancellationToken)", generated);
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

    [Fact]
    public void Generator_UsesXmlDocSummaryForToolAndParamDescriptions()
    {
        const string controller = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        /// <summary>Gets a product by id.</summary>
        /// <param name=""id"">The product identifier.</param>
        [HttpGet(""{id}"")]
        public Task<string> GetById(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + AspNetStubs + controller, AllExceptExcludedConfig);

        Assert.Contains("[Description(\"Gets a product by id.\")]", generated);
        Assert.Contains("[Description(\"The product identifier.\")]", generated);
    }

    [Fact]
    public void Generator_FallsBackToGenericParamDescription_WhenNoXmlDoc()
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

        Assert.Contains("[Description(\"Parameter: id\")]", generated);
    }

    [Fact]
    public void Generator_BakesApiVersion_FromControllerAttribute_IntoUrlSegmentRoute()
    {
        const string controller = @"
namespace TestApi
{
    [ApiVersion(""2.0"")]
    [Route(""api/v{version:apiVersion}/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        public Task<string> GetById(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + AspNetStubs + controller, AllExceptExcludedConfig);

        Assert.Contains("$\"/api/v2.0/products/{routeid}\"", generated);
        Assert.DoesNotContain(":apiVersion", generated);
        Assert.DoesNotContain("{version", generated);
    }

    [Fact]
    public void Generator_DefaultsApiVersion_WhenPlaceholderPresentButNoAttribute()
    {
        const string controller = @"
namespace TestApi
{
    [Route(""api/v{version:apiVersion}/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        public Task<string> GetAll() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + AspNetStubs + controller, AllExceptExcludedConfig);

        Assert.Contains("$\"/api/v1.0/products\"", generated);
        Assert.DoesNotContain(":apiVersion", generated);
    }

    [Theory]
    [InlineData("/catalog/search")]
    [InlineData("~/catalog/search")]
    public void Generator_AbsoluteActionTemplate_ReplacesTheControllerPrefix(string template)
    {
        // ASP.NET routing ignores the controller's [Route] for an action template starting with "/" or "~/".
        var controller = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet(""" + template + @""")]
        public Task<string> Search(string query) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + AspNetStubs + controller, AllExceptExcludedConfig);

        Assert.Contains("var route = $\"/catalog/search\";", generated);
        Assert.DoesNotContain("/api/products/", generated);
    }

    [Fact]
    public void Generator_UsesScopeFromMcpExpose_OverTheHttpMethodDefault()
    {
        // Query-style endpoints that take their filter as a POST body would otherwise demand Write,
        // locking read-only grants out of what is only a read.
        const string controller = @"
namespace Api.ToMcp.Abstractions.Scopes
{
    [Flags]
    public enum McpScope { None = 0, Read = 1, Write = 2, Delete = 4 }
}

namespace Api.ToMcp.Abstractions
{
    public sealed class McpExposeAttribute : Attribute
    {
        public Api.ToMcp.Abstractions.Scopes.McpScope Scope { get; set; }
    }
}

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [Api.ToMcp.Abstractions.McpExpose(Scope = Api.ToMcp.Abstractions.Scopes.McpScope.Read)]
        [HttpPost(""search"")]
        public Task<string> Search(string query) => Task.FromResult(string.Empty);

        [HttpPost]
        public Task<string> Create(string name) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + AspNetStubs + controller, AllExceptExcludedConfig);

        var searchTool = generated.Substring(generated.IndexOf("class ReportsController_SearchTool", StringComparison.Ordinal));
        var createTool = generated.Substring(generated.IndexOf("class ReportsController_CreateTool", StringComparison.Ordinal));
        Assert.Contains("BeforeInvokeAsync(McpScope.Read,", searchTool.Substring(0, searchTool.IndexOf("return await", StringComparison.Ordinal)));
        Assert.Contains("BeforeInvokeAsync(McpScope.Write,", createTool.Substring(0, createTool.IndexOf("return await", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("McpScope.Read | McpScope.Write", "McpScope.Read | McpScope.Write")]
    [InlineData("McpScope.Read | McpScope.Delete", "McpScope.Read | McpScope.Delete")]
    [InlineData("McpScope.All", "McpScope.All")]
    [InlineData("(McpScope)8", "(McpScope)8")]
    public void Generator_CombinedOrUnnamedScope_EmitsAScopeExpressionThatIsValidCSharp(string scopeInSource, string expectedScopeExpression)
    {
        // McpScope is a [Flags] enum: the ToString() of Read | Write is "Read, Write", which is not C#.
        var controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [McpExpose(Scope = " + scopeInSource + @")]
        [HttpPost(""import"")]
        public Task<string> Import(string name) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal(
            $"await invoker.BeforeInvokeAsync({expectedScopeExpression}, cancellationToken);",
            GetScopeCheck(generated, "ReportsController_ImportTool"));
    }

    [Fact]
    public void Generator_UsesScopeFromAControllerMcpExpose_ForActionsWithoutTheirOwn()
    {
        const string controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    [McpExpose(Scope = McpScope.Read)]
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [HttpPost(""search"")]
        public Task<string> Search(string query) => Task.FromResult(string.Empty);

        [McpExpose(Scope = McpScope.Write)]
        [HttpPost(""import"")]
        public Task<string> Import(string name) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);", GetScopeCheck(generated, "ReportsController_SearchTool"));
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", GetScopeCheck(generated, "ReportsController_ImportTool"));
    }

    [Fact]
    public void Generator_ActionWithAnExplicitNoneScope_FollowsItsHttpMethod_NotTheControllerScope()
    {
        // Inheriting the controller's Read here would let a read-only grant call a state-changing POST.
        const string controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    [McpExpose(Scope = McpScope.Read)]
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [McpExpose(Scope = McpScope.None)]
        [HttpPost(""import"")]
        public Task<string> Import(string name) => Task.FromResult(string.Empty);

        [McpExpose(ToolName = ""reports_export"")]
        [HttpPost(""export"")]
        public Task<string> Export(string name) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", GetScopeCheck(generated, "ReportsController_ImportTool"));
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);", GetScopeCheck(generated, "ReportsController_ExportTool"));
    }

    [Fact]
    public void Generator_InheritsScopeFromTheNearestMcpExpose_OfABaseControllerOrOverriddenAction()
    {
        // [McpExpose] is Inherited = true, but Roslyn's GetAttributes() returns only what a symbol declares itself;
        // missing the base's scope would fall back to the HTTP method's, a weaker check than the author asked for.
        const string controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    [McpExpose(Scope = McpScope.All)]
    public abstract class AdminControllerBase : ControllerBase
    {
        [McpExpose(Scope = McpScope.Read)]
        [HttpPost(""search"")]
        public virtual Task<string> Search(string query) => Task.FromResult(string.Empty);
    }

    [Route(""api/[controller]"")]
    public class UsersController : AdminControllerBase
    {
        [HttpGet]
        public Task<string> GetAll() => Task.FromResult(string.Empty);

        [HttpPost(""search"")]
        public override Task<string> Search(string query) => Task.FromResult(string.Empty);
    }

    [McpExpose]
    [Route(""api/[controller]"")]
    public class AuditsController : AdminControllerBase
    {
        [HttpGet]
        public Task<string> GetAll() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.All, cancellationToken);", GetScopeCheck(generated, "UsersController_GetAllTool"));
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);", GetScopeCheck(generated, "UsersController_SearchTool"));
        // As with reflection, a derived [McpExpose] replaces the base's whole attribute, Scope included.
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);", GetScopeCheck(generated, "AuditsController_GetAllTool"));
    }

    [Fact]
    public void Generator_SendsAQueryParameter_UnderTheNameItsFromQueryAttributeBinds()
    {
        // MVC binds [FromQuery(Name = "q")] from "q"; sent as "query", the action would silently run with its default.
        const string controller = @"
namespace TestApi
{
    public sealed class ReportFilter
    {
        public string? City { get; set; }
    }

    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [HttpGet]
        public Task<string> Find([FromQuery(Name = ""q"")] string query, [FromQuery(Name = ""f"")] ReportFilter filter, int page) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Contains("AddQueryValue(queryParts, \"q\", query);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"f\", filter);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"page\", page);", generated);
    }

    [Fact]
    public void Generator_QueryAliasWithQuotesAndBackslashes_EmitsAnEscapedLiteralThatCompiles()
    {
        const string controller = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [HttpGet]
        public Task<string> Find([FromQuery(Name = ""q\""x\\y"")] string query) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);
        var errors = CompileWithGeneratedTools(Usings + controller, AllExceptExcludedConfig);

        Assert.Contains("AddQueryValue(queryParts, \"q\\\"x\\\\y\", query);", generated);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Generator_ReplacesControllerAndActionTokens_InAbsoluteAndRelativeTemplates()
    {
        // ASP.NET Core replaces the tokens in an absolute template too; left in, the request goes to a literal "[controller]" and 404s.
        const string controller = @"
namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet(""/catalog/[controller]/search"")]
        public Task<string> Search(string query) => Task.FromResult(string.Empty);

        [HttpGet(""[action]"")]
        public Task<string> Popular() => Task.FromResult(string.Empty);

        [HttpGet(""~/[Controller]/[Action]"")]
        public Task<string> RecentAsync() => Task.FromResult(string.Empty);

        [ActionName(""lookup"")]
        [HttpGet(""[action]"")]
        public Task<string> FindAsync() => Task.FromResult(string.Empty);

        [ActionName(""price$&"")]
        [HttpGet(""[action]"")]
        public Task<string> Price() => Task.FromResult(string.Empty);
    }

    public abstract class CatalogControllerBase : ControllerBase
    {
        [ActionName(""browse"")]
        public virtual Task<string> List() => Task.FromResult(string.Empty);
    }

    [Route(""api/[controller]"")]
    public class ShelvesController : CatalogControllerBase
    {
        [HttpGet(""[action]"")]
        public override Task<string> List() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal("var route = $\"/catalog/products/search\";", GetRouteLine(generated, "ProductsController_SearchTool"));
        Assert.Equal("var route = $\"/api/products/popular\";", GetRouteLine(generated, "ProductsController_PopularTool"));
        // MVC drops the Async suffix from action names by default (SuppressAsyncSuffixInActionNames).
        Assert.Equal("var route = $\"/products/recent\";", GetRouteLine(generated, "ProductsController_RecentAsyncTool"));
        // [ActionName] replaces the method name, Async suffix and all.
        Assert.Equal("var route = $\"/api/products/lookup\";", GetRouteLine(generated, "ProductsController_FindAsyncTool"));
        // A "$&" in the name is text, not a regex substitution of the matched token.
        Assert.Equal("var route = $\"/api/products/price$&\";", GetRouteLine(generated, "ProductsController_PriceTool"));
        // MVC inherits [ActionName] from the base action an override replaces.
        Assert.Equal("var route = $\"/api/shelves/browse\";", GetRouteLine(generated, "ShelvesController_ListTool"));
    }

    [Fact]
    public void Generator_EmitsToolsThatCompile_AgainstTheRealRuntimeAndAspNetCore()
    {
        // The other tests assert on the emitted text only; this one is what catches emitted code that is not C#.
        const string controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    public sealed class ReportFilter
    {
        public DateOnly? From { get; set; }
        public string[] Tags { get; set; } = [];
    }

    [McpExpose(Scope = McpScope.Read)]
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [HttpGet(""{id}"")]
        public Task<string> GetById(Guid id) => Task.FromResult(string.Empty);

        [HttpGet]
        public Task<string> Find([FromQuery] ReportFilter filter, int page) => Task.FromResult(string.Empty);

        [HttpPost(""search"")]
        public Task<string> Search([FromBody] ReportFilter filter) => Task.FromResult(string.Empty);

        [McpExpose(Scope = McpScope.Read | McpScope.Write)]
        [HttpPost(""import"")]
        public Task<string> Import(string name) => Task.FromResult(string.Empty);

        [McpExpose(Scope = McpScope.Read | McpScope.Delete)]
        [HttpDelete(""{id}"")]
        public Task<string> Delete(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        var errors = CompileWithGeneratedTools(Usings + controller, AllExceptExcludedConfig);

        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
    }

    private static string GetScopeCheck(string generated, string toolClassName)
    {
        return GetToolLine(generated, toolClassName, "BeforeInvokeAsync");
    }

    private static string GetRouteLine(string generated, string toolClassName)
    {
        return GetToolLine(generated, toolClassName, "var route = ");
    }

    private static string GetToolLine(string generated, string toolClassName, string lineMarker)
    {
        var tool = generated.Substring(generated.IndexOf($"class {toolClassName}", StringComparison.Ordinal));

        return tool
            .Split('\n')
            .First(x => x.Contains(lineMarker))
            .Trim();
    }

    private static string RunGenerator(string source, string generatorJson)
    {
        var runResult = CreateDriver(generatorJson)
            .RunGenerators(CreateCompilation(source))
            .GetRunResult();

        return string.Join(
            "\n",
            runResult.Results
                .SelectMany(r => r.GeneratedSources)
                .Select(s => s.SourceText.ToString()));
    }

    private static List<Diagnostic> CompileWithGeneratedTools(string source, string generatorJson)
    {
        CreateDriver(generatorJson).RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out var outputCompilation, out var generatorDiagnostics);

        return generatorDiagnostics
            .Concat(outputCompilation.GetDiagnostics())
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    private static CSharpCompilation CreateCompilation(params string[] sources)
    {
        // DocumentationMode.Parse makes XML doc comments available via GetDocumentationCommentXml,
        // mirroring a consumer build with <GenerateDocumentationFile>true</GenerateDocumentationFile>.
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Parse);
        var syntaxTrees = sources.Select(x => CSharpSyntaxTree.ParseText(x, parseOptions));

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        return CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <param name="generatorJson">The config's content, or null for a project without a generator.json.</param>
    private static GeneratorDriver CreateDriver(string? generatorJson)
    {
        var driver = CSharpGeneratorDriver.Create(new McpToolGenerator());
        if (generatorJson is null)
        {
            return driver;
        }

        return driver.AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText("Mcp/generator.json", generatorJson)));
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
