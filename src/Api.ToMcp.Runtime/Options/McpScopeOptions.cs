using System.ComponentModel;
using Api.ToMcp.Abstractions.Scopes;
using ModelContextProtocol.Authentication;

namespace Api.ToMcp.Runtime.Options;

/// <summary>
/// Configuration options for the tools registered by <c>AddMcpTools</c>: scope-based access control, OAuth sign-in
/// discovery and how the tool argument schemas are described. When not configured, all tools are accessible without
/// scope validation.
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

    /// <summary>
    /// Turns on OAuth sign-in discovery for the MCP endpoint, as MCP clients that sign a user in expect it: the OAuth
    /// Protected Resource Metadata (RFC 9728) served at <c>/.well-known/oauth-protected-resource/&lt;MCP path&gt;</c>, and
    /// a <c>401</c> challenge whose <c>WWW-Authenticate</c> header points to it. List the issuer of your tokens in
    /// <see cref="ProtectedResourceMetadata.AuthorizationServers"/>. Left empty, <see cref="ProtectedResourceMetadata.Resource"/>
    /// is the MCP endpoint's URL and <see cref="ProtectedResourceMetadata.ScopesSupported"/> lists the
    /// <see cref="ScopeNames"/> of the scopes the generated tools require. With <see cref="ClaimToScopeMapper"/> set, a
    /// tool call that scope validation refuses is answered with a <c>403</c> <c>insufficient_scope</c> challenge, or a
    /// <c>401</c> one when the caller is not signed in, instead of an error result. Needs <c>UseAuthentication</c> and
    /// <c>UseAuthorization</c>. Defaults to <see langword="null"/>, which leaves the MCP endpoint as it is.
    /// </summary>
    public ProtectedResourceMetadata? ResourceMetadata { get; set; }

    /// <summary>
    /// The OAuth scope that grants each <see cref="McpScope"/> flag, such as <c>mcp:read</c> for <see cref="McpScope.Read"/>:
    /// one entry for each of <see cref="McpScope.Read"/>, <see cref="McpScope.Write"/> and <see cref="McpScope.Delete"/>.
    /// With <see cref="ResourceMetadata"/> set, these are the scopes listed in <c>scopes_supported</c> and named in a scope
    /// challenge. <see cref="ClaimToScopeMapper"/> still decides what a token grants.
    /// </summary>
    public IDictionary<McpScope, string> ScopeNames { get; } = new Dictionary<McpScope, string>();
}
