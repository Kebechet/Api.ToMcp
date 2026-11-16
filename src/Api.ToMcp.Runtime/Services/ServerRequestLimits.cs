using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The per-request limits of the server the host runs, which hold a tool's in-process request as they hold a request the
/// server receives. The server is the <see cref="IServer"/> the host runs, not one whose options are registered: the
/// default host configures Kestrel's options even when a test replaces the server with its <c>TestServer</c>.
/// </summary>
/// <remarks>
/// Kestrel holds the request to its limits of an HTTP/1.1 request, which the in-process request is: the request line and
/// the headers before the app sees it, and the body once the app reads it. IIS in-process hosting and HTTP.sys give the app
/// only the body's limit in their options; IIS's request filtering and HTTP.sys's registry settings hold a request to their
/// other limits outside the app. A server this does not know, such as <c>TestServer</c>, holds the request to none.
/// </remarks>
internal sealed class ServerRequestLimits
{
    private const string Space = " ";
    private const string HeaderNameSeparator = ": ";
    private const string HeaderValueSeparator = ", ";
    private const string LineEnd = "\r\n";

    private readonly KestrelServerLimits? _kestrelLimits;
    private readonly Func<long?> _getMaxRequestBodySize;

    private ServerRequestLimits(KestrelServerLimits? kestrelLimits, Func<long?> getMaxRequestBodySize)
    {
        _kestrelLimits = kestrelLimits;
        _getMaxRequestBodySize = getMaxRequestBodySize;
    }

    /// <summary>
    /// The limits of a server that holds a request to none, such as <c>TestServer</c>.
    /// </summary>
    public static ServerRequestLimits None { get; } = new(kestrelLimits: null, () => null);

    /// <summary>
    /// The size a request's body is held to until the app sets another for the request through
    /// <see cref="IHttpMaxRequestBodySizeFeature"/>, as <c>[RequestSizeLimit]</c> does; null for none. It is read from the
    /// server's options for each request, as the server reads it.
    /// </summary>
    public long? MaxRequestBodySize => _getMaxRequestBodySize();

    /// <summary>
    /// The limits of the server the host runs, read from the options that server was given: Kestrel's
    /// <see cref="KestrelServerOptions.Limits"/>, and the body size of <see cref="IISServerOptions"/> or
    /// <see cref="HttpSysOptions"/>. None without a server, or with one of another kind.
    /// </summary>
    public static ServerRequestLimits Of(IServiceProvider services)
    {
        var server = services.GetService<IServer>();
        if (server is null)
        {
            return None;
        }

        // The host runs Kestrel as an internal type of Kestrel's assembly, made from the registered options; a KestrelServer
        // made by hand carries options of its own.
        var kestrelServer = server as KestrelServer;
        if (kestrelServer is not null)
        {
            return ForKestrel(kestrelServer.Options.Limits);
        }

        var serverAssembly = server.GetType().Assembly;
        if (serverAssembly == typeof(KestrelServer).Assembly)
        {
            return ForKestrel(services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits);
        }

        if (serverAssembly == typeof(IISServerOptions).Assembly)
        {
            var iisOptions = services.GetRequiredService<IOptions<IISServerOptions>>().Value;
            return new ServerRequestLimits(kestrelLimits: null, () => iisOptions.MaxRequestBodySize);
        }

        if (serverAssembly == typeof(HttpSysOptions).Assembly)
        {
            var httpSysOptions = services.GetRequiredService<IOptions<HttpSysOptions>>().Value;
            return new ServerRequestLimits(kestrelLimits: null, () => httpSysOptions.MaxRequestBodySize);
        }

        return None;
    }

    /// <summary>
    /// The limits of Kestrel, read from <paramref name="limits"/> for each request, as Kestrel reads them.
    /// </summary>
    public static ServerRequestLimits ForKestrel(KestrelServerLimits limits)
    {
        return new ServerRequestLimits(limits, () => limits.MaxRequestBodySize);
    }

    /// <summary>
    /// How the server refuses the request before the app sees it, or null when it lets the app have it. Kestrel measures
    /// the request as a client writes it in HTTP/1.1: the request line as the method, the target as the request's
    /// <see cref="IHttpRequestFeature.RawTarget"/> holds it, percent-encoded, and the protocol, separated by spaces and
    /// ended by a CRLF; and each header as one line of its name, a colon and a space, and its values separated by a comma
    /// and a space, ended by a CRLF, as <see cref="HttpClient"/> writes it.
    /// </summary>
    public Refusal? GetRefusal(IHttpRequestFeature request)
    {
        if (_kestrelLimits is null)
        {
            return null;
        }

        var requestLineSize = GetByteCount(request.Method) + Space.Length + GetByteCount(request.RawTarget) + Space.Length +
            GetByteCount(request.Protocol) + LineEnd.Length;
        if (requestLineSize > _kestrelLimits.MaxRequestLineSize)
        {
            return new Refusal(StatusCodes.Status414UriTooLong, "Request line too long.");
        }

        // Kestrel reads the header lines into the total size and 2 bytes more, for the empty line that ends them, and counts
        // a header once it has read its whole line. A line that does not fit leaves the headers unfinished, which is a
        // refusal for their size, whatever their number.
        var allowedHeadersSize = (long)_kestrelLimits.MaxRequestHeadersTotalSize + LineEnd.Length;
        var headersSize = 0L;
        var headerCount = 0;
        foreach (var (name, values) in request.Headers)
        {
            headersSize += GetByteCount(name) + HeaderNameSeparator.Length + GetByteCount(values) + LineEnd.Length;
            if (headersSize > allowedHeadersSize)
            {
                return RefuseHeadersSize();
            }

            headerCount++;
            if (headerCount > _kestrelLimits.MaxRequestHeaderCount)
            {
                return new Refusal(StatusCodes.Status431RequestHeaderFieldsTooLarge, "Request contains too many headers.");
            }
        }

        return headersSize + LineEnd.Length > allowedHeadersSize ? RefuseHeadersSize() : null;
    }

    private static Refusal RefuseHeadersSize()
    {
        return new Refusal(StatusCodes.Status431RequestHeaderFieldsTooLarge, "Request headers too long.");
    }

    private static long GetByteCount(StringValues values)
    {
        long size = HeaderValueSeparator.Length * Math.Max(values.Count - 1, 0);
        foreach (var value in values)
        {
            size += GetByteCount(value);
        }

        return size;
    }

    private static int GetByteCount(string? text)
    {
        return text is null ? 0 : Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>
    /// The status the server answers a refused request with, without a body, and the reason it logs.
    /// </summary>
    public sealed record Refusal(int StatusCode, string Reason);
}
