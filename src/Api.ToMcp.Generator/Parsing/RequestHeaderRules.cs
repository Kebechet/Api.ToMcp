using System;
using System.Collections.Generic;
using System.Linq;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// Which request headers a generated tool sends from its arguments. The AI chooses every such value, and the tool's
    /// request never passes the front end that sets or strips headers of a client's request, so a header the request
    /// carries of its own, or one the app may trust as set by a proxy, never comes from an argument. Header names are
    /// compared ignoring case, as HTTP compares them. The runtime's invoker refuses the same headers.
    /// </summary>
    internal static class RequestHeaderRules
    {
        /// <summary>
        /// The characters besides letters and digits that HTTP allows in a token (RFC 9110, section 5.6.2).
        /// </summary>
        private const string TokenSymbols = "!#$%&'*+-.^_`|~";

        /// <summary>
        /// The headers a tool never sends from an argument: the caller's credentials, which the invoker forwards or never
        /// sends; the host, the loop prevention mark and the trace context, which the request carries of its own; the
        /// headers that frame or describe the body the tool writes, which <c>HttpClient</c> keeps among the content's; the
        /// hop-by-hop headers of the connection; and those a middleware reads to change the whole request, its method
        /// (<c>UseHttpMethodOverride</c>) or its client and scheme (<c>UseForwardedHeaders</c> and the like).
        /// </summary>
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HttpHeaderNames.Authorization,
            HttpHeaderNames.ProxyAuthorization,
            HttpHeaderNames.Cookie,
            HttpHeaderNames.Host,
            HttpHeaderNames.LoopPrevention,
            HttpHeaderNames.TraceParent,
            HttpHeaderNames.TraceState,
            HttpHeaderNames.Baggage,
            HttpHeaderNames.CorrelationContext,
            HttpHeaderNames.TransferEncoding,
            HttpHeaderNames.ContentType,
            HttpHeaderNames.ContentLength,
            HttpHeaderNames.ContentEncoding,
            HttpHeaderNames.ContentLanguage,
            HttpHeaderNames.ContentLocation,
            HttpHeaderNames.ContentMD5,
            HttpHeaderNames.ContentRange,
            HttpHeaderNames.ContentDisposition,
            HttpHeaderNames.Allow,
            HttpHeaderNames.Expires,
            HttpHeaderNames.LastModified,
            HttpHeaderNames.Connection,
            HttpHeaderNames.Upgrade,
            HttpHeaderNames.TE,
            HttpHeaderNames.KeepAlive,
            HttpHeaderNames.Trailer,
            HttpHeaderNames.ProxyConnection,
            HttpHeaderNames.HttpMethodOverride,
            HttpHeaderNames.HttpMethod,
            HttpHeaderNames.MethodOverride,
            HttpHeaderNames.Forwarded,
            HttpHeaderNames.RealIp,
            HttpHeaderNames.TrueClientIp,
            HttpHeaderNames.CloudflareConnectingIp,
            HttpHeaderNames.ClientIp,
            HttpHeaderNames.ClusterClientIp,
            HttpHeaderNames.ArrClientCert,
            HttpHeaderNames.ClientCert
        };

        /// <summary>
        /// The starts of the header families a tool never sends from an argument: the forwarding headers, and those in
        /// which a front end that signs the user in passes the user, their token or their client certificate on to the app.
        /// </summary>
        private static readonly string[] Prefixes =
        {
            HttpHeaderNames.ForwardedPrefix,
            HttpHeaderNames.OriginalPrefix,
            HttpHeaderNames.ClientPrincipalPrefix,
            HttpHeaderNames.TokenPrefix,
            HttpHeaderNames.AuthRequestPrefix,
            HttpHeaderNames.RemotePrefix,
            HttpHeaderNames.RemoteUserPrefix,
            HttpHeaderNames.GoogleAuthenticatedUserPrefix,
            HttpHeaderNames.GoogleIapPrefix,
            HttpHeaderNames.AmazonOidcPrefix,
            HttpHeaderNames.AmazonMtlsPrefix,
            HttpHeaderNames.CloudflareAccessPrefix,
            HttpHeaderNames.PomeriumPrefix,
            HttpHeaderNames.SslClientPrefix
        };

        /// <summary>
        /// The headers a tool never sends from an argument that its request may carry of its own: the
        /// <c>Authorization</c> it forwards from the MCP request, the host, the loop prevention mark, and, over HTTP, the
        /// trace context and the <c>X-Forwarded-Proto</c> the invoker sends to a loopback address for an HTTPS request.
        /// </summary>
        private static readonly HashSet<string> CarriedByTheRequest = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HttpHeaderNames.Authorization,
            HttpHeaderNames.Host,
            HttpHeaderNames.LoopPrevention,
            HttpHeaderNames.TraceParent,
            HttpHeaderNames.TraceState,
            HttpHeaderNames.Baggage,
            HttpHeaderNames.CorrelationContext,
            HttpHeaderNames.ForwardedProto
        };

        /// <summary>
        /// The headers of <see cref="Names"/> a tool's request carries when it has a body, a JSON one or a form.
        /// </summary>
        private static readonly HashSet<string> CarriedWithABody = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HttpHeaderNames.ContentType,
            HttpHeaderNames.ContentLength
        };

        internal static IReadOnlyCollection<string> NeverSetNames => Names;

        internal static IReadOnlyList<string> NeverSetPrefixes => Prefixes;

        /// <summary>
        /// Whether a tool never sends the header from an argument: an action that reads it gets the value the tool's
        /// request carries, or none, as it does a service.
        /// </summary>
        public static bool IsNeverSetFromArguments(string headerName)
        {
            return Names.Contains(headerName) ||
                Prefixes.Any(x => headerName.StartsWith(x, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Whether a tool's request may carry the header of its own, one of <see cref="IsNeverSetFromArguments"/>.
        /// </summary>
        public static bool MayBeCarriedByTheRequest(string headerName, bool hasBody)
        {
            return CarriedByTheRequest.Contains(headerName) || (hasBody && CarriedWithABody.Contains(headerName));
        }

        /// <summary>
        /// Whether the name is a token, as HTTP requires of a header's name (RFC 9110, section 5.6.2).
        /// </summary>
        public static bool IsValidName(string headerName)
        {
            return headerName.Length > 0 && headerName.All(IsTokenCharacter);
        }

        private static bool IsTokenCharacter(char character)
        {
            return character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
                TokenSymbols.IndexOf(character) >= 0;
        }
    }
}
