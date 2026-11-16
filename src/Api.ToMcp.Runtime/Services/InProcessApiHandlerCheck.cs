namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Whether the invoker's <see cref="HttpClient"/> has <see cref="InProcessApiHandler"/> among its handlers, which
/// <see cref="InProcessApiHandlerFilter"/> adds. App code can take that filter away, as
/// <c>services.RemoveAll&lt;IHttpMessageHandlerBuilderFilter&gt;()</c> after <c>AddMcpTools</c> does, or register an
/// <see cref="IHttpClientFactory"/> of its own, whose clients the filter never sees. A request the invoker marks to be
/// served in process would then reach the client's primary handler and leave the process. The client factory builds the
/// client's handlers from the same configuration every time, so the check looks at them once.
/// </summary>
internal sealed class InProcessApiHandlerCheck
{
    private readonly Lazy<bool> _isClientFromTheHandlerFactory;
    private readonly Lazy<bool> _isInTheClientsChain;

    /// <param name="isClientFromTheHandlerFactory">
    /// Whether the client factory that creates the invoker's client builds its handlers through the
    /// <c>IHttpMessageHandlerFactory</c> that <paramref name="createChain"/> asks, as the one client factory both services
    /// resolve to by default does.
    /// </param>
    /// <param name="createChain">Creates the client's handlers, as the client factory does: the outermost first.</param>
    public InProcessApiHandlerCheck(Func<bool> isClientFromTheHandlerFactory, Func<HttpMessageHandler> createChain)
    {
        _isClientFromTheHandlerFactory = new Lazy<bool>(isClientFromTheHandlerFactory);
        _isInTheClientsChain = new Lazy<bool>(() => IsClientFromTheHandlerFactory && HasInProcessApiHandler(createChain()));
    }

    /// <summary>
    /// Whether the invoker's client comes from the client factory whose handlers the check looks at. An
    /// <see cref="IHttpClientFactory"/> the app registers after <c>AddMcpTools</c> creates clients the check cannot see.
    /// </summary>
    public bool IsClientFromTheHandlerFactory => _isClientFromTheHandlerFactory.Value;

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
