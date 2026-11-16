using Api.ToMcp.Runtime.Options;

namespace Api.ToMcp.Runtime;

/// <summary>
/// Marks a request that a tool runs through the app's request pipeline in process, as it does with
/// <see cref="McpScopeOptions.AreApiCallsInProcess"/> on. The invoker sets it on the request's own features, so no
/// client can send it, unlike the <c>X-MCP-Internal-Call</c> header. A tool's request over HTTP is one the server
/// receives, and does not carry it. Read it with <see cref="McpToolRequestExtensions.IsMcpToolRequest"/>, or with
/// <c>HttpContext.Features.Get&lt;IMcpToolRequestFeature&gt;()</c> for the tool's name.
/// </summary>
public interface IMcpToolRequestFeature
{
    /// <summary>
    /// The name of the tool whose call sent the request, as the MCP client called it, or <see langword="null"/> when the
    /// invoker was called outside a tool call, as code of the app's own may call it.
    /// </summary>
    string? ToolName { get; }
}
