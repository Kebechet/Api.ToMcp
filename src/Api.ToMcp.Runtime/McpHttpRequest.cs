using ModelContextProtocol.Protocol;

namespace Api.ToMcp.Runtime;

/// <summary>
/// A generated tool's API request with headers or a form, which the <see cref="IMcpHttpInvoker"/> methods cannot send:
/// its method, route, body and headers.
/// </summary>
public sealed class McpHttpRequest
{
    public McpHttpRequest(HttpMethod method, string route)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(route);

        Method = method;
        Route = route;
    }

    public HttpMethod Method { get; }

    /// <summary>
    /// The path and query string, as the <see cref="IMcpHttpInvoker"/> methods take them.
    /// </summary>
    public string Route { get; }

    /// <summary>
    /// The body as JSON, or null for none. A request has a JSON body or a form, never both.
    /// </summary>
    public string? JsonBody { get; init; }

    /// <summary>
    /// The body as an <c>application/x-www-form-urlencoded</c> form, its keys and values escaped and joined with
    /// <c>&amp;</c>, or null for none. Empty is a form without fields.
    /// </summary>
    public string? FormBody { get; init; }

    /// <summary>
    /// The request headers, each value under its header's name; a name may come several times, once per value.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>
    /// Sends the request through the invoker: through <see cref="IMcpHttpRequestInvoker.SendAsync"/> when it implements
    /// that interface, else through its <see cref="IMcpHttpInvoker"/> method for the HTTP method, which carries neither
    /// headers nor a form. A request with either gets an error result from an invoker that cannot send it.
    /// </summary>
    public Task<CallToolResult> SendAsync(IMcpHttpInvoker invoker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoker);

        var requestInvoker = invoker as IMcpHttpRequestInvoker;
        if (requestInvoker is not null)
        {
            return requestInvoker.SendAsync(this, ct);
        }

        if (Headers.Count == 0 && FormBody is null)
        {
            if (Method == HttpMethod.Get && JsonBody is null)
            {
                return invoker.GetAsync(Route, ct);
            }

            if (Method == HttpMethod.Post)
            {
                return invoker.PostAsync(Route, JsonBody, ct);
            }

            if (Method == HttpMethod.Put)
            {
                return invoker.PutAsync(Route, JsonBody, ct);
            }

            if (Method == HttpMethod.Patch)
            {
                return invoker.PatchAsync(Route, JsonBody, ct);
            }

            if (Method == HttpMethod.Delete && JsonBody is null)
            {
                return invoker.DeleteAsync(Route, ct);
            }
        }

        return Task.FromResult(new CallToolResult
        {
            IsError = true,
            Content =
            {
                new TextContentBlock
                {
                    Text = $"The tool sends a {Method.Method} request with headers or a body that the registered IMcpHttpInvoker, " +
                        $"{invoker.GetType().FullName}, cannot send: implement {nameof(IMcpHttpRequestInvoker)} as well."
                }
            }
        });
    }
}
