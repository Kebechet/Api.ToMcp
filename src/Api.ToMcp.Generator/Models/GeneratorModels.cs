using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Api.ToMcp.Generator.Models
{
    internal sealed class GeneratorConfigModel
    {
        public int SchemaVersion { get; set; } = 1;
        public SelectionModeModel Mode { get; set; } = SelectionModeModel.SelectedOnly;
        public ImmutableArray<string> Include { get; set; } = ImmutableArray<string>.Empty;
        public ImmutableArray<string> Exclude { get; set; } = ImmutableArray<string>.Empty;
        public NamingConfigModel Naming { get; set; } = new NamingConfigModel();
        public bool StructuredOutput { get; set; } = true;
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

        public string ToolClassName => $"{ControllerName}_{Name}Tool";
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
        public bool HasDefaultValue { get; set; }
        public object? DefaultValue { get; set; }
        public ParameterSourceModel Source { get; set; }
        public ImmutableArray<PropertyInfoModel> Properties { get; set; } = ImmutableArray<PropertyInfoModel>.Empty;
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
    }

    internal enum ParameterSourceModel
    {
        Auto,
        Route,
        Query,
        Body,
        Header,
        Services
    }

    internal sealed class ReturnTypeInfoModel
    {
        public string FullTypeName { get; set; } = "";
        public bool IsAsync { get; set; }
        public string UnwrappedType { get; set; } = "";
    }
}
