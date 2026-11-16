using Microsoft.Extensions.Http;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Adds <see cref="InProcessApiHandler"/> to the invoker's client as its last handler, and puts
/// <see cref="RefusingPrimaryHandler"/> below it as the primary handler, so no request of the client leaves the process.
/// <c>AddMcpTools</c> registers it as the first filter, which the client factory runs last: after the client's builder
/// actions, which add the app's handlers and set its primary handler whether the app configures the client before or after
/// <c>AddMcpTools</c>, after the loggers of <c>AddLogger</c>, and after every other filter, such as the one that adds the
/// factory's logging handlers and the one that gives the primary handler the app's meter factory.
/// </summary>
internal sealed class InProcessApiHandlerFilter : IHttpMessageHandlerBuilderFilter
{
    private readonly string _clientName;
    private readonly InProcessApiPipeline _pipeline;

    public InProcessApiHandlerFilter(string clientName, InProcessApiPipeline pipeline)
    {
        _clientName = clientName;
        _pipeline = pipeline;
    }

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        return builder =>
        {
            next(builder);
            if (string.Equals(builder.Name, _clientName, StringComparison.Ordinal))
            {
                // A handler belongs to one chain, and the factory builds the client's chain again after each handler
                // lifetime. The primary handler replaced here is left undisposed, as the app may share it with other
                // clients.
                builder.PrimaryHandler = new RefusingPrimaryHandler();
                builder.AdditionalHandlers.Add(new InProcessApiHandler(_pipeline));
            }
        };
    }
}
