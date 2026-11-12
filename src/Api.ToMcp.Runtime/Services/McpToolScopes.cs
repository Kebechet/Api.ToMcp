using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The scope each generated tool requires, read from its <see cref="McpRequiredScopeAttribute"/>, and the OAuth scopes
/// <see cref="McpScopeOptions.ScopeNames"/> names for them. The tools are read on first use, so asking does not build
/// them before the host has started.
/// </summary>
internal sealed class McpToolScopes
{
    private readonly Lazy<Dictionary<string, McpScope>> _requiredScopes;
    private readonly IDictionary<McpScope, string> _scopeNames;

    public McpToolScopes(IServiceProvider serviceProvider, IOptions<McpScopeOptions> scopeOptions)
    {
        _requiredScopes = new Lazy<Dictionary<string, McpScope>>(() => ReadRequiredScopes(serviceProvider.GetServices<McpServerTool>()));
        _scopeNames = scopeOptions.Value.ScopeNames;
    }

    /// <summary>
    /// Every scope flag a generated tool requires.
    /// </summary>
    public McpScope AllRequiredScopes => _requiredScopes.Value.Values.Aggregate(McpScope.None, (combined, x) => combined | x);

    /// <summary>
    /// The scope the tool requires; <see langword="false"/> for a tool the generator did not write, which says none.
    /// </summary>
    public bool TryGetRequiredScope(string toolName, out McpScope requiredScope)
    {
        return _requiredScopes.Value.TryGetValue(toolName, out requiredScope);
    }

    /// <summary>
    /// The OAuth scope named for each flag of <paramref name="scope"/>, in the flags' order and each once, as an app with
    /// a single OAuth scope names it for several flags; and in <paramref name="unnamedScope"/> the flags no name is
    /// configured for.
    /// </summary>
    public IReadOnlyList<string> GetScopeNames(McpScope scope, out McpScope unnamedScope)
    {
        var names = new List<string>();
        unnamedScope = McpScope.None;

        foreach (var flag in GetFlags(scope))
        {
            var hasName = _scopeNames.TryGetValue(flag, out var name) && !string.IsNullOrEmpty(name);
            if (!hasName)
            {
                unnamedScope |= flag;
            }
            else if (!names.Contains(name!, StringComparer.Ordinal))
            {
                names.Add(name!);
            }
        }

        return names;
    }

    /// <summary>
    /// Each set bit of the scope as a flag of its own, including a bit no <see cref="McpScope"/> member names, which a
    /// tool gets from <c>[McpExpose(Scope = (McpScope)8)]</c>.
    /// </summary>
    private static IEnumerable<McpScope> GetFlags(McpScope scope)
    {
        for (var bit = 1; bit != 0 && bit <= (int)scope; bit <<= 1)
        {
            var flag = (McpScope)bit;
            if ((scope & flag) == flag)
            {
                yield return flag;
            }
        }
    }

    /// <summary>
    /// The scope of each tool that names one, by tool name. Tools that share a name are listed by the SDK as one; the first
    /// is kept here, and the build warns about them with <c>MCP007</c>.
    /// </summary>
    private static Dictionary<string, McpScope> ReadRequiredScopes(IEnumerable<McpServerTool> tools)
    {
        var requiredScopes = new Dictionary<string, McpScope>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var requiredScope = tool.Metadata.OfType<McpRequiredScopeAttribute>().FirstOrDefault();
            if (requiredScope is not null)
            {
                requiredScopes.TryAdd(tool.ProtocolTool.Name, requiredScope.Scope);
            }
        }

        return requiredScopes;
    }
}
