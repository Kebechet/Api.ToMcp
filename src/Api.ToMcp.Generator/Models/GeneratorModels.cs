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
        public string? CustomDescription { get; set; }

        public string ToolClassName => $"{ControllerName}_{Name}Tool";
    }

    internal sealed class ParameterInfoModel
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsNullable { get; set; }
        public bool HasDefaultValue { get; set; }
        public object? DefaultValue { get; set; }
        public ParameterSourceModel Source { get; set; }
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
