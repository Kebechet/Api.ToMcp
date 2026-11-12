using System.ComponentModel;
using Api.ToMcp.Abstractions.Scopes;

namespace Api.ToMcp.Runtime.Options;

/// <summary>
/// Configuration options for the tools registered by <c>AddMcpTools</c>: scope-based access control and how the
/// tool argument schemas are described. When not configured, all tools are accessible without scope validation.
/// </summary>
public class McpScopeOptions
{
    /// <summary>
    /// Maps a claim value to McpScope. If null, scope checking is disabled (default).
    /// </summary>
    public Func<string, McpScope>? ClaimToScopeMapper { get; set; }

    /// <summary>
    /// Name of the claim to read scopes from (e.g., "scope", "permissions").
    /// Defaults to "scope".
    /// </summary>
    public string ClaimName { get; set; } = "scope";

    /// <summary>
    /// Describes every property of a tool argument type (a request DTO, a nested object, an array item) that carries
    /// no <see cref="DescriptionAttribute"/> from the property's XML <c>&lt;summary&gt;</c>, read when the host starts
    /// from the <c>&lt;AssemblyName&gt;.xml</c> file next to the assembly that declares the property. A
    /// <see cref="DescriptionAttribute"/> always wins. An assembly without that file is reported with a warning at
    /// startup. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ArePropertyDescriptionsFromXmlDocs { get; set; } = true;

    /// <summary>
    /// Whether a generated tool serves its API request inside this process, through the request pipeline the host built
    /// for the server, so it passes every middleware a request the server receives passes, with no socket and no address
    /// to call. When <see langword="false"/>, or when the host built no pipeline, the request goes over HTTP to the
    /// address <see cref="ISelfBaseUrlProvider"/> gives. Defaults to <see langword="true"/>.
    /// </summary>
    public bool AreApiCallsInProcess { get; set; } = true;
}
