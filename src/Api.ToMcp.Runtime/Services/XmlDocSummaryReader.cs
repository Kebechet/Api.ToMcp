using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Reads property and field <c>&lt;summary&gt;</c> texts from the <c>&lt;AssemblyName&gt;.xml</c> file the compiler
/// writes next to an assembly built with <c>GenerateDocumentationFile</c>, once per assembly. XML docs are not
/// compiled into the assembly, so that file is the only place a <c>&lt;summary&gt;</c> survives to runtime.
/// </summary>
internal sealed class XmlDocSummaryReader
{
    private const string PropertyDocumentationIdPrefix = "P:";
    private const string FieldDocumentationIdPrefix = "F:";
    private const string XmlDocFileExtension = ".xml";
    private const string MemberElementName = "member";
    private const string NameAttributeName = "name";
    private const string SummaryElementName = "summary";
    private const string CrefAttributeName = "cref";
    private const string LangwordAttributeName = "langword";
    private const string HrefAttributeName = "href";

    /// <summary>
    /// The MCP SDK counts as framework: the SDK builds a schema for a tool's return type too, which for a generated
    /// tool is its own <c>CallToolResult</c>, and nobody deploys the SDK's XML docs.
    /// </summary>
    private static readonly string[] FrameworkAssemblyNamePrefixes = ["System.", "Microsoft.", "ModelContextProtocol."];
    private static readonly string[] FrameworkAssemblyNames = ["System", "mscorlib", "netstandard", "ModelContextProtocol"];

    /// <summary>
    /// Elements that stand on their own line in rendered docs; a space around them keeps "term" and
    /// "description" of a list item from running into one word.
    /// </summary>
    private static readonly HashSet<string> SeparatedElementNames = ["para", "br", "list", "item", "term", "description", "code"];

    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, string>?> _summariesByAssembly = new();
    private readonly ConcurrentDictionary<Assembly, string> _missingXmlDocPathsByAssembly = new();
    private readonly ConcurrentDictionary<Assembly, XmlDocReadFailure> _unreadableXmlDocsByAssembly = new();

    /// <summary>
    /// Expected XML documentation file path of every assembly that was asked for a summary and has no such file.
    /// Framework assemblies are never asked.
    /// </summary>
    public IReadOnlyDictionary<Assembly, string> MissingXmlDocPathsByAssembly => _missingXmlDocPathsByAssembly;

    /// <summary>
    /// Every assembly that was asked for a summary and has an XML documentation file that could not be read: cut short,
    /// malformed or locked. Its members stay undescribed, as with a missing file.
    /// </summary>
    public IReadOnlyDictionary<Assembly, XmlDocReadFailure> UnreadableXmlDocsByAssembly => _unreadableXmlDocsByAssembly;

    /// <summary>
    /// Returns the normalized <c>&lt;summary&gt;</c> of a property or field, or null when the declaring assembly is a
    /// framework assembly, has no readable XML documentation file, or documents the member without a summary.
    /// <c>&lt;inheritdoc/&gt;</c> is not resolved, so a member documented only that way yields null.
    /// </summary>
    public string? GetSummary(MemberInfo member)
    {
        var declaringType = member.DeclaringType;
        if (declaringType is null || declaringType.Assembly.IsDynamic || IsFrameworkAssembly(declaringType.Assembly))
        {
            return null;
        }

        var summariesByDocumentationId = _summariesByAssembly.GetOrAdd(declaringType.Assembly, LoadSummaries);
        if (summariesByDocumentationId is null)
        {
            return null;
        }

        return summariesByDocumentationId.TryGetValue(GetDocumentationId(member), out var summary)
            ? summary
            : null;
    }

    /// <summary>
    /// The doc id the compiler writes for a property (<c>P:</c>) or field (<c>F:</c>): a generic type by its definition
    /// with a backtick arity (<c>Paged`1</c>), nested types separated by a dot where reflection uses a plus, and the
    /// declaring type rather than the reflected one, so an inherited member resolves to the base class's docs.
    /// </summary>
    internal static string GetDocumentationId(MemberInfo member)
    {
        var prefix = member is FieldInfo
            ? FieldDocumentationIdPrefix
            : PropertyDocumentationIdPrefix;
        var declaringType = member.DeclaringType!;
        var typeDefinition = declaringType.IsConstructedGenericType
            ? declaringType.GetGenericTypeDefinition()
            : declaringType;

        return $"{prefix}{typeDefinition.FullName!.Replace('+', '.')}.{member.Name}";
    }

    /// <summary>
    /// Collects the rendered summary of every property and field member of a documentation file, keyed by doc id.
    /// Streams the file member by member instead of loading the whole document.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadSummaries(XmlReader xmlReader)
    {
        var summariesByDocumentationId = new Dictionary<string, string>(StringComparer.Ordinal);

        xmlReader.MoveToContent();
        while (!xmlReader.EOF)
        {
            if (xmlReader.NodeType != XmlNodeType.Element || xmlReader.Name != MemberElementName)
            {
                xmlReader.Read();
                continue;
            }

            var documentationId = xmlReader.GetAttribute(NameAttributeName);
            if (documentationId is null || !IsPropertyOrFieldDocumentationId(documentationId))
            {
                xmlReader.Skip();
                continue;
            }

            var memberElement = (XElement)XNode.ReadFrom(xmlReader);
            var summaryElement = memberElement.Element(SummaryElementName);
            if (summaryElement is null)
            {
                continue;
            }

            var summary = RenderSummary(summaryElement);
            if (summary.Length > 0)
            {
                summariesByDocumentationId[documentationId] = summary;
            }
        }

        return summariesByDocumentationId;
    }

    /// <summary>
    /// Flattens a summary to one line: <c>&lt;see cref&gt;</c> becomes the member's last name segment, <c>&lt;c&gt;</c>
    /// its text, <c>&lt;paramref&gt;</c> and <c>&lt;typeparamref&gt;</c> the name, <c>&lt;para&gt;</c> a separator,
    /// and runs of whitespace a single space.
    /// </summary>
    internal static string RenderSummary(XElement summaryElement)
    {
        var text = new StringBuilder();
        AppendContent(text, summaryElement);

        return string.Join(" ", text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The last segment of a cref: <c>P:Ns.Type.Prop</c> is <c>Prop</c>, <c>M:Ns.Type.Run(System.String)</c> is
    /// <c>Run</c>, <c>T:Ns.Paged`1</c> is <c>Paged</c>.
    /// </summary>
    internal static string GetCrefDisplayName(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':'
            ? cref.Substring(2)
            : cref;

        var parameterListStart = name.IndexOf('(');
        if (parameterListStart >= 0)
        {
            name = name.Substring(0, parameterListStart);
        }

        var lastSeparator = name.LastIndexOf('.');
        if (lastSeparator >= 0)
        {
            name = name.Substring(lastSeparator + 1);
        }

        var arityStart = name.IndexOf('`');
        if (arityStart >= 0)
        {
            name = name.Substring(0, arityStart);
        }

        return name;
    }

    private IReadOnlyDictionary<string, string>? LoadSummaries(Assembly assembly)
    {
        var xmlDocPath = GetXmlDocPath(assembly);
        if (!File.Exists(xmlDocPath))
        {
            _missingXmlDocPathsByAssembly[assembly] = xmlDocPath;
            return null;
        }

        try
        {
            using var xmlReader = XmlReader.Create(xmlDocPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });

            return ReadSummaries(xmlReader);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            // The descriptions are optional, and every tool is built at startup: a damaged file must not stop the host.
            _unreadableXmlDocsByAssembly[assembly] = new XmlDocReadFailure(xmlDocPath, exception);
            return null;
        }
    }

    /// <summary>
    /// A single-file bundle reports an empty <see cref="Assembly.Location"/> while its loose files, the XML docs
    /// among them, sit in <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    private static string GetXmlDocPath(Assembly assembly)
    {
        if (!string.IsNullOrEmpty(assembly.Location))
        {
            return Path.ChangeExtension(assembly.Location, XmlDocFileExtension);
        }

        return Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}{XmlDocFileExtension}");
    }

    private static bool IsFrameworkAssembly(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name;
        if (assemblyName is null)
        {
            return true;
        }

        return FrameworkAssemblyNames.Contains(assemblyName) ||
            FrameworkAssemblyNamePrefixes.Any(x => assemblyName.StartsWith(x, StringComparison.Ordinal));
    }

    private static bool IsPropertyOrFieldDocumentationId(string documentationId)
    {
        return documentationId.StartsWith(PropertyDocumentationIdPrefix, StringComparison.Ordinal) ||
            documentationId.StartsWith(FieldDocumentationIdPrefix, StringComparison.Ordinal);
    }

    private static void AppendContent(StringBuilder text, XElement element)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText textNode:
                    text.Append(textNode.Value);
                    break;
                case XElement childElement:
                    AppendElement(text, childElement);
                    break;
            }
        }
    }

    private static void AppendElement(StringBuilder text, XElement element)
    {
        var elementName = element.Name.LocalName;
        switch (elementName)
        {
            case "see":
            case "seealso":
                AppendReference(text, element);
                return;
            case "paramref":
            case "typeparamref":
                text.Append(element.Attribute(NameAttributeName)?.Value);
                return;
            case "inheritdoc":
                return;
        }

        var isSeparated = SeparatedElementNames.Contains(elementName);
        if (isSeparated)
        {
            text.Append(' ');
        }

        AppendContent(text, element);

        if (isSeparated)
        {
            text.Append(' ');
        }
    }

    private static void AppendReference(StringBuilder text, XElement element)
    {
        if (element.Nodes().Any())
        {
            AppendContent(text, element);
            return;
        }

        var cref = element.Attribute(CrefAttributeName)?.Value;
        if (cref is not null)
        {
            text.Append(GetCrefDisplayName(cref));
            return;
        }

        text.Append(element.Attribute(LangwordAttributeName)?.Value ?? element.Attribute(HrefAttributeName)?.Value);
    }
}

/// <summary>
/// An XML documentation file that exists next to its assembly but could not be read.
/// </summary>
/// <param name="XmlDocPath">Path of the file.</param>
/// <param name="Exception">Why reading it failed.</param>
internal sealed record XmlDocReadFailure(string XmlDocPath, Exception Exception);
