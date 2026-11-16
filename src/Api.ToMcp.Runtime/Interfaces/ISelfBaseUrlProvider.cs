namespace Api.ToMcp.Runtime;

/// <summary>
/// The address that names a tool's API request to the app when the request has no MCP request to take its scheme, host
/// and path base from: a tool call outside an MCP request, as from code of the app's own, or under an MCP request without
/// a <c>Host</c>. The request still runs in process, so the address is never called; the app sees it as the request's
/// scheme and host. <c>AddMcpTools</c> registers <c>DefaultBaseUrlProvider</c>, which reads <c>McpTools:BaseUrl</c>.
/// </summary>
public interface ISelfBaseUrlProvider
{
    /// <summary>
    /// The scheme, host and port, and any path, that go in front of the tool's route, without a trailing <c>/</c>.
    /// </summary>
    string GetBaseUrl();
}
