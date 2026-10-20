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
    [InlineData("[FromQuery] double ratio = double.NaN", "double? ratio = double.NaN")]
    [InlineData("[FromQuery] double ratio = double.NegativeInfinity", "double? ratio = double.NegativeInfinity")]
    [InlineData("[FromQuery] decimal price = 1.5m", "decimal? price = 1.5M")]
    [InlineData("[FromQuery] long offset = long.MinValue", "long? offset = -9223372036854775808L")]
    [InlineData("[FromQuery] ulong offset = ulong.MaxValue", "ulong? offset = 18446744073709551615UL")]
    public void Generator_CarriesAnActionsDefaultValueOver_IntoAToolParameterThatCompiles(string parameter, string expectedDeclaration)
    {
        var tool = GenerateCompilingSimpleTypeTool($"public const int PageSize = 25; [HttpGet] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains($" {expectedDeclaration},", tool);
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
