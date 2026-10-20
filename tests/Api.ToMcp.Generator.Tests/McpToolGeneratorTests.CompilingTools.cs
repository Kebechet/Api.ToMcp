using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Actions whose tools used to break the consuming project's build with an error inside a generated file. A generated tool
/// has to compile for every action, whatever its parameters are called and whatever default values they declare. See
/// https://github.com/Kebechet/Api.ToMcp/issues/46.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Theory]
    [InlineData("[FromQuery] Point origin = default", "TestApi.Point? origin = null")]
    [InlineData("[FromQuery] DateTime since = default", "System.DateTime? since = null")]
    [InlineData("[FromQuery] Guid tenant = default", "System.Guid? tenant = null")]
    [InlineData("[FromQuery] int? count = default", "int? count = null")]
    [InlineData("[FromQuery] int page = 2", "int? page = 2")]
    [InlineData("[FromQuery] int page = PageSize", "int? page = 25")]
    [InlineData("[FromQuery] OrderStatus status = OrderStatus.Closed", "TestApi.OrderStatus? status = global::TestApi.OrderStatus.Closed")]
    [InlineData("[FromQuery] OrderStatus? status = OrderStatus.Closed", "TestApi.OrderStatus? status = global::TestApi.OrderStatus.Closed")]
    [InlineData("[FromQuery] OrderStatus status = (OrderStatus)7", "TestApi.OrderStatus? status = (global::TestApi.OrderStatus)7")]
    [InlineData("[FromQuery] OrderStatus status = (OrderStatus)(-1)", "TestApi.OrderStatus? status = (global::TestApi.OrderStatus)(-1)")]
    [InlineData("[FromQuery] string note = \"a\\\"b\\\\c\"", "string note = \"a\\\"b\\\\c\"")]
    [InlineData("[FromQuery] char separator = '\\''", "char? separator = '\\''")]
    [InlineData("[FromQuery] float ratio = 1.5f", "float? ratio = 1.5F")]
    [InlineData("[FromQuery] double ratio = 0.1", "double? ratio = 0.1D")]
    [InlineData("[FromQuery] double ratio = double.NaN", "double? ratio = null")]
    [InlineData("[FromQuery] double ratio = double.NegativeInfinity", "double? ratio = null")]
    [InlineData("[FromQuery] float ratio = float.PositiveInfinity", "float? ratio = null")]
    [InlineData("[FromQuery] decimal price = 1.5m", "decimal? price = 1.5M")]
    [InlineData("[FromQuery] long offset = long.MinValue", "long? offset = -9223372036854775808L")]
    [InlineData("[FromQuery] ulong offset = ulong.MaxValue", "ulong? offset = 18446744073709551615UL")]
    public void Generator_CarriesAnActionsDefaultValueOver_IntoAToolParameterThatCompiles(string parameter, string expectedDeclaration)
    {
        var tool = GenerateCompilingSimpleTypeTool($"public const int PageSize = 25; [HttpGet] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains($" {expectedDeclaration},", tool);
    }

    [Theory]
    [InlineData("string name, int? count, OrderId orderId", "name, orderId, count")]
    [InlineData("string? note, string city, OrderStatus? status, Guid tenant, [FromQuery] int page = 1", "city, tenant, note, status, page")]
    public void Generator_DeclaresTheRequiredParametersFirst_SoAnOptionalOneBeforeARequiredOneCompiles(string parameters, string expectedOrder)
    {
        // MCP passes arguments by name, so the tool's parameter order is free; C# wants every optional one last (CS1737).
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet] public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        var declaredNames = tool
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("[Description(\"Parameter: ", StringComparison.Ordinal))
            .Select(x => x.Split('"')[1].Substring("Parameter: ".Length));

        Assert.Equal(expectedOrder, string.Join(", ", declaredNames));
    }

    [Theory]
    [InlineData("[HttpPost(\"clash/{id}\")]", "int id, string route, string invoker, string queryParts, [FromBody] ProductFilter bodyJson, string cancellationToken, string routeid")]
    [InlineData("[HttpGet(\"clash/{route}\")]", "string route, string route2, string routeroute, string invoker, string invoker2, [FromQuery] string? queryParts")]
    [InlineData("[HttpPut(\"clash/{id}\")]", "int id, string System, string JsonSerializer, string McpScope, [FromBody] ProductFilter filter")]
    [InlineData("[HttpGet(\"clash/{id}\")]", "AttributedQuery q, string routeq_Id")]
    public void Generator_GivesTheToolsOwnVariablesNamesNoParameterHas_SoAToolWithAnyParameterNamesCompiles(string httpAttribute, string parameters)
    {
        GenerateCompilingSimpleTypeTool($"{httpAttribute} public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");
    }

    [Fact]
    public void Generator_NumbersAToolsOwnName_ThatAParameterHas()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpGet(\"clash/{route}\")] public Task<string> Find(string route, string route2, string routeroute, string invoker, string invoker2) => Task.FromResult(string.Empty);");

        Assert.Contains("            IMcpHttpInvoker invoker3,", tool);
        Assert.Contains($"var routeroute2 = System.Uri.EscapeDataString({Formatter}.Format(route));", tool);
        Assert.Contains("var route3 = $\"/api/orders/clash/{routeroute2}\";", tool);
        Assert.Contains("return await invoker3.GetAsync(route3, cancellationToken);", tool);
    }

    [Fact]
    public void Generator_QualifiesANameTheToolRefersTo_ThatAParameterHides()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPut(\"clash/{id}\")] public Task<string> Find(int id, string System, string JsonSerializer, string McpScope, [FromBody] ProductFilter filter) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeid = global::System.Uri.EscapeDataString({Formatter}.Format(id));", tool);
        Assert.Contains("var queryParts = new global::System.Collections.Generic.List<string>();", tool);
        Assert.Contains("var bodyJson = global::System.Text.Json.JsonSerializer.Serialize(filter);", tool);
        Assert.Contains("await invoker.BeforeInvokeAsync(global::Api.ToMcp.Abstractions.Scopes.McpScope.Write, cancellationToken);", tool);
    }

    [Fact]
    public void Generator_KeepsTheToolsOwnVariableNames_WhenNoParameterTakesThem()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpPost(\"x/{id}\")] public Task<string> Find(int id, [FromQuery] string? city, [FromBody] ProductFilter filter) => Task.FromResult(string.Empty);");

        Assert.Contains("            IMcpHttpInvoker invoker,", tool);
        Assert.Contains($"var routeid = System.Uri.EscapeDataString({Formatter}.Format(id));", tool);
        Assert.Contains("var route = $\"/api/orders/x/{routeid}\";", tool);
        Assert.Contains("var queryParts = new System.Collections.Generic.List<string>();", tool);
        Assert.Contains("var bodyJson = JsonSerializer.Serialize(filter);", tool);
        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", tool);
        Assert.Contains("return await invoker.PostAsync(route, bodyJson, cancellationToken);", tool);
    }

    [Fact]
    public void Generator_AnnotatesAReferenceParameterWithANullDefault_OfANullableObliviousAction_SoTheToolCompilesWithoutWarnings()
    {
        // The tool file enables nullable reference types, where an unannotated "string name = null" warns (CS8625), which
        // TreatWarningsAsErrors turns into a broken build.
        const string controller = @"
#nullable disable
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet]
        public Task<string> Find([FromQuery] string name = null, [FromQuery] string[] tags = null) => Task.FromResult(string.Empty);
    }
}
";
        CreateDriver(AllExceptExcludedConfig).RunGeneratorsAndUpdateCompilation(CreateCompilation(Usings + controller), out var outputCompilation, out _);
        var generatedFileDiagnostics = outputCompilation.GetDiagnostics()
            .Where(x => x.Severity >= DiagnosticSeverity.Warning && x.Location.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) == true)
            .ToList();
        var tool = GetToolClass(RunGenerator(Usings + controller, AllExceptExcludedConfig), SimpleTypeToolClassName);

        Assert.Empty(generatedFileDiagnostics);
        Assert.Contains(" string? name = null,", tool);
        Assert.Contains(" string[]? tags = null,", tool);
    }

    [Fact]
    public void Generator_WritesAToolNameAndDescriptions_AsStringsThatCompile()
    {
        // A line or paragraph separator ends a C# string literal as a line feed does.
        var tool = GenerateCompilingSimpleTypeTool(
            "[Api.ToMcp.Abstractions.McpExpose(ToolName = \"find \\\"orders\\\" \\\\ all\", Description = \"Finds\\u2028all\\u2029orders\\u0085now.\")] " +
            "[HttpGet] public Task<string> Find(string name) => Task.FromResult(string.Empty);");

        Assert.Contains("[McpServerTool(Name = \"find \\\"orders\\\" \\\\ all\", ", tool);
        Assert.Contains("[Description(\"Finds all orders now.\")]", tool);
    }

    [Fact]
    public void Generator_SendsNoQueryValueOrOptionalRouteValue_ThatEqualsTheActionsDefault()
    {
        // MVC applies the action's default to a value it is not sent, and may refuse the same value as text: 99, which no
        // member of OrderStatus has, answers 400.
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpGet(\"d/{status?}\")] public Task<string> Find([FromQuery] OrderStatus unknown = (OrderStatus)99, int page = 2, string text = \"x\", " +
            "string? none = null, OrderStatus status = OrderStatus.Closed) => Task.FromResult(string.Empty);");

        Assert.Contains("AddQueryValue(queryParts, \"unknown\", unknown == (global::TestApi.OrderStatus)99 ? null : unknown);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"page\", page == 2 ? null : page);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"text\", text == \"x\" ? null : text);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"none\", none);", tool);
        Assert.Contains($"var routestatus = System.Uri.EscapeDataString({Formatter}.Format(status == global::TestApi.OrderStatus.Closed ? null : status));", tool);
    }

    [Theory]
    [InlineData("[FromQuery, Microsoft.AspNetCore.Mvc.ModelBinding.BindRequired] int page = 1", "page")]
    [InlineData("[FromQuery, Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Required)] int page = 1", "page")]
    [InlineData("[FromQuery, System.ComponentModel.DataAnnotations.Required] int page = 1", "page")]
    public void Generator_SendsTheDefault_OfAParameterMvcRequiresAValueFor(string parameter, string name)
    {
        // MVC answers 400 when such a parameter gets no value, though the action declares a default.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains($"AddQueryValue(queryParts, \"{name}\", {name});", tool);
    }

    private const string BindingBehaviorAttributes = @"
    public sealed class MustBindAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public MustBindAttribute() : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Required)
        {
        }
    }

    public sealed class BehavesAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public BehavesAttribute(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior behavior) : base(behavior)
        {
        }
    }

    public sealed class MayBindAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public MayBindAttribute(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior ignored) : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Optional)
        {
        }
    }
";

    [Theory]
    [InlineData("[FromQuery, MustBind] int page = 1", "page")]
    [InlineData("[FromQuery, Behaves(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Required)] int page = 1", "page")]
    [InlineData("[FromQuery, Behaves(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Optional)] int page = 1", "page == 1 ? null : page")]
    [InlineData("[FromQuery, MayBind(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Required)] int page = 1", "page == 1 ? null : page")]
    [InlineData("[FromQuery, Microsoft.AspNetCore.Mvc.ModelBinding.BindNever] int page = 1", "page == 1 ? null : page")]
    public void Generator_ReadsTheBindingBehavior_ThatTheConstructorsOfAnAttributeInTheProjectPassOn(string parameter, string sentValue)
    {
        // MVC reads the behavior the attribute's constructors pass to BindingBehaviorAttribute's, whatever its own take.
        var tool = GenerateCompilingSimpleTypeTool(
            $"[HttpGet] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);",
            BindingBehaviorAttributes + ApiControllerDeclaration);

        Assert.Contains($"AddQueryValue(queryParts, \"page\", {sentValue});", tool);
    }

    [Fact]
    public void Generator_SendsTheDefault_OfAParameterWithABindingBehaviorAttributeFromAReferencedAssembly_WhereMvcReadsItAsText()
    {
        // Metadata holds no constructor code, so the behavior stays unknown; a value MVC does not require is sent harmlessly,
        // while one it requires and does not get answers 400. A default MVC refuses as text, though, an enum value that no
        // member has nor a combination of [Flags] members makes, fails when sent for sure, so the tool sends it only when
        // it differs from the default.
        const string referenced = SimpleTypeSources.Usings + @"
namespace Shared
{
    public sealed class SharedMayBindAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public SharedMayBindAttribute() : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Optional)
        {
        }
    }

    public sealed class SharedMustBindAttribute : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute
    {
        public SharedMustBindAttribute() : base(Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Required)
        {
        }
    }
}
";
        const string source = SimpleTypeSources.Usings + SimpleTypeSources.Types + @"
namespace TestApi
{
    [Flags]
    public enum Access
    {
        Read = 1,
        Write = 2
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet]
        public Task<string> Find(
            [FromQuery, Shared.SharedMayBind] int page = 1,
            [FromQuery, Shared.SharedMustBind] int size = 1,
            [FromQuery, Shared.SharedMayBind] OrderStatus status = OrderStatus.Closed,
            [FromQuery, Shared.SharedMayBind] OrderStatus unknown = (OrderStatus)99,
            [FromQuery, Shared.SharedMayBind] OrderStatus? nullableUnknown = (OrderStatus)99,
            [FromQuery, Shared.SharedMayBind] Access access = Access.Read | Access.Write,
            [FromQuery, Shared.SharedMayBind] Access none = 0,
            [FromQuery, Shared.SharedMayBind] Access unknownAccess = (Access)8) => Task.FromResult(string.Empty);
    }
}
";
        var compilation = CreateCompilation(source).AddReferences(CompileToReference(referenced, "Shared"));

        CreateDriver(AllExceptExcludedConfig).RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var errors = diagnostics.Concat(outputCompilation.GetDiagnostics()).Where(x => x.Severity == DiagnosticSeverity.Error);
        var generated = string.Join("\n", outputCompilation.SyntaxTrees.Skip(1).Select(x => x.ToString()));

        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
        Assert.Contains("AddQueryValue(queryParts, \"page\", page);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"size\", size);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"status\", status);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"unknown\", unknown == (global::TestApi.OrderStatus)99 ? null : unknown);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"nullableUnknown\", nullableUnknown == (global::TestApi.OrderStatus)99 ? null : nullableUnknown);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"access\", access);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"none\", none == (global::TestApi.Access)0 ? null : none);", generated);
        Assert.Contains("AddQueryValue(queryParts, \"unknownAccess\", unknownAccess == (global::TestApi.Access)8 ? null : unknownAccess);", generated);
    }

    [Theory]
    [InlineData("p/{page}")]
    [InlineData("p/{name}.{page?}")]
    public void Generator_SendsARouteValue_ThatEqualsTheActionsDefault_WhenTheUrlCannotLeaveItOut(string template)
    {
        // The URL needs the segment; one value of a segment of several MVC matches only with the separator before it.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"{template}\")] public Task<string> Find(string name, int page = 1) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routepage = System.Uri.EscapeDataString({Formatter}.Format(page));", tool);
    }

    [Theory]
    [InlineData("names/{name}.{ext?}", "/api/orders/names/{routename}{(routeext.Length == 0 ? \"\" : \".\")}{routeext}")]
    [InlineData("names/{name}.{ext?}/size", "/api/orders/names/{routename}{(routeext.Length == 0 ? \"\" : \".\")}{routeext}/size")]
    [InlineData("names/{name}.{ext}", "/api/orders/names/{routename}.{routeext}")]
    public void Generator_WritesTheDotBeforeAnOptionalRouteValueInASegmentOfSeveral_OnlyWithAValue(string template, string expectedRoute)
    {
        // MVC serves {name}.{ext?} at report, without the dot, and answers report. with 404.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"{template}\")] public Task<string> Find(string name, string? ext = null) => Task.FromResult(string.Empty);");

        Assert.Contains($"var route = $\"{expectedRoute}\";", tool);
    }

    [Fact]
    public void Generator_WritesADefaultValue_WithTheInvariantCulture()
    {
        // A comma as the decimal separator would end the default value in the middle of the number.
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs-CZ");
        try
        {
            var tool = GenerateCompilingSimpleTypeTool("[HttpGet] public Task<string> Find([FromQuery] double ratio = 1.5, [FromQuery] decimal price = 2.5m) => Task.FromResult(string.Empty);");

            Assert.Contains(" double? ratio = 1.5D,", tool);
            Assert.Contains(" decimal? price = 2.5M,", tool);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
