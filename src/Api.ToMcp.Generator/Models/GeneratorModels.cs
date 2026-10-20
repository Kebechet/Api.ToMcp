using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Api.ToMcp.Generator.Models
{
    internal sealed class GeneratorConfigModel
    {
        public int SchemaVersion { get; set; } = 1;
        public SelectionModeModel Mode { get; set; } = SelectionModeModel.SelectedOnly;
        public ImmutableArray<string> Include { get; set; } = ImmutableArray<string>.Empty;
        public ImmutableArray<string> Exclude { get; set; } = ImmutableArray<string>.Empty;

        /// <summary>
        /// Where each entry of <see cref="Include"/>, at the same index, is written in the file: the span of its string,
        /// or an empty span at the file's start when the parser cannot place it. Empty when the list is.
        /// </summary>
        public ImmutableArray<LinePositionSpan> IncludeSpans { get; set; } = ImmutableArray<LinePositionSpan>.Empty;

        /// <summary>
        /// Where each entry of <see cref="Exclude"/> is written in the file, as <see cref="IncludeSpans"/> are.
        /// </summary>
        public ImmutableArray<LinePositionSpan> ExcludeSpans { get; set; } = ImmutableArray<LinePositionSpan>.Empty;

        public NamingConfigModel Naming { get; set; } = new NamingConfigModel();
        public bool StructuredOutput { get; set; } = true;

        /// <summary>
        /// The generator.json this config was read from, or null for the default config of a project without one.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Why the generator.json could not be used as written, or null when it could. The config then holds what the
        /// generator falls back to.
        /// </summary>
        public ConfigErrorModel? Error { get; set; }
    }

    internal sealed class ConfigErrorModel
    {
        /// <summary>
        /// What the generator uses instead, as a clause of MCP001's message.
        /// </summary>
        public string Fallback { get; set; } = "";

        public string Reason { get; set; } = "";

        /// <summary>
        /// Where in the file the problem is, or its start when the parser does not say.
        /// </summary>
        public LinePosition Position { get; set; }
    }

    internal enum SelectionModeModel
    {
        SelectedOnly,
        AllExceptExcluded
    }

    internal sealed class NamingConfigModel
    {
        public string ToolNameFormat { get; set; } = "{Controller}_{Action}";
        public bool RemoveControllerSuffix { get; set; } = true;
    }

    internal sealed class ControllerInfoModel
    {
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string? RoutePrefix { get; set; }
        public bool HasMcpExposeAttribute { get; set; }
        public bool HasMcpIgnoreAttribute { get; set; }
        public ImmutableArray<ActionInfoModel> Actions { get; set; } = ImmutableArray<ActionInfoModel>.Empty;
    }

    internal sealed class ActionInfoModel
    {
        public string Name { get; set; } = "";
        public string ControllerName { get; set; } = "";
        public string HttpMethod { get; set; } = "";
        public string RouteTemplate { get; set; } = "";
        public ImmutableArray<ParameterInfoModel> Parameters { get; set; } = ImmutableArray<ParameterInfoModel>.Empty;
        public ReturnTypeInfoModel ReturnType { get; set; } = new ReturnTypeInfoModel();
        public bool HasMcpExposeAttribute { get; set; }
        public bool HasMcpIgnoreAttribute { get; set; }
        public string? XmlDocSummary { get; set; }
        public string? CustomToolName { get; set; }

        /// <summary>
        /// The tool's human-readable title: the action's own <c>[McpExpose(Title)]</c>, else derived from its name. Empty
        /// only for a model built outside the analyzer, which then emits no title.
        /// </summary>
        public string Title { get; set; } = "";

        public string? CustomDescription { get; set; }

        /// <summary>
        /// Whether MVC applies the <c>[ApiController]</c> conventions to the action's controller, and so infers the body
        /// as the source of a complex parameter without a binding attribute. Without them it binds such a parameter
        /// from the route, query and form values.
        /// </summary>
        public bool IsApiController { get; set; }

        public McpScopeModel RequiredScope { get; set; } = McpScopeModel.Read;
        public ToolAnnotationsModel Annotations { get; set; } = new ToolAnnotationsModel();

        /// <summary>
        /// The success response type as a <c>typeof</c> operand, or null when the tool advertises no output schema.
        /// </summary>
        public string? OutputSchemaType { get; set; }

        /// <summary>
        /// Whether the action can succeed without a body, so the output schema has to allow null. True unless proven
        /// otherwise: a schema that allows null is never wrong, one that does not breaks the call that sends none.
        /// </summary>
        public bool IsOutputNullable { get; set; } = true;

        /// <summary>
        /// Where the action is declared in source, which an inherited action shares with every controller that inherits
        /// it, or null for an action declared in a referenced assembly.
        /// </summary>
        public SourceLocationModel? SourceLocation { get; set; }

        public string ToolClassName => $"{ControllerName}_{Name}Tool";
    }

    /// <summary>
    /// A place in a source file, as plain values: a <c>Location</c> would hold its syntax tree, and the compilation
    /// with it, in the generator's pipeline from one compilation to the next.
    /// </summary>
    internal sealed class SourceLocationModel
    {
        public string FilePath { get; set; } = "";
        public TextSpan TextSpan { get; set; }
        public LinePositionSpan LineSpan { get; set; }

        /// <summary>
        /// The place of a source location, or null for one outside source, such as in a referenced assembly.
        /// </summary>
        public static SourceLocationModel? From(Location? location)
        {
            if (location?.SourceTree is null)
            {
                return null;
            }

            var lineSpan = location.GetLineSpan();
            return new SourceLocationModel
            {
                FilePath = lineSpan.Path,
                TextSpan = location.SourceSpan,
                LineSpan = lineSpan.Span
            };
        }

        /// <summary>
        /// The location in the compilation's syntax tree of the file: the compiler applies the file's
        /// <c>#pragma warning</c> and <c>.editorconfig</c> severities to a diagnostic through that tree, and to one
        /// pointing at a bare file path it applies neither. A file location only when no tree has the file's path.
        /// </summary>
        public Location ToLocation(Compilation compilation)
        {
            var tree = compilation.SyntaxTrees.FirstOrDefault(x => x.FilePath == FilePath);

            return tree is null
                ? Location.Create(FilePath, TextSpan, LineSpan)
                : Location.Create(tree, TextSpan);
        }
    }

    /// <summary>
    /// A tool's four hints. Each starts at the MCP spec's default for an omitted hint, the cautious reading, so an action
    /// whose hints were never derived still makes a client confirm before running it.
    /// </summary>
    internal sealed class ToolAnnotationsModel
    {
        public bool IsReadOnly { get; set; }
        public bool IsDestructive { get; set; } = true;
        public bool IsIdempotent { get; set; }
        public bool IsOpenWorld { get; set; } = true;
    }

    internal sealed class ParameterInfoModel
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsNullable { get; set; }

        /// <summary>
        /// Whether the type is a struct, <c>Nullable&lt;T&gt;</c> included, which the tool has to make nullable to give it
        /// the default <c>null</c>.
        /// </summary>
        public bool IsValueType { get; set; }

        public bool HasDefaultValue { get; set; }

        /// <summary>
        /// The action's default value as a C# expression of the parameter's type, or null when it declares none.
        /// </summary>
        public string? DefaultValueExpression { get; set; }

        public ParameterSourceModel Source { get; set; }

        /// <summary>
        /// Whether MVC reads the parameter's type from one string, through a type converter or a static <c>TryParse</c>,
        /// as it does a primitive or a strongly-typed id. Only such a parameter binds from the query string without
        /// <c>[FromQuery]</c>; any other is complex, bound property by property.
        /// </summary>
        public bool IsSimpleType { get; set; }

        public ImmutableArray<PropertyInfoModel> Properties { get; set; } = ImmutableArray<PropertyInfoModel>.Empty;

        /// <summary>
        /// Whether a property of the parameter's type names a binding source of its own. <c>[ApiController]</c> then
        /// infers no source for the parameter, so MVC binds it from the route, query and form values, not the body.
        /// </summary>
        public bool HasBindingSourceProperties => Properties.Any(x => x.NamesBindingSource);

        public string? Description { get; set; }

        /// <summary>
        /// The key a <c>[FromQuery(Name = "...")]</c> makes MVC bind the parameter from, or null when it names none.
        /// </summary>
        public string? QueryName { get; set; }
    }

    internal sealed class PropertyInfoModel
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsNullable { get; set; }

        /// <summary>
        /// The key a binding attribute such as <c>[FromQuery(Name = "...")]</c> makes MVC bind the property from, or null
        /// when it names none.
        /// </summary>
        public string? ModelName { get; set; }

        /// <summary>
        /// Where the property's own binding attribute makes MVC read it from: <see cref="ParameterSourceModel.Route"/> or
        /// <see cref="ParameterSourceModel.Query"/>, <see cref="ParameterSourceModel.Other"/> for any other source, and
        /// <see cref="ParameterSourceModel.Auto"/> when it has none and is read wherever its parameter is.
        /// </summary>
        public ParameterSourceModel Source { get; set; }

        /// <summary>
        /// Whether a record's primary constructor sets the property. MVC then binds it through the constructor parameter,
        /// whose binding attributes <see cref="ModelName"/> and <see cref="Source"/> come from.
        /// </summary>
        public bool IsSetByConstructor { get; set; }

        /// <summary>
        /// Whether the property's binding source keeps <c>[ApiController]</c> from reading its type from the body. MVC
        /// leaves the properties a record's constructor sets out of that check (<c>ModelMetadata.BoundProperties</c>).
        /// </summary>
        public bool NamesBindingSource => !IsSetByConstructor && Source != ParameterSourceModel.Auto;
    }

    internal enum ParameterSourceModel
    {
        Auto,
        Route,
        Query,
        Body,
        Header,
        Services,

        /// <summary>
        /// A binding source a tool does not send, such as a header, a form field or a service, of a property.
        /// </summary>
        Other
    }

    internal sealed class ReturnTypeInfoModel
    {
        public string FullTypeName { get; set; } = "";
        public bool IsAsync { get; set; }
        public string UnwrappedType { get; set; } = "";
    }
}
