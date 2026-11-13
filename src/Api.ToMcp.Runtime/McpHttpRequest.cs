using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.Protocol;

namespace Api.ToMcp.Runtime;

/// <summary>
/// A generated tool's API request with headers or a form, which the <see cref="IMcpHttpInvoker"/> methods cannot send:
/// its method, route, body and headers.
/// </summary>
public sealed class McpHttpRequest
{
    private const char QueryStart = '?';

    /// <summary>
    /// The query values that <c>UseRequestLocalization</c> picks the request's culture from by default, <c>culture</c> and
    /// <c>ui-culture</c>.
    /// </summary>
    private static readonly QueryStringRequestCultureProvider CultureQuery = new();

    public McpHttpRequest(HttpMethod method, string route)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(route);

        Method = method;
        Route = route;
    }

    public HttpMethod Method { get; }

    /// <summary>
    /// The path and query string, as the <see cref="IMcpHttpInvoker"/> methods take them.
    /// </summary>
    public string Route { get; }

    /// <summary>
    /// The body as JSON, or null for none. A request has a JSON body or a form, never both.
    /// </summary>
    public string? JsonBody { get; init; }

    /// <summary>
    /// The body as an <c>application/x-www-form-urlencoded</c> form, its keys and values escaped and joined with
    /// <c>&amp;</c>, or null for none. Empty is a form without fields.
    /// </summary>
    public string? FormBody { get; init; }

    /// <summary>
    /// The culture <see cref="Services.McpRequestValueFormatter.TryAddFormValue"/> wrote the form's dates and numbers in,
    /// or null when the form holds none whose text another culture may read otherwise.
    /// </summary>
    /// <remarks>
    /// MVC reads a form in the culture of the API request, which <c>UseRequestLocalization</c> picks from what the request
    /// carries. So when the MCP request ran it, <c>McpHttpInvoker</c> names the culture of the form, or of the MCP request,
    /// in the <c>Accept-Language</c> of a request with a form, and answers a request whose own <c>Accept-Language</c>,
    /// <c>culture</c> or <c>ui-culture</c> query value names another culture than this one with an error result: 1.5
    /// written in <c>en-US</c> is 15 in <c>de-DE</c>.
    /// </remarks>
    public CultureInfo? FormCulture { get; init; }

    /// <summary>
    /// The request headers, each value under its header's name; a name may come several times, once per value.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>
    /// The tool argument each header's value comes from, under the header's name, for an error result about a header to
    /// name the argument the AI gave. A generated tool gives one for each of its headers.
    /// </summary>
    public IReadOnlyDictionary<string, string> HeaderArguments { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// The argument the header's value comes from, its name compared ignoring case, as HTTP compares header names, or
    /// null when <see cref="HeaderArguments"/> does not say.
    /// </summary>
    internal string? FindHeaderArgument(string headerName)
    {
        return HeaderArguments.FirstOrDefault(x => string.Equals(x.Key, headerName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>
    /// Why the app would read the form in another culture than <paramref name="formCulture"/>, which it is written in,
    /// when it picks the request's culture with <c>UseRequestLocalization</c>: the request names a culture of its own in
    /// an <c>Accept-Language</c> header or a <c>culture</c> or <c>ui-culture</c> query value, and not that same culture.
    /// Null when nothing in the request names another.
    /// </summary>
    internal string? RefuseAnotherCulture(CultureInfo formCulture)
    {
        var languages = Headers
            .Where(x => string.Equals(x.Key, HeaderNames.AcceptLanguage, StringComparison.OrdinalIgnoreCase))
            .Select(x => (string?)x.Value)
            .ToList();
        if (languages.Count > 0 && !IsTheCulture(languages, formCulture))
        {
            var argument = FindHeaderArgument(HeaderNames.AcceptLanguage);
            var subject = argument is null
                ? $"The tool's request carries the header '{HeaderNames.AcceptLanguage}', which"
                : $"The argument '{argument}' is sent as the request header '{HeaderNames.AcceptLanguage}', which";

            return $"{subject} {DescribeCultureRefusal(formCulture, argument is null ? "it" : $"'{argument}'")}";
        }

        var queryStart = Route.IndexOf(QueryStart);
        var query = queryStart < 0 ? [] : QueryHelpers.ParseQuery(Route[queryStart..]);
        var cultureKey = query.ContainsKey(CultureQuery.QueryStringKey) ? CultureQuery.QueryStringKey
            : query.ContainsKey(CultureQuery.UIQueryStringKey) ? CultureQuery.UIQueryStringKey
            : null;
        if (cultureKey is not null && !IsTheCulture(query[cultureKey].ToList(), formCulture))
        {
            return $"The query value '{cultureKey}' of the tool's request {DescribeCultureRefusal(formCulture, $"the argument that fills '{cultureKey}'")}";
        }

        return null;
    }

    /// <summary>
    /// Sends the request through the invoker: through <see cref="IMcpHttpRequestInvoker.SendAsync"/> when it implements
    /// that interface, else through its <see cref="IMcpHttpInvoker"/> method for the HTTP method, which carries neither
    /// headers nor a form. A request with either gets an error result from an invoker that cannot send it.
    /// </summary>
    public Task<CallToolResult> SendAsync(IMcpHttpInvoker invoker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoker);

        var requestInvoker = invoker as IMcpHttpRequestInvoker;
        if (requestInvoker is not null)
        {
            return requestInvoker.SendAsync(this, ct);
        }

        if (Headers.Count == 0 && FormBody is null)
        {
            if (Method == HttpMethod.Get && JsonBody is null)
            {
                return invoker.GetAsync(Route, ct);
            }

            if (Method == HttpMethod.Post)
            {
                return invoker.PostAsync(Route, JsonBody, ct);
            }

            if (Method == HttpMethod.Put)
            {
                return invoker.PutAsync(Route, JsonBody, ct);
            }

            if (Method == HttpMethod.Patch)
            {
                return invoker.PatchAsync(Route, JsonBody, ct);
            }

            if (Method == HttpMethod.Delete && JsonBody is null)
            {
                return invoker.DeleteAsync(Route, ct);
            }
        }

        return Task.FromResult(new CallToolResult
        {
            IsError = true,
            Content =
            {
                new TextContentBlock
                {
                    Text = $"The tool sends a {Method.Method} request with headers or a body that the registered IMcpHttpInvoker, " +
                        $"{invoker.GetType().FullName}, cannot send: implement {nameof(IMcpHttpRequestInvoker)} as well."
                }
            }
        });
    }

    private static bool IsTheCulture(List<string?> values, CultureInfo culture)
    {
        return values.Count == 1 && culture.Name.Length > 0 && string.Equals(values[0]?.Trim(), culture.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rest of the error result, which tells the AI what the tool can send, without suggesting that the culture it
    /// asked for comes some other way: dropping the argument runs the action in the MCP request's culture.
    /// </summary>
    private static string DescribeCultureRefusal(CultureInfo formCulture, string argument)
    {
        var problem = "picks the culture the app reads the tool's form in, and the tool writes the form's numbers and dates in the " +
            "culture of the MCP request, ";

        return formCulture.Name.Length > 0
            ? $"{problem}'{formCulture.Name}'. The tool sends {argument} only with the value '{formCulture.Name}': another culture " +
                "needs an MCP request that runs in it, as one whose own Accept-Language header asks for it does."
            : $"{problem}the invariant culture. The tool cannot send {argument} with a value, as no culture name stands for that culture.";
    }
}
