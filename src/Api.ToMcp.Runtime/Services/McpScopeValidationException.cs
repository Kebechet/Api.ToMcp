namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Thrown by <see cref="McpHttpInvoker.BeforeInvokeAsync"/> when scope validation refuses a tool call. Its message is
/// meant for the client, which <see cref="McpScopeValidationFilter"/> sends it to; it names the scopes the call needs
/// and the caller has, never a token or a claim's value.
/// </summary>
internal sealed class McpScopeValidationException : UnauthorizedAccessException
{
    public McpScopeValidationException(string message)
        : base(message)
    {
    }
}
