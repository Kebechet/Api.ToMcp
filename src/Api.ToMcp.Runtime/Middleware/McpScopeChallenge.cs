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
/// tool: with <c>403</c> and an <c>insufficient_scope</c> challenge that names the scopes the tool requires and the ones
/// the caller already has, or with
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

    private const char PathSeparator = RouteTokens.PathSeparator;
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

    /// <summary>
    /// The largest <c>tools/call</c> body read before the SDK reads it. A larger call goes to the SDK unread, and the tool's
    /// own check refuses it with an error result instead of a challenge, so a request never holds more of the endpoint's
    /// memory than this before the SDK reads it, and none of its disk.
    /// </summary>
    internal const int MaxToolCallBodyBytes = 1024 * 1024;

    private const int ReadChunkBytes = 16 * 1024;

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
        _toolScopes.GetScopeNames(requiredScope, out var unnamedScope);
        if (unnamedScope != McpScope.None)
        {
            return false;
        }

        // A 2025-11-25 client asks for the challenge's scopes alone, so the scopes the caller already has are named too,
        // or the new token would lose them.
        var scope = string.Join(ScopeSeparator, _toolScopes.GetScopeNames(requiredScope | scopeCheck.GrantedScope, out _));
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
    /// not a JSON-RPC message, which the SDK answers, and one larger than <see cref="MaxToolCallBodyBytes"/>. The body is
    /// read the way the SDK reads it, with <c>ReadFromJsonAsync</c>, which follows the content type's charset, and is put
    /// back for the SDK to read again.
    /// </summary>
    private static async Task<string?> ReadCalledToolNameAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength > MaxToolCallBodyBytes || !request.HasJsonContentType())
        {
            return null;
        }

        var body = await ReadBodyAsync(request, context.RequestAborted);
        if (body is null)
        {
            return null;
        }

        try
        {
            var message = await request.ReadFromJsonAsync(MessageTypeInfo, context.RequestAborted) as JsonRpcRequest;
            if (message is null || message.Method != RequestMethods.ToolsCall || message.Params is null)
            {
                return null;
            }

            var toolCall = JsonSerializer.Deserialize(message.Params, ToolCallTypeInfo) as CallToolRequestParams;
            return toolCall?.Name;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A charset ReadFromJsonAsync does not know; the SDK answers it.
            return null;
        }
        finally
        {
            body.Position = 0;
        }
    }

    /// <summary>
    /// The body in memory, now <see cref="HttpRequest.Body"/>, when it holds at most <see cref="MaxToolCallBodyBytes"/>;
    /// otherwise <see langword="null"/>, with what was read put back in front of the rest of the body. The length is not
    /// always known beforehand: the MCP SDK's own client sends its requests chunked.
    /// </summary>
    private static async Task<MemoryStream?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var body = request.Body;
        var readBody = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        while (readBody.Length <= MaxToolCallBodyBytes)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                readBody.Position = 0;
                request.Body = readBody;
                return readBody;
            }

            readBody.Write(chunk, 0, read);
        }

        readBody.Position = 0;
        request.Body = new ReadAheadStream(readBody, body);
        return null;
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

    /// <summary>
    /// The bytes read ahead, then the rest of the body, read only forward as the server's own body stream is.
    /// </summary>
    private sealed class ReadAheadStream : Stream
    {
        private readonly Stream _readAhead;
        private readonly Stream _rest;

        public ReadAheadStream(Stream readAhead, Stream rest)
        {
            _readAhead = readAhead;
            _rest = rest;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _readAhead.Read(buffer);
            return read > 0 ? read : _rest.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = _readAhead.Read(buffer.Span);
            if (read > 0)
            {
                return read;
            }

            return await _rest.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
