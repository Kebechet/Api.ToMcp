using Microsoft.Extensions.Http;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// What the client factory creates <see cref="McpHttpInvoker"/> with when the app registers it as a typed client, as
/// <c>services.AddHttpClient&lt;IMcpHttpInvoker, McpHttpInvoker&gt;()</c> does, which tests did to hand the invoker a
/// <c>TestServer</c> handler. Registered for <see cref="McpHttpInvoker"/> alone, it takes the place of the factory's own,
/// which would fail on the invoker's missing public constructor without saying what to do. <c>AddMcpTools</c> creates its
/// invoker without it.
/// </summary>
internal sealed class TypedInvokerClientRefusal : ITypedHttpClientFactory<McpHttpInvoker>
{
    public McpHttpInvoker CreateClient(HttpClient httpClient)
    {
        throw new InvalidOperationException(
            $"{nameof(McpHttpInvoker)} is registered as a typed client, as services.AddHttpClient<{nameof(IMcpHttpInvoker)}, {nameof(McpHttpInvoker)}>() does, " +
            $"which replaces the invoker AddMcpTools registers. Only AddMcpTools creates {nameof(McpHttpInvoker)}, and it runs every tool's API request in process, " +
            $"so remove that call: a test under WebApplicationFactory needs no TestServer handler for the tools. To configure the invoker's HttpClient, " +
            $"configure it by its name, as in services.AddHttpClient(nameof({nameof(IMcpHttpInvoker)})).AddHttpMessageHandler(...).");
    }
}
