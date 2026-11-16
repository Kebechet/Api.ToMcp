namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The primary handler of the invoker's <see cref="HttpClient"/>, in place of the one the app or the client factory gives
/// it. <see cref="InProcessApiHandler"/> above it serves the invoker's requests in process and refuses any other, so a
/// request that reaches this handler is refused rather than sent over the network, to the host the MCP request named.
/// </summary>
internal sealed class RefusingPrimaryHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            $"The tool's API request {request.Method} {request.RequestUri} reached the primary handler of the invoker's HttpClient, which never sends a request " +
            $"over the network: a tool's request runs in process only, through {nameof(InProcessApiHandler)}, which is missing from the client's handlers.");
    }
}
