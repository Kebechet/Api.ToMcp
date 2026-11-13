using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        /// Whether <see cref="SelectionModeModel.AllExceptExcluded"/> mode selects Minimal API endpoints as it selects
        /// actions, or null when the file does not say, which means it does not. Off by default, so updating the package
        /// exposes no endpoint the project did not choose; an include entry or <c>[McpExpose]</c> selects one whatever it
        /// says.
        /// </summary>
        public bool? MinimalApis { get; init; }

        /// <summary>
        /// The generator.json this config was read from, or null for the default config of a project without one.
        /// </summary>
        public string? FilePath { get; init; }

        /// <summary>
        /// The full path of ApiToMcpConfigPath, which the package's build targets pass, or null for a project that does
        /// not import them.
        /// </summary>
        public string? ConfiguredFilePath { get; init; }

        /// <summary>
        /// The other additional files named generator.json, which the generator passes over for the file at
        /// <see cref="ConfiguredFilePath"/>. It read the first of them before the build passed that path.
        /// </summary>
        public EquatableArray<string> PassedOverFilePaths { get; init; } = EquatableArray<string>.Empty;

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
        /// The names of every action MVC serves on the controller, its inherited ones included: also those no tool calls,
        /// served for HEAD or OPTIONS only, for every method or by conventional routing, which an entry names all the same.
        /// </summary>
        public EquatableArray<string> ActionNames { get; init; } = EquatableArray<string>.Empty;

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
        /// Every route MVC serves a controller's action at, the one the tool calls included, whose template the generator
        /// can read: with <c>[HttpGet("{id}")]</c> and <c>[HttpGet]</c>, the controller's route as well. Empty for a
        /// Minimal API endpoint, served at <see cref="RouteTemplate"/> alone.
        /// </summary>
        public EquatableArray<ServedRouteModel> ServedRoutes { get; init; } = EquatableArray<ServedRouteModel>.Empty;

        /// <summary>
        /// The routes MVC serves a controller's action at under each route of its controller but the first, whose
        /// template the generator can read: only to tell the values the tool cannot send (MCP014).
        /// </summary>
        public EquatableArray<ServedRouteModel> OtherControllerRoutes { get; init; } = EquatableArray<ServedRouteModel>.Empty;

        /// <summary>
        /// Whether a call may leave out the route value that ends <see cref="RouteTemplate"/>, which the routes of the
        /// other actions decide. See <see cref="RouteEndModel"/>.
        /// </summary>
        public RouteEndModel RouteEnd { get; init; }

        /// <summary>
        /// Why the generator cannot read the route or HTTP method MVC serves the action at from its routing attributes, as
        /// a clause of MCP009's message, or null when it can.
        /// </summary>
        public string? RouteProblem { get; init; }

        /// <summary>
        /// Why a tool cannot send what the action reads from the request, such as a header a tool never sets or a form
        /// file, as a clause of MCP009's message, or null when it can. Unlike <see cref="RouteProblem"/>, the route is known,
        /// so the action still counts when the routes of other actions are compared.
        /// </summary>
        public string? ParameterProblem { get; init; }

        public EquatableArray<ParameterInfoModel> Parameters { get; init; } = EquatableArray<ParameterInfoModel>.Empty;

        /// <summary>
        /// Whether a parameter's type is not public, which only a Minimal API handler can take: its tool is then an
        /// internal class, as a public tool method could not name the type.
        /// </summary>
        public bool HasNonPublicParameterTypes { get; init; }

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
        /// The method declaration whose body can prove <see cref="IsOutputNullable"/> false, or null when nothing can. The
        /// proof binds the whole body, so it waits for the config: the generator runs it only for an action that becomes a
        /// tool with an output schema. The tool's source model drops it after the proof, as it holds the place of the
        /// declaration, which an edit that only moves the action changes.
        /// </summary>
        public BodyToAnalyzeModel? BodyToAnalyze { get; init; }

        /// <summary>
        /// Where the action is declared in source, which an inherited action shares with every controller that inherits
        /// it; for an action declared in a referenced assembly, the controller's name. For a Minimal API endpoint, the
        /// <c>Map*</c> call that maps it.
        /// </summary>
        public SourceLocationModel? SourceLocation { get; init; }

        /// <summary>
        /// Whether the action is a Minimal API endpoint, mapped by a call such as <c>app.MapGet(...)</c>, rather than an
        /// action of a controller. <see cref="ControllerName"/> is then the class that declares its handler.
        /// </summary>
        public bool IsMinimalApiEndpoint { get; init; }

        /// <summary>
        /// The description a Minimal API endpoint's own metadata gives it: <c>.WithDescription</c> or
        /// <c>[EndpointDescription]</c>, else <c>.WithSummary</c> or <c>[EndpointSummary]</c>, else <c>[Description]</c> on
        /// its handler; null for a controller action.
        /// </summary>
        public string? EndpointDescription { get; init; }

        /// <summary>
        /// The other HTTP methods a tool sends that a Minimal API endpoint is mapped for, which its tool, calling it with
        /// <see cref="HttpMethod"/>, never uses.
        /// </summary>
        public EquatableArray<string> OtherHttpMethods { get; init; } = EquatableArray<string>.Empty;

        /// <summary>
        /// A Minimal API endpoint's name is any string, from <c>.WithName</c>, so the characters a C# identifier cannot
        /// hold become underscores.
        /// </summary>
        public string ToolClassName => $"{ToIdentifierPart(ControllerName)}_{ToIdentifierPart(Name)}Tool";

        private static string ToIdentifierPart(string name)
        {
            var characters = name.ToCharArray();
            for (var i = 0; i < characters.Length; i++)
            {
                if (!SyntaxFacts.IsIdentifierPartCharacter(characters[i]))
                {
                    characters[i] = '_';
                }
            }

            return new string(characters);
        }
    }

    /// <summary>
    /// The Minimal API endpoints the config could select, the <c>Map*</c> calls of each class that declares handlers as one
    /// controller-like model each, and how many endpoints the generator did not analyze, as nothing in the config or the
    /// code selects them.
    /// </summary>
    internal sealed record EndpointsModel
    {
        public EquatableArray<ControllerInfoModel> Classes { get; init; } = EquatableArray<ControllerInfoModel>.Empty;
        public int LeftOutCount { get; init; }
    }

    /// <summary>
    /// What the generator makes of the controllers and the config: the tools to generate, in the order the registration
    /// lists them, and the diagnostics to report.
    /// </summary>
    internal sealed record GenerationPlanModel
    {
        public EquatableArray<ToolSourceModel> Tools { get; init; } = EquatableArray<ToolSourceModel>.Empty;
        public EquatableArray<DiagnosticInfoModel> Diagnostics { get; init; } = EquatableArray<DiagnosticInfoModel>.Empty;

        /// <summary>
        /// The method declarations whose bodies can prove a tool's output schema need not allow null, each once: those of
        /// the tools that get an output schema.
        /// </summary>
        public EquatableArray<BodyToAnalyzeModel> BodiesToAnalyze { get; init; } = EquatableArray<BodyToAnalyzeModel>.Empty;
    }

    /// <summary>
    /// A method declaration whose body can prove that an action never succeeds without a body.
    /// </summary>
    internal sealed record BodyToAnalyzeModel
    {
        /// <summary>
        /// The declaration's documentation ID, which finds the method in the compilation the proof runs on.
        /// </summary>
        public string MethodId { get; init; } = "";

        /// <summary>
        /// Where the declaration's name is, which finds the declaration when its documentation ID leads back to no method
        /// or to several.
        /// </summary>
        public SourceLocationModel Declaration { get; init; } = new SourceLocationModel();
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
        /// The source text the location spans, such as an action's name, which tells apart two files with the same path.
        /// </summary>
        public string Text { get; init; } = "";

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
                LineSpan = lineSpan.Span,
                Text = location.SourceTree.GetText().ToString(location.SourceSpan)
            };
        }

        /// <summary>
        /// The location in the compilation's syntax tree of the file: the compiler applies the file's
        /// <c>#pragma warning</c> and <c>.editorconfig</c> severities to a diagnostic through that tree, and to one
        /// pointing at a bare file path it applies neither. Several trees can share a path, as files without one do, so
        /// it is the tree whose text at the span is the location's; a span past a tree's end would make Roslyn throw and
        /// drop the generator's output. A file location only when no tree of the path holds the span.
        /// </summary>
        public Location ToLocation(Compilation compilation)
        {
            var trees = compilation.SyntaxTrees
                .Where(x => x.FilePath == FilePath && TextSpan.End <= x.Length)
                .ToList();
            var tree = trees.FirstOrDefault(x => x.GetText().ToString(TextSpan) == Text) ?? trees.FirstOrDefault();

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

        /// <summary>
        /// Whether MVC requires a value for the parameter, through <c>[BindRequired]</c> or <c>[Required]</c>, and answers 400
        /// without one, though the action declares a default.
        /// </summary>
        public bool IsBindingRequired { get; init; }

        /// <summary>
        /// Whether the generator cannot tell if MVC requires a value for the parameter, as its binding behavior attribute
        /// comes from a referenced assembly, whose constructor code metadata does not hold.
        /// </summary>
        public bool IsBindingRequirementUnknown { get; init; }

        /// <summary>
        /// Whether MVC refuses the action's default value when the tool sends it as text, as it does an enum value that no
        /// member has.
        /// </summary>
        public bool IsDefaultRefusedAsText { get; init; }

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
        /// The name MVC binds the parameter under, or null when it binds it under its own: the name the first of its
        /// attributes, then of its type's, implementing <c>IModelNameProvider</c> gives, such as <c>[FromQuery(Name = "q")]</c>,
        /// <c>[FromRoute(Name = "productId")]</c>, <c>[ModelBinder(Name = "q")]</c> or <c>[Bind(Prefix = "f")]</c>. It is the
        /// query key, the header, the form key, the route value the parameter fills and the prefix of its properties; the
        /// tool's argument keeps the parameter's own name. Of a Minimal API endpoint, the <c>Name</c> of its <c>[FromQuery]</c>
        /// or <c>[FromRoute]</c>, and the header of its <c>[FromHeader]</c>.
        /// </summary>
        public string? ModelName { get; init; }
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
        /// Where the property's own binding attribute makes MVC read it from: <see cref="ParameterSourceModel.Route"/>,
        /// <see cref="ParameterSourceModel.Query"/> or <see cref="ParameterSourceModel.Form"/>,
        /// <see cref="ParameterSourceModel.Other"/> for any other source, and
        /// <see cref="ParameterSourceModel.Auto"/> when it has none and is read wherever its parameter is.
        /// </summary>
        public ParameterSourceModel Source { get; init; }

        /// <summary>
        /// Whether a record's primary constructor sets the property. MVC then binds it through the constructor parameter,
        /// whose binding attributes <see cref="ModelName"/> and <see cref="Source"/> come from.
        /// </summary>
        public bool IsSetByConstructor { get; init; }

        /// <summary>
        /// Whether MVC's complex binder binds the property: no binding behavior makes it <c>Never</c>, and it has a public
        /// setter, a record's constructor sets it, or MVC can update it in place. Only MCP014 reads it; the tool sends
        /// every property as it did.
        /// </summary>
        public bool IsBindable { get; init; } = true;

        /// <summary>
        /// Whether the property's binding source keeps <c>[ApiController]</c> from reading its type from the body. MVC
        /// leaves the properties a record's constructor sets out of that check (<c>ModelMetadata.BoundProperties</c>).
        /// </summary>
        public bool NamesBindingSource => !IsSetByConstructor && Source != ParameterSourceModel.Auto;
    }

    /// <summary>
    /// A route MVC serves an action at.
    /// </summary>
    internal sealed record ServedRouteModel
    {
        public string Template { get; init; } = "";

        /// <summary>
        /// The HTTP methods MVC serves the route for, or none for every method: a <c>[Route]</c> alone, or one whose
        /// methods the generator cannot read.
        /// </summary>
        public EquatableArray<string> HttpMethods { get; init; } = EquatableArray<string>.Empty;
    }

    /// <summary>
    /// Whether a call may leave out the route value that ends an action's template, so that its URL ends a segment
    /// earlier. That URL can be another action's, of the same HTTP method.
    /// </summary>
    internal enum RouteEndModel
    {
        /// <summary>
        /// As the template says: <c>{id}</c> needs a value, and <c>{id?}</c>, <c>{id=5}</c> or a catch-all can be left out.
        /// </summary>
        AsTemplate,

        /// <summary>
        /// Can be left out though the template needs a value: another route of the same action serves the URL without it.
        /// </summary>
        Optional,

        /// <summary>
        /// Needs a value though the template lets the URL leave it out: another action may serve the URL without it.
        /// </summary>
        Required
    }

    internal enum ParameterSourceModel
    {
        Auto,
        Route,
        Query,
        Body,
        Header,

        /// <summary>
        /// The form content, which the tool sends as <c>application/x-www-form-urlencoded</c>.
        /// </summary>
        Form,

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
