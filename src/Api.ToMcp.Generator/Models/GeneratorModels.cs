using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Api.ToMcp.Generator.Models
{
    // The models are records, compared by value, arrays included: Roslyn reuses a pipeline step's output from the run
    // before when the step's input equals that run's (https://github.com/Kebechet/Api.ToMcp/issues/46). For the same
    // reason they hold no symbol, syntax node or Location, which never equal those of another compilation.
    internal sealed record GeneratorConfigModel
    {
        public int SchemaVersion { get; init; } = 1;
        public SelectionModeModel Mode { get; init; } = SelectionModeModel.SelectedOnly;
        public EquatableArray<string> Include { get; init; } = EquatableArray<string>.Empty;
        public EquatableArray<string> Exclude { get; init; } = EquatableArray<string>.Empty;

        /// <summary>
        /// Where each entry of <see cref="Include"/>, at the same index, is written in the file: the span of its string,
        /// or an empty span at the file's start when the parser cannot place it. Empty when the list is.
        /// </summary>
        public EquatableArray<LinePositionSpan> IncludeSpans { get; init; } = EquatableArray<LinePositionSpan>.Empty;

        /// <summary>
        /// Where each entry of <see cref="Exclude"/> is written in the file, as <see cref="IncludeSpans"/> are.
        /// </summary>
        public EquatableArray<LinePositionSpan> ExcludeSpans { get; init; } = EquatableArray<LinePositionSpan>.Empty;

        public NamingConfigModel Naming { get; init; } = new NamingConfigModel();
        public bool StructuredOutput { get; init; } = true;

        /// <summary>
        /// The generator.json this config was read from, or null for the default config of a project without one.
        /// </summary>
        public string? FilePath { get; init; }

        /// <summary>
        /// Why the generator.json could not be used as written, or null when it could. The config then holds what the
        /// generator falls back to.
        /// </summary>
        public ConfigErrorModel? Error { get; init; }
    }

    internal sealed record ConfigErrorModel
    {
        /// <summary>
        /// What the generator uses instead, as a clause of MCP001's message.
        /// </summary>
        public string Fallback { get; init; } = "";

        public string Reason { get; init; } = "";

        /// <summary>
        /// Where in the file the problem is, or its start when the parser does not say.
        /// </summary>
        public LinePosition Position { get; init; }
    }

    internal enum SelectionModeModel
    {
        SelectedOnly,
        AllExceptExcluded
    }

    internal sealed record NamingConfigModel
    {
        public string ToolNameFormat { get; init; } = "{Controller}_{Action}";
        public bool RemoveControllerSuffix { get; init; } = true;
    }

    internal sealed record ControllerInfoModel
    {
        public string Name { get; init; } = "";
        public string Namespace { get; init; } = "";
        public string? RoutePrefix { get; init; }
        public bool HasMcpExposeAttribute { get; init; }
        public bool HasMcpIgnoreAttribute { get; init; }
        public EquatableArray<ActionInfoModel> Actions { get; init; } = EquatableArray<ActionInfoModel>.Empty;

        /// <summary>
        /// The classes the controller inherits actions from, nearest first, up to the framework's <c>ControllerBase</c>.
        /// </summary>
        public EquatableArray<BaseClassModel> BaseClasses { get; init; } = EquatableArray<BaseClassModel>.Empty;
    }

    /// <summary>
    /// A class a controller inherits actions from, which MCP008 names when an include or exclude entry names the base
    /// instead of the controllers that inherit from it.
    /// </summary>
    internal sealed record BaseClassModel
    {
        public string Name { get; init; } = "";

        /// <summary>
        /// The names of the methods the class declares, each once.
        /// </summary>
        public EquatableArray<string> MethodNames { get; init; } = EquatableArray<string>.Empty;
    }

    internal sealed record ActionInfoModel
    {
        public string Name { get; init; } = "";
        public string ControllerName { get; init; } = "";
        public string HttpMethod { get; init; } = "";
        public string RouteTemplate { get; init; } = "";

        /// <summary>
        /// Why the generator cannot read the route or HTTP method MVC serves the action at from its routing attributes, as
        /// a clause of MCP009's message, or null when it can.
        /// </summary>
        public string? RouteProblem { get; init; }

        public EquatableArray<ParameterInfoModel> Parameters { get; init; } = EquatableArray<ParameterInfoModel>.Empty;
        public ReturnTypeInfoModel ReturnType { get; init; } = new ReturnTypeInfoModel();
        public bool HasMcpExposeAttribute { get; init; }
        public bool HasMcpIgnoreAttribute { get; init; }
        public string? XmlDocSummary { get; init; }
        public string? CustomToolName { get; init; }

        /// <summary>
        /// The tool's human-readable title: the action's own <c>[McpExpose(Title)]</c>, else derived from its name. Empty
        /// only for a model built outside the analyzer, which then emits no title.
        /// </summary>
        public string Title { get; init; } = "";

        public string? CustomDescription { get; init; }

        /// <summary>
        /// Whether MVC applies the <c>[ApiController]</c> conventions to the action's controller, and so infers the body
        /// as the source of a complex parameter without a binding attribute. Without them it binds such a parameter
        /// from the route, query and form values.
        /// </summary>
        public bool IsApiController { get; init; }

        public McpScopeModel RequiredScope { get; init; } = McpScopeModel.Read;
        public ToolAnnotationsModel Annotations { get; init; } = new ToolAnnotationsModel();

        /// <summary>
        /// The success response type as a <c>typeof</c> operand, or null when the tool advertises no output schema.
        /// </summary>
        public string? OutputSchemaType { get; init; }

        /// <summary>
        /// Whether the action can succeed without a body, so the output schema has to allow null. True unless proven
        /// otherwise: a schema that allows null is never wrong, one that does not breaks the call that sends none.
        /// </summary>
        public bool IsOutputNullable { get; init; } = true;

        /// <summary>
        /// Where the action is declared in source, which an inherited action shares with every controller that inherits
        /// it, or null for an action declared in a referenced assembly.
        /// </summary>
        public SourceLocationModel? SourceLocation { get; init; }

        public string ToolClassName => $"{ControllerName}_{Name}Tool";
    }

    /// <summary>
    /// What the generator makes of the controllers and the config: the tools to generate, in the order the registration
    /// lists them, and the diagnostics to report.
    /// </summary>
    internal sealed record GenerationPlanModel
    {
        public EquatableArray<ToolSourceModel> Tools { get; init; } = EquatableArray<ToolSourceModel>.Empty;
        public EquatableArray<DiagnosticInfoModel> Diagnostics { get; init; } = EquatableArray<DiagnosticInfoModel>.Empty;
    }

    /// <summary>
    /// What the source of one tool is generated from: the action, without the place it is declared at, which only
    /// diagnostics point at, so that an edit that moves an action generates no source again; the tool's class name; and
    /// the parts of the config the tool's source reads.
    /// </summary>
    internal sealed record ToolSourceModel
    {
        public string ClassName { get; init; } = "";
        public ActionInfoModel Action { get; init; } = new ActionInfoModel();
        public NamingConfigModel Naming { get; init; } = new NamingConfigModel();
        public bool StructuredOutput { get; init; }
    }

    /// <summary>
    /// A place in a source file, as plain values: a <c>Location</c> would hold its syntax tree, and the compilation
    /// with it, in the generator's pipeline from one compilation to the next.
    /// </summary>
    internal sealed record SourceLocationModel
    {
        public string FilePath { get; init; } = "";
        public TextSpan TextSpan { get; init; }
        public LinePositionSpan LineSpan { get; init; }

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
    internal sealed record ToolAnnotationsModel
    {
        public bool IsReadOnly { get; init; }
        public bool IsDestructive { get; init; } = true;
        public bool IsIdempotent { get; init; }
        public bool IsOpenWorld { get; init; } = true;
    }

    internal sealed record ParameterInfoModel
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public bool IsNullable { get; init; }

        /// <summary>
        /// Whether the type is a struct, <c>Nullable&lt;T&gt;</c> included, which the tool has to make nullable to give it
        /// the default <c>null</c>.
        /// </summary>
        public bool IsValueType { get; init; }

        public bool HasDefaultValue { get; init; }

        /// <summary>
        /// The action's default value as a C# expression of the parameter's type, or null when it declares none.
        /// </summary>
        public string? DefaultValueExpression { get; init; }

        public ParameterSourceModel Source { get; init; }

        /// <summary>
        /// Whether MVC reads the parameter's type from one string, through a type converter or a static <c>TryParse</c>,
        /// as it does a primitive or a strongly-typed id. Only such a parameter binds from the query string without
        /// <c>[FromQuery]</c>; any other is complex, bound property by property.
        /// </summary>
        public bool IsSimpleType { get; init; }

        /// <summary>
        /// Whether the type is an array or another enumerable but a string, whose items a query string repeats the key for.
        /// </summary>
        public bool IsCollection { get; init; }

        public EquatableArray<PropertyInfoModel> Properties { get; init; } = EquatableArray<PropertyInfoModel>.Empty;

        /// <summary>
        /// Whether a property of the parameter's type names a binding source of its own. <c>[ApiController]</c> then
        /// infers no source for the parameter, so MVC binds it from the route, query and form values, not the body.
        /// </summary>
        public bool HasBindingSourceProperties => Properties.Any(x => x.NamesBindingSource);

        public string? Description { get; init; }

        /// <summary>
        /// The key a <c>[FromQuery(Name = "...")]</c> makes MVC bind the parameter from, or null when it names none.
        /// </summary>
        public string? QueryName { get; init; }
    }

    internal sealed record PropertyInfoModel
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public bool IsNullable { get; init; }

        /// <summary>
        /// The key a binding attribute such as <c>[FromQuery(Name = "...")]</c> makes MVC bind the property from, or null
        /// when it names none.
        /// </summary>
        public string? ModelName { get; init; }

        /// <summary>
        /// Where the property's own binding attribute makes MVC read it from: <see cref="ParameterSourceModel.Route"/> or
        /// <see cref="ParameterSourceModel.Query"/>, <see cref="ParameterSourceModel.Other"/> for any other source, and
        /// <see cref="ParameterSourceModel.Auto"/> when it has none and is read wherever its parameter is.
        /// </summary>
        public ParameterSourceModel Source { get; init; }

        /// <summary>
        /// Whether a record's primary constructor sets the property. MVC then binds it through the constructor parameter,
        /// whose binding attributes <see cref="ModelName"/> and <see cref="Source"/> come from.
        /// </summary>
        public bool IsSetByConstructor { get; init; }

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
        /// A model binder of the parameter's own, named by <c>[ModelBinder(typeof(...))]</c> on it or on its type, which
        /// reads the route, query and form values under the parameter's model name.
        /// </summary>
        Custom,

        /// <summary>
        /// A binding source a tool does not send, such as a header, a form field or a service, of a property.
        /// </summary>
        Other
    }

    internal sealed record ReturnTypeInfoModel
    {
        public string FullTypeName { get; init; } = "";
        public bool IsAsync { get; init; }
        public string UnwrappedType { get; init; } = "";
    }
}
