using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;

namespace Api.ToMcp.Runtime.Middleware;

/// <summary>
/// With OAuth sign-in discovery on, answers a <c>tools/call</c> that scope validation refuses before the MCP SDK runs the
/// tool: with <c>403</c> and an <c>insufficient_scope</c> challenge that names the scopes the tool requires, or with
/// <c>401</c> and a sign-in challenge when the caller is not signed in, as the MCP authorization spec asks, so the client
/// can ask the user for them. It has to happen here: the SDK sends the status of a tool call before it runs the tool, at
/// once for a protocol version before <c>2026-07-28</c> and after 250 ms from then on, so a filter around the tool
/// cannot change it. A call this does not refuse goes on to the SDK as it came, and the tool still checks its scope.
/// </summary>
internal sealed class McpScopeChallenge
{
    /// <summary>
    /// Where <see cref="McpAuthenticationHandler"/> serves the metadata unless told otherwise, followed by the path of the
    /// resource it describes.
    /// </summary>
    private const string DefaultResourceMetadataPath = "/.well-known/oauth-protected-resource";

    private const char PathSeparator = '/';
    private const string BearerScheme = "Bearer";
    private const string InsufficientScopeError = "insufficient_scope";
    private const string ScopeSeparator = " ";
    private const string ParameterSeparator = ", ";

    /// <summary>
    /// The <c>WWW-Authenticate</c> parameters of RFC 6750 section 3 and RFC 9728 section 5.1.
    /// </summary>
    private const string ErrorParameter = "error";
    private const string ErrorDescriptionParameter = "error_description";
    private const string ScopeParameter = "scope";
    private const string ResourceMetadataParameter = "resource_metadata";

    private static readonly JsonTypeInfo MessageTypeInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));
    private static readonly JsonTypeInfo ToolCallTypeInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolRequestParams));

    private readonly McpScopeOptions _scopeOptions;
    private readonly McpToolScopes _toolScopes;
    private readonly IOptionsMonitor<McpAuthenticationOptions> _authenticationOptions;
    private readonly ILogger<McpScopeChallenge> _logger;

    public McpScopeChallenge(
        IOptions<McpScopeOptions> scopeOptions,
        McpToolScopes toolScopes,
        IOptionsMonitor<McpAuthenticationOptions> authenticationOptions,
        ILogger<McpScopeChallenge> logger)
    {
        _scopeOptions = scopeOptions.Value;
        _toolScopes = toolScopes;
        _authenticationOptions = authenticationOptions;
        _logger = logger;
    }

    /// <summary>
    /// <paramref name="endpoint"/>, answering a tool call that scope validation refuses with a challenge.
    /// </summary>
    public RequestDelegate ChallengeRefusedToolCalls(RequestDelegate endpoint)
    {
        return async context =>
        {
            var isRefused = await TryRefuseAsync(context);
            if (!isRefused)
            {
                await endpoint(context);
            }
        };
    }

    private async Task<bool> TryRefuseAsync(HttpContext context)
    {
        var claimToScopeMapper = _scopeOptions.ClaimToScopeMapper;
        if (claimToScopeMapper is null || !HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        var toolName = await ReadCalledToolNameAsync(context);
        if (toolName is null || !_toolScopes.TryGetRequiredScope(toolName, out var requiredScope))
        {
            return false;
        }

        var scopeCheck = McpScopeCheck.Check(context.User, requiredScope, _scopeOptions.ClaimName, claimToScopeMapper);
        if (scopeCheck.Refusal is null)
        {
            return false;
        }

        // A challenge naming only some of the scopes would send the client to sign in for too little, so a scope without a
        // name is left to the tool's own check, which refuses the call with an error result.
        var scopeNames = _toolScopes.GetScopeNames(requiredScope, out var unnamedScope);
        if (unnamedScope != McpScope.None)
        {
            return false;
        }

        var scope = string.Join(ScopeSeparator, scopeNames);
        var resourceMetadataUri = GetResourceMetadataUri(context.Request);
        string challenge;
        if (scopeCheck.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            challenge = FormatChallenge(
                (ErrorParameter, InsufficientScopeError),
                (ScopeParameter, scope),
                (ResourceMetadataParameter, resourceMetadataUri),
                (ErrorDescriptionParameter, ToErrorDescription(scopeCheck.Refusal)));
        }
        else
        {
            // RFC 6750 section 3.1: a request without credentials gets no error code.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            challenge = FormatChallenge((ResourceMetadataParameter, resourceMetadataUri), (ScopeParameter, scope));
        }

        context.Response.Headers.Append(HeaderNames.WWWAuthenticate, challenge);
        _logger.LogWarning(
            "MCP tool call '{ToolName}' refused by scope validation with {StatusCode}: {Reason}",
            toolName,
            context.Response.StatusCode,
            scopeCheck.Refusal);

        return true;
    }

    /// <summary>
    /// The tool a <c>tools/call</c> request calls, or <see langword="null"/> for any other request, including one that is
    /// not a JSON-RPC message, which the SDK answers. The body is buffered and rewound for the SDK to read again.
    /// </summary>
    private static async Task<string?> ReadCalledToolNameAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        try
        {
            var request = await JsonSerializer.DeserializeAsync(context.Request.Body, MessageTypeInfo, context.RequestAborted) as JsonRpcRequest;
            if (request is null || request.Method != RequestMethods.ToolsCall || request.Params is null)
            {
                return null;
            }

            var toolCall = JsonSerializer.Deserialize(request.Params, ToolCallTypeInfo) as CallToolRequestParams;
            return toolCall?.Name;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }

    /// <summary>
    /// The address <see cref="McpAuthenticationHandler"/> puts in its own <c>401</c> challenge, built the same way, as the
    /// SDK keeps that method private: the configured <see cref="McpAuthenticationOptions.ResourceMetadataUri"/>, else
    /// the default path followed by the MCP endpoint's path.
    /// </summary>
    private string GetResourceMetadataUri(HttpRequest request)
    {
        var origin = request.Scheme + Uri.SchemeDelimiter + request.Host.ToUriComponent() + request.PathBase;
        var configuredUri = _authenticationOptions.Get(McpAuthenticationDefaults.AuthenticationScheme).ResourceMetadataUri;
        if (configuredUri is null)
        {
            return origin + DefaultResourceMetadataPath + request.Path;
        }

        if (configuredUri.IsAbsoluteUri)
        {
            return configuredUri.ToString();
        }

        var separator = configuredUri.OriginalString.StartsWith(PathSeparator) ? "" : PathSeparator.ToString();
        return origin + separator + configuredUri.OriginalString;
    }

    private static string FormatChallenge(params (string Name, string Value)[] parameters)
    {
        return BearerScheme + " " + string.Join(ParameterSeparator, parameters.Select(x => $"{x.Name}=\"{x.Value}\""));
    }

    /// <summary>
    /// The reason with only the characters RFC 6750 section 3 allows in <c>error_description</c>: printable ASCII without
    /// <c>"</c> and <c>\</c>. A claim name comes from the app's configuration and could hold others.
    /// </summary>
    private static string ToErrorDescription(string reason)
    {
        return new string(reason.Where(x => x is >= ' ' and <= '~' and not '"' and not '\\').ToArray());
    }
}
