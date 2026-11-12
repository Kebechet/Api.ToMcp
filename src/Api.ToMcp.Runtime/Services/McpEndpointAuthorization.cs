using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The authorization of the MCP endpoint with OAuth sign-in discovery on. The caller is signed in through the MCP SDK's
/// scheme, which reads the bearer token through the scheme its <c>ForwardAuthenticate</c> names and answers a caller who
/// is not signed in with the challenge that points to the protected resource metadata.
/// </summary>
/// <remarks>
/// Authorization metadata on an endpoint ends ASP.NET Core's fallback policy for it, and a policy object among that
/// metadata ends the default policy that an <c>[Authorize]</c> would bring. So the endpoint carries one policy of its own,
/// and <see cref="McpAppPoliciesRequirement"/> applies the app's default and fallback policies to each request itself.
/// </remarks>
internal static class McpEndpointAuthorization
{
    /// <summary>
    /// A signed-in caller who meets the app's default and fallback policies.
    /// </summary>
    public static AuthorizationPolicy CreateSignedInPolicy(IHttpContextAccessor httpContextAccessor)
    {
        return new AuthorizationPolicyBuilder(McpAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new McpAppPoliciesRequirement(httpContextAccessor))
            .Build();
    }

    /// <summary>
    /// For an endpoint that also allows anonymous callers: <c>AuthorizationMiddleware</c> still signs the caller in through
    /// the policy's scheme before it lets an anonymous one through, so the tools see the token's user.
    /// </summary>
    public static AuthorizationPolicy CreateAnonymousPolicy()
    {
        return new AuthorizationPolicyBuilder(McpAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
    }
}

/// <summary>
/// Requires what the app's own authorization requires: its default policy and, when it has one, its fallback policy, both
/// as the app's <see cref="IAuthorizationPolicyProvider"/> gives them for the request. Each policy is checked against the
/// caller as the schemes it names sign them in, so a policy tied to a scheme accepts nothing else, or as the MCP SDK's
/// scheme signs them in when it names none. A requirement that handles itself, through ASP.NET Core's
/// <c>PassThroughAuthorizationHandler</c>.
/// </summary>
internal sealed class McpAppPoliciesRequirement : IAuthorizationRequirement, IAuthorizationHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public McpAppPoliciesRequirement(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        // The resource is the endpoint instead of the request when the app sets ASP.NET Core's
        // SuppressUseHttpContextAsAuthorizationResource switch.
        var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        var services = httpContext.RequestServices;
        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorizationService = services.GetRequiredService<IAuthorizationService>();

        var defaultPolicy = await policyProvider.GetDefaultPolicyAsync();
        var isDefaultPolicyMet = await IsMetAsync(defaultPolicy, context, httpContext, authorizationService);
        if (!isDefaultPolicyMet)
        {
            return;
        }

        var fallbackPolicy = await policyProvider.GetFallbackPolicyAsync();
        if (fallbackPolicy is not null)
        {
            var isFallbackPolicyMet = await IsMetAsync(fallbackPolicy, context, httpContext, authorizationService);
            if (!isFallbackPolicyMet)
            {
                return;
            }
        }

        context.Succeed(this);
    }

    private static async Task<bool> IsMetAsync(
        AuthorizationPolicy policy,
        AuthorizationHandlerContext context,
        HttpContext httpContext,
        IAuthorizationService authorizationService)
    {
        var user = await SignInAsync(policy, context.User, httpContext);
        var result = await authorizationService.AuthorizeAsync(user, context.Resource, policy);

        return result.Succeeded;
    }

    /// <summary>
    /// The caller as the policy's schemes sign them in, their identities together as <c>PolicyEvaluator</c> puts them;
    /// the caller the MCP SDK's scheme signed in when the policy names no scheme.
    /// </summary>
    private static async Task<ClaimsPrincipal> SignInAsync(AuthorizationPolicy policy, ClaimsPrincipal mcpUser, HttpContext httpContext)
    {
        if (policy.AuthenticationSchemes.Count == 0)
        {
            return mcpUser;
        }

        var identities = new List<ClaimsIdentity>();
        foreach (var scheme in policy.AuthenticationSchemes)
        {
            var result = await httpContext.AuthenticateAsync(scheme);
            if (result.Succeeded && result.Principal is not null)
            {
                identities.AddRange(result.Principal.Identities);
            }
        }

        return new ClaimsPrincipal(identities);
    }
}
