namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Whether the invoker's <see cref="HttpClient"/> has <see cref="InProcessApiHandler"/> among its handlers, which
/// <see cref="InProcessApiHandlerFilter"/> adds. App code can take that filter away, as
/// <c>services.RemoveAll&lt;IHttpMessageHandlerBuilderFilter&gt;()</c> after <c>AddMcpTools</c> does, and a request the
/// invoker marks to be served in process would then reach the primary handler and leave the process. The client factory
/// builds the client's handlers from the same configuration every time, so the check looks at them once.
/// </summary>
internal sealed class InProcessApiHandlerCheck
{
    private readonly Lazy<bool> _isInTheClientsChain;

    /// <param name="createChain">Creates the client's handlers, as the client factory does: the outermost first.</param>
    public InProcessApiHandlerCheck(Func<HttpMessageHandler> createChain)
    {
        _isInTheClientsChain = new Lazy<bool>(() => HasInProcessApiHandler(createChain()));
    }

    /// <summary>
    /// Whether a request the invoker marks reaches <see cref="InProcessApiHandler"/>, which serves it in process, before
    /// it can reach the client's primary handler.
    /// </summary>
    public bool IsInTheClientsChain => _isInTheClientsChain.Value;

    private static bool HasInProcessApiHandler(HttpMessageHandler chain)
    {
        var handler = chain as DelegatingHandler;
        while (handler is not null)
        {
            if (handler is InProcessApiHandler)
            {
                return true;
            }

            handler = handler.InnerHandler as DelegatingHandler;
        }

        return false;
    }
}
