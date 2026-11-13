using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Warns, once per invoker type, when a generated tool that sends request headers or a form is called through a
/// registered <see cref="IMcpHttpInvoker"/> that does not implement <see cref="IMcpHttpRequestInvoker"/>, such as a
/// decorator of the app's own. Such a call gets an error result, which only the AI client would otherwise see. The check
/// runs on the call, not at startup, so it resolves the invoker from the call's own services, as the tool does.
/// </summary>
internal static class McpRequestInvokerCheck
{
    private static readonly ConcurrentDictionary<Type, bool> WarnedInvokerTypes = new();

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return (context, cancellationToken) =>
        {
            WarnAboutAnInvokerThatCannotSendHeadersOrForms(context);
            return next(context, cancellationToken);
        };
    }

    private static void WarnAboutAnInvokerThatCannotSendHeadersOrForms(RequestContext<CallToolRequestParams> context)
    {
        var tool = context.MatchedPrimitive as McpServerTool;
        var services = context.Services;
        if (tool is null || services is null || !tool.Metadata.OfType<McpGeneratedToolAttribute>().Any(x => x.SendsHeadersOrForm))
        {
            return;
        }

        var invoker = services.GetService<IMcpHttpInvoker>();
        if (invoker is null || invoker is IMcpHttpRequestInvoker || !WarnedInvokerTypes.TryAdd(invoker.GetType(), true))
        {
            return;
        }

        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpRequestInvokerCheck).FullName!).LogWarning(
            "The tool {ToolName} sends request headers or a form, which the registered IMcpHttpInvoker, {InvokerType}, cannot send, so its calls with any get an error result. Implement IMcpHttpRequestInvoker as well.",
            tool.ProtocolTool.Name,
            invoker.GetType().FullName);
    }
}
