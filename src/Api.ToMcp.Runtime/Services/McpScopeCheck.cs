using System.Security.Claims;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Whether a caller may call a tool that requires a scope, as <see cref="McpScopeOptions.ClaimToScopeMapper"/> reads the
/// caller's claim. The invoker checks it when the tool runs, and the MCP endpoint, with OAuth sign-in discovery on,
/// before the call reaches the tool.
/// </summary>
internal static class McpScopeCheck
{
    /// <param name="user">The caller.</param>
    /// <param name="requiredScope">The scope the tool requires.</param>
    /// <param name="claimName">The claim the caller's scope is read from.</param>
    /// <param name="claimToScopeMapper">Reads the scope that the claim grants.</param>
    public static McpScopeCheckResult Check(ClaimsPrincipal? user, McpScope requiredScope, string claimName, Func<string, McpScope> claimToScopeMapper)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return McpScopeCheckResult.Refuse("User is not authenticated. Scope validation requires authentication.", isAuthenticated: false, McpScope.None);
        }

        var claimValue = user.FindFirst(claimName)?.Value;
        if (string.IsNullOrEmpty(claimValue))
        {
            return McpScopeCheckResult.Refuse($"Claim '{claimName}' not found. Required scope: {requiredScope}", isAuthenticated: true, McpScope.None);
        }

        var grantedScope = claimToScopeMapper(claimValue);
        if ((grantedScope & requiredScope) != requiredScope)
        {
            return McpScopeCheckResult.Refuse($"Insufficient scope. Required: {requiredScope}, Granted: {grantedScope}", isAuthenticated: true, grantedScope);
        }

        return new McpScopeCheckResult(IsAuthenticated: true, GrantedScope: grantedScope, Refusal: null);
    }
}

/// <param name="IsAuthenticated">Whether the caller is signed in.</param>
/// <param name="GrantedScope">The scope the caller's claim grants; <see cref="McpScope.None"/> without the claim.</param>
/// <param name="Refusal">Why the call is refused, meant for the client: it names scopes and the claim, never a token or a
/// claim's value. <see langword="null"/> when the call is allowed.</param>
internal readonly record struct McpScopeCheckResult(bool IsAuthenticated, McpScope GrantedScope, string? Refusal)
{
    public static McpScopeCheckResult Refuse(string reason, bool isAuthenticated, McpScope grantedScope)
    {
        return new McpScopeCheckResult(isAuthenticated, grantedScope, reason);
    }
}
