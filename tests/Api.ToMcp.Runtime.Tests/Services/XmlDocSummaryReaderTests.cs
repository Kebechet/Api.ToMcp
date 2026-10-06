using System.Xml;
using Api.ToMcp.Abstractions.Configuration;
using Api.ToMcp.Runtime.Services;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class XmlDocSummaryReaderTests
{
    [Fact]
    public void GetSummary_ReadsThePropertySummary_FromTheAssemblyXmlFile()
    {
        var reader = new XmlDocSummaryReader();

        var summary = reader.GetSummary(typeof(DerivedRequest).GetProperty(nameof(DerivedRequest.Query))!);

        Assert.Equal("Free-text search phrase.", summary);
    }

    [Fact]
    public void GetSummary_ResolvesAnInheritedProperty_ToTheBaseClassDocs()
    {
        var reader = new XmlDocSummaryReader();

        var summary = reader.GetSummary(typeof(DerivedRequest).GetProperty(nameof(DerivedRequest.AccountId))!);

        Assert.Equal("Identifier of the account the request runs for.", summary);
    }

    [Fact]
    public void GetSummary_ResolvesNestedAndGenericTypes_AndFields()
    {
        var reader = new XmlDocSummaryReader();

        Assert.Equal("Depth of the nesting.", reader.GetSummary(typeof(Outer.Inner).GetProperty(nameof(Outer.Inner.Depth))!));
        Assert.Equal("Items on this page.", reader.GetSummary(typeof(Paged<DerivedRequest>).GetProperty(nameof(Paged<DerivedRequest>.Items))!));
        Assert.Equal("Number of items.", reader.GetSummary(typeof(Paged<int>).GetField(nameof(Paged<int>.Count))!));
    }

    [Fact]
    public void GetSummary_RendersReferencesAndParagraphs_AsOneLine()
    {
        var reader = new XmlDocSummaryReader();

        var summary = reader.GetSummary(typeof(RenderingSample).GetProperty(nameof(RenderingSample.Rendered))!);

        Assert.Equal("Links Paged, AccountId and Trim; shows code and null. Second paragraph.", summary);
    }

    [Fact]
    public void GetSummary_ReturnsNull_ForAnUndocumentedOrInheritdocMember()
    {
        var reader = new XmlDocSummaryReader();

        Assert.Null(reader.GetSummary(typeof(DerivedRequest).GetProperty(nameof(DerivedRequest.Undocumented))!));
        Assert.Null(reader.GetSummary(typeof(DerivedRequest).GetProperty(nameof(DerivedRequest.Note))!));
        Assert.Empty(reader.MissingXmlDocPathsByAssembly);
    }

    [Fact]
    public void GetSummary_SkipsFrameworkAndMcpSdkAssemblies_WithoutReportingThem()
    {
        // The SDK builds a schema for a tool's return type too, which for a generated tool is CallToolResult.
        var reader = new XmlDocSummaryReader();

        Assert.Null(reader.GetSummary(typeof(Uri).GetProperty(nameof(Uri.Host))!));
        Assert.Null(reader.GetSummary(typeof(CallToolResult).GetProperty(nameof(CallToolResult.IsError))!));
        Assert.Empty(reader.MissingXmlDocPathsByAssembly);
    }

    [Fact]
    public void GetSummary_ReportsAnAssemblyWithoutXmlDocs_Once()
    {
        // Api.ToMcp.Abstractions is built without GenerateDocumentationFile, so no .xml sits next to it.
        var reader = new XmlDocSummaryReader();

        Assert.Null(reader.GetSummary(typeof(GeneratorConfig).GetProperty(nameof(GeneratorConfig.Mode))!));
        Assert.Null(reader.GetSummary(typeof(GeneratorConfig).GetProperty(nameof(GeneratorConfig.Include))!));

        var (assembly, xmlDocPath) = Assert.Single(reader.MissingXmlDocPathsByAssembly);
        Assert.Same(typeof(GeneratorConfig).Assembly, assembly);
        Assert.Equal(Path.ChangeExtension(assembly.Location, ".xml"), xmlDocPath);
    }

    [Theory]
    [InlineData(typeof(Paged<int>), nameof(Paged<int>.Items), "P:Api.ToMcp.Runtime.Tests.Services.Paged`1.Items")]
    [InlineData(typeof(Paged<int>), nameof(Paged<int>.Count), "F:Api.ToMcp.Runtime.Tests.Services.Paged`1.Count")]
    [InlineData(typeof(Outer.Inner), nameof(Outer.Inner.Depth), "P:Api.ToMcp.Runtime.Tests.Services.Outer.Inner.Depth")]
    [InlineData(typeof(DerivedRequest), nameof(DerivedRequest.AccountId), "P:Api.ToMcp.Runtime.Tests.Services.BaseRequest.AccountId")]
    public void GetDocumentationId_MatchesTheCompilerFormat(Type type, string memberName, string expectedDocumentationId)
    {
        var member = type.GetMember(memberName).Single();

        Assert.Equal(expectedDocumentationId, XmlDocSummaryReader.GetDocumentationId(member));
    }

    [Fact]
    public void ReadSummaries_KeepsPropertiesAndFields_RendersInlineTags_AndNormalizesWhitespace()
    {
        const string xml = """
            <doc>
              <members>
                <member name="P:Ns.Type.Prop">
                  <summary>
                    Uses   <paramref name="filter"/> and <typeparamref name="TItem"/>
                    via <see cref="M:Ns.Type.Run(System.String)"/>, see <see href="https://example.com">the docs</see>.
                  </summary>
                </member>
                <member name="M:Ns.Type.Run(System.String)"><summary>A method.</summary></member>
                <member name="F:Ns.Type.Field"><summary>A field.</summary></member>
                <member name="P:Ns.Type.NoSummary"><remarks>Only remarks.</remarks></member>
                <member name="P:Ns.Type.Inherited"><inheritdoc/></member>
              </members>
            </doc>
            """;
        using var xmlReader = XmlReader.Create(new StringReader(xml));

        var summariesByDocumentationId = XmlDocSummaryReader.ReadSummaries(xmlReader);

        Assert.Equal(2, summariesByDocumentationId.Count);
        Assert.Equal("Uses filter and TItem via Run, see the docs.", summariesByDocumentationId["P:Ns.Type.Prop"]);
        Assert.Equal("A field.", summariesByDocumentationId["F:Ns.Type.Field"]);
    }

    [Theory]
    [InlineData("P:Ns.Type.Prop", "Prop")]
    [InlineData("M:Ns.Type.Run(System.String,System.Int32)", "Run")]
    [InlineData("T:System.Collections.Generic.List`1", "List")]
    [InlineData("!:Unresolved", "Unresolved")]
    public void GetCrefDisplayName_ReturnsTheLastSegment(string cref, string expectedDisplayName)
    {
        Assert.Equal(expectedDisplayName, XmlDocSummaryReader.GetCrefDisplayName(cref));
    }
}

public class BaseRequest
{
    /// <summary>Identifier of the account the request runs for.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Free-text note.</summary>
    public virtual string? Note { get; set; }
}

public sealed class DerivedRequest : BaseRequest
{
    /// <summary>Free-text search phrase.</summary>
    public string? Query { get; set; }

    public string? Undocumented { get; set; }

    /// <inheritdoc/>
    public override string? Note { get; set; }
}

/// <summary>A page of <typeparamref name="T"/> items.</summary>
public sealed class Paged<T>
{
    /// <summary>Number of items.</summary>
    public int Count;

    /// <summary>Items on this page.</summary>
    public List<T> Items { get; set; } = [];
}

public static class Outer
{
    public sealed class Inner
    {
        /// <summary>Depth of the nesting.</summary>
        public int Depth { get; set; }
    }
}

public sealed class RenderingSample
{
    /// <summary>
    /// Links <see cref="Paged{T}"/>, <see cref="BaseRequest.AccountId"/> and <see cref="string.Trim()"/>; shows <c>code</c>
    /// and <see langword="null"/>.
    /// <para>Second paragraph.</para>
    /// </summary>
    public string? Rendered { get; set; }
}
