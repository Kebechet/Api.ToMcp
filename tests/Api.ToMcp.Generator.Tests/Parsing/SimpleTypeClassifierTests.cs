using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Api.ToMcp.Generator.Parsing;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Api.ToMcp.Generator.Tests.Parsing;

/// <summary>
/// The generator's split of parameter types into simple and complex, checked against MVC's own
/// (<c>ModelMetadata.IsComplexType</c>): each type is compiled into an assembly, the generator classifies its Roslyn
/// symbol and MVC the loaded type.
/// </summary>
public class SimpleTypeClassifierTests
{
    [Theory]
    [InlineData("OrderId", true)]
    [InlineData("OrderId?", true)]
    [InlineData("CustomerId", true)]
    [InlineData("CustomerId?", true)]
    [InlineData("InvoiceId", true)]
    [InlineData("ProjectId", true)]
    [InlineData("EntityId", true)]
    [InlineData("Sku", true)]
    [InlineData("Sku?", true)]
    [InlineData("Slug", true)]
    [InlineData("OrderStatus", true)]
    [InlineData("OrderStatus?", true)]
    [InlineData("string", true)]
    [InlineData("bool", true)]
    [InlineData("char", true)]
    [InlineData("int", true)]
    [InlineData("int?", true)]
    [InlineData("long", true)]
    [InlineData("sbyte", true)]
    [InlineData("ushort", true)]
    [InlineData("uint", true)]
    [InlineData("ulong", true)]
    [InlineData("nint", true)]
    [InlineData("nuint", true)]
    [InlineData("float", true)]
    [InlineData("double", true)]
    [InlineData("decimal", true)]
    [InlineData("Half", true)]
    [InlineData("Int128", true)]
    [InlineData("UInt128", true)]
    [InlineData("Guid", true)]
    [InlineData("Guid?", true)]
    [InlineData("DateTime", true)]
    [InlineData("DateTimeOffset", true)]
    [InlineData("TimeSpan", true)]
    [InlineData("DateOnly", true)]
    [InlineData("DateOnly?", true)]
    [InlineData("TimeOnly", true)]
    [InlineData("DayOfWeek", true)]
    [InlineData("Uri", true)]
    [InlineData("Version", true)]
    [InlineData("System.Net.IPAddress", true)]
    [InlineData("CultureInfo", true)]
    [InlineData("ProductFilter", false)]
    [InlineData("ProductFilter?", false)]
    [InlineData("ExpandableFilter", false)]
    [InlineData("Rating", false)]
    [InlineData("object", false)]
    [InlineData("byte[]", false)]
    [InlineData("int[]", false)]
    [InlineData("List<int>", false)]
    [InlineData("List<string>", false)]
    [InlineData("Dictionary<string, string>", false)]
    [InlineData("System.Text.Json.JsonElement", false)]
    public void IsSimpleType_AgreesWithMvc(string typeExpression, bool isSimple)
    {
        var (symbol, type) = CompileParameterType(typeExpression);

        // The expectation is checked against MVC too, so a wrong one cannot hide a wrong rule.
        Assert.Equal(isSimple, !new EmptyModelMetadataProvider().GetMetadataForType(type).IsComplexType);
        Assert.Equal(isSimple, SimpleTypeClassifier.IsSimpleType(symbol));
    }

    /// <summary>
    /// The type of a parameter declared as <paramref name="typeExpression"/>, as its Roslyn symbol and as the loaded
    /// <see cref="Type"/>, so that <c>OrderId?</c> is the same <c>Nullable&lt;OrderId&gt;</c> on both sides.
    /// </summary>
    private static (ITypeSymbol Symbol, Type Type) CompileParameterType(string typeExpression)
    {
        var source = SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
    public static class Probe
    {{
        public static void Take({typeExpression} value) {{ }}
    }}
}}
";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            $"Probe{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

        var symbol = compilation.GetTypeByMetadataName("TestApi.Probe")!
            .GetMembers("Take")
            .OfType<IMethodSymbol>()
            .Single()
            .Parameters[0]
            .Type;

        // [TypeConverter] keeps only the converter's assembly-qualified name, which TypeDescriptor resolves in the default
        // context; an assembly from Assembly.Load(byte[]) is not found there, and MVC would see no converter at all.
        assemblyStream.Position = 0;
        var type = AssemblyLoadContext.Default.LoadFromStream(assemblyStream)
            .GetType("TestApi.Probe")!
            .GetMethod("Take")!
            .GetParameters()[0]
            .ParameterType;

        return (symbol, type);
    }
}
