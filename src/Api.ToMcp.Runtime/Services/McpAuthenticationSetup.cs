using Api.ToMcp.Runtime.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Adds the MCP SDK's authentication scheme, which serves the protected resource metadata and issues the challenge that
/// points to it, when <see cref="McpScopeOptions.ResourceMetadata"/> turns OAuth sign-in discovery on, and hands it that
/// metadata. Both run when the options are first read, after the app has configured them, so an app that leaves the
/// option unset gets no extra scheme.
/// </summary>
internal sealed class McpAuthenticationSetup : IPostConfigureOptions<AuthenticationOptions>, IConfigureNamedOptions<McpAuthenticationOptions>
{
    /// <summary>
    /// The switch that turns ASP.NET Core's default to a single scheme off, which <see cref="AuthenticationOptions"/>
    /// reads into an internal property.
    /// </summary>
    private const string SuppressAutoDefaultSchemeSwitch = "Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme";

    private readonly McpScopeOptions _scopeOptions;
    private readonly McpToolScopes _toolScopes;

    public McpAuthenticationSetup(IOptions<McpScopeOptions> scopeOptions, McpToolScopes toolScopes)
    {
        _scopeOptions = scopeOptions.Value;
        _toolScopes = toolScopes;
    }

    public void PostConfigure(string? name, AuthenticationOptions options)
    {
        // An app that registered the SDK's scheme itself, with AddMcp(), keeps it.
        if (_scopeOptions.ResourceMetadata is null || options.SchemeMap.ContainsKey(McpAuthenticationDefaults.AuthenticationScheme))
        {
            return;
        }

        // As the only scheme, the SDK's would become the default of the whole app, and with no scheme to forward to it
        // would fail every request. The MCP endpoint, which names it, fails instead, and the startup check says why.
        var schemes = options.Schemes.ToList();
        if (schemes.Count == 0)
        {
            return;
        }

        // With a single scheme and no default, ASP.NET Core uses that scheme as the default; a second scheme would end that.
        var isAutoDefaultSchemeSuppressed = AppContext.TryGetSwitch(SuppressAutoDefaultSchemeSwitch, out var isSuppressed) && isSuppressed;
        if (options.DefaultScheme is null && !isAutoDefaultSchemeSuppressed && schemes.Count == 1)
        {
            options.DefaultScheme = schemes[0].Name;
        }

        options.AddScheme<McpAuthenticationHandler>(McpAuthenticationDefaults.AuthenticationScheme, McpAuthenticationDefaults.DisplayName);
    }

    public void Configure(string? name, McpAuthenticationOptions options)
    {
        var resourceMetadata = _scopeOptions.ResourceMetadata;
        if (resourceMetadata is null || name != McpAuthenticationDefaults.AuthenticationScheme)
        {
            return;
        }

        var metadata = resourceMetadata.Clone();
        if (metadata.ScopesSupported.Count == 0)
        {
            metadata.ScopesSupported = [.. _toolScopes.GetScopeNames(_toolScopes.AllRequiredScopes, out _)];
        }

        options.ResourceMetadata = metadata;
    }

    public void Configure(McpAuthenticationOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }
}
