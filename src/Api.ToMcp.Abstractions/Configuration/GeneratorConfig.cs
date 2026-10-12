namespace Api.ToMcp.Abstractions.Configuration;

public sealed class GeneratorConfig
{
    public int SchemaVersion { get; set; } = 1;
    public SelectionMode Mode { get; set; } = SelectionMode.SelectedOnly;
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public NamingConfig Naming { get; set; } = new();

    /// <summary>
    /// Whether generated tools advertise an output schema from the action's response type and return structured
    /// content. On by default; turn it off when the API serializes with Newtonsoft.Json, whose attributes the schema
    /// cannot see.
    /// </summary>
    public bool StructuredOutput { get; set; } = true;
}

public enum SelectionMode
{
    SelectedOnly,
    AllExceptExcluded
}

public sealed class NamingConfig
{
    public string ToolNameFormat { get; set; } = "{Controller}_{Action}";
    public bool RemoveControllerSuffix { get; set; } = true;
}
