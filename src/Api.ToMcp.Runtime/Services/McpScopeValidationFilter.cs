using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Returns a tool call that scope validation refused as an error result whose text is the reason, as the invoker
/// returns an API call that failed. Thrown on, the exception would reach the client only as the SDK's
/// <c>An error occurred invoking '&lt;tool&gt;'.</c>: the SDK hides the message of every exception but an
/// <c>McpException</c>, since a message can hold anything. Only <see cref="McpScopeValidationException"/> is turned
/// into a result, so any other exception stays hidden.
/// </summary>
internal static class McpScopeValidationFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (McpScopeValidationException exception)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = exception.Message }]
                };
            }
        };
    }
}
