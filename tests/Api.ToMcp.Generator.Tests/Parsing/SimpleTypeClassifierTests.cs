using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
    [InlineData("TicketId", true)]
    [InlineData("TicketId?", true)]
    [InlineData("ShelfCode", true)]
    [InlineData("SeatId", true)]
    [InlineData("GateId", true)]
    [InlineData("DockId", false)]
    [InlineData("IfaceId", true)]
    [InlineData("IfaceId?", true)]
    [InlineData("ExpandableIfaceFilter", false)]
    [InlineData("HiddenIfaceId", false)]
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
        var (symbol, type) = CompileParameterType(typeExpression, SimpleTypeSources.Types, []);

        AssertAgreesWithMvc(isSimple, symbol, type);
    }

    [Theory]
    [InlineData("OrderId", true)]
    [InlineData("OrderId?", true)]
    [InlineData("CustomerId", true)]
    [InlineData("InvoiceId", true)]
    [InlineData("ProjectId", true)]
    [InlineData("Sku", true)]
    [InlineData("Slug", true)]
    [InlineData("TicketId", true)]
    [InlineData("TicketId?", true)]
    [InlineData("ShelfCode", true)]
    [InlineData("SeatId", true)]
    [InlineData("GateId", true)]
    [InlineData("DockId", false)]
    [InlineData("IfaceId", true)]
    [InlineData("ExpandableIfaceFilter", false)]
    [InlineData("HiddenIfaceId", false)]
    [InlineData("OrderStatus", true)]
    [InlineData("ProductFilter", false)]
    [InlineData("ExpandableFilter", false)]
    [InlineData("Rating", false)]
    public void IsSimpleType_AgreesWithMvc_ForATypeFromAReferencedAssembly(string typeExpression, bool isSimple)
    {
        // A type from another project or a package is a metadata symbol: Roslyn reads its attributes, overrides and
        // explicit implementations from IL, not from source.
        var (symbol, type) = CompileParameterType(typeExpression, "", [ReferencedTypes.Value]);
        Assert.DoesNotContain(symbol.OriginalDefinition.Locations, x => x.IsInSource);

        AssertAgreesWithMvc(isSimple, symbol, type);
    }

    private static readonly MetadataReference[] FrameworkReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    /// <summary>
    /// The test types compiled once into an assembly of their own, which a probe references as metadata.
    /// </summary>
    private static readonly Lazy<MetadataReference> ReferencedTypes = new(() =>
    {
        var image = Emit(CreateCompilation("SimpleTypes", SimpleTypeSources.Usings + SimpleTypeSources.Types, []));
        LoadIntoDefaultContext(image);

        return MetadataReference.CreateFromImage(image);
    });

    private static void AssertAgreesWithMvc(bool isSimple, ITypeSymbol symbol, Type type)
    {
        // The expectation is checked against MVC too, so a wrong one cannot hide a wrong rule.
        Assert.Equal(isSimple, !new EmptyModelMetadataProvider().GetMetadataForType(type).IsComplexType);
        Assert.Equal(isSimple, SimpleTypeClassifier.IsSimpleType(symbol));
    }

    /// <summary>
    /// The type of a parameter declared as <paramref name="typeExpression"/>, as its Roslyn symbol and as the loaded
    /// <see cref="Type"/>, so that <c>OrderId?</c> is the same <c>Nullable&lt;OrderId&gt;</c> on both sides.
    /// </summary>
    private static (ITypeSymbol Symbol, Type Type) CompileParameterType(string typeExpression, string types, MetadataReference[] references)
    {
        var source = SimpleTypeSources.Usings + types + $@"
namespace TestApi
{{
    public static class Probe
    {{
        public static void Take({typeExpression} value) {{ }}
    }}
}}
";
        var compilation = CreateCompilation("Probe", source, references);

        var symbol = compilation.GetTypeByMetadataName("TestApi.Probe")!
            .GetMembers("Take")
            .OfType<IMethodSymbol>()
            .Single()
            .Parameters[0]
            .Type;
        var type = LoadIntoDefaultContext(Emit(compilation))
            .GetType("TestApi.Probe")!
            .GetMethod("Take")!
            .GetParameters()[0]
            .ParameterType;

        return (symbol, type);
    }

    private static CSharpCompilation CreateCompilation(string assemblyNamePrefix, string source, MetadataReference[] references)
    {
        return CSharpCompilation.Create(
            $"{assemblyNamePrefix}{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(source)],
            FrameworkReferences.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static byte[] Emit(CSharpCompilation compilation)
    {
        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

        return assemblyStream.ToArray();
    }

    /// <summary>
    /// [TypeConverter] keeps only the converter's assembly-qualified name, which TypeDescriptor resolves in the default
    /// context; an assembly from Assembly.Load(byte[]) is not found there, and MVC would see no converter at all. A probe
    /// that references the test types resolves them there too.
    /// </summary>
    private static Assembly LoadIntoDefaultContext(byte[] image)
    {
        return AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(image));
    }
}
