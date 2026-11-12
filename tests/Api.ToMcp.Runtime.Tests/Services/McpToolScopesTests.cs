using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Api.ToMcp.Runtime.Tests.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class McpToolScopesTests
{
    [Fact]
    public void TryGetRequiredScope_ReadsTheScope_FromTheToolsRequiredScopeAttribute()
    {
        var toolScopes = CreateToolScopes(names => { }, ScopedTools.ReadTool, ScopedTools.ReadDeleteTool, ScopedTools.UnscopedTool);

        Assert.True(toolScopes.TryGetRequiredScope(ScopedTools.ReadTool, out var readScope));
        Assert.Equal(McpScope.Read, readScope);
        Assert.True(toolScopes.TryGetRequiredScope(ScopedTools.ReadDeleteTool, out var readDeleteScope));
        Assert.Equal(McpScope.Read | McpScope.Delete, readDeleteScope);
        Assert.False(toolScopes.TryGetRequiredScope(ScopedTools.UnscopedTool, out _));
        Assert.False(toolScopes.TryGetRequiredScope("missing", out _));
    }

    [Fact]
    public void AllRequiredScopes_CombinesTheScopesOfEveryTool()
    {
        var toolScopes = CreateToolScopes(names => { }, ScopedTools.ReadTool, ScopedTools.ReadDeleteTool, ScopedTools.UnscopedTool);

        Assert.Equal(McpScope.Read | McpScope.Delete, toolScopes.AllRequiredScopes);
    }

    [Fact]
    public void GetScopeNames_NamesEachFlag_InTheFlagsOrder_AndReportsTheFlagsWithoutAName()
    {
        var toolScopes = CreateToolScopes(names =>
        {
            names[McpScope.Delete] = "mcp:delete";
            names[McpScope.Read] = "mcp:read";
            names[McpScope.Write] = "";
        });

        var scopeNames = toolScopes.GetScopeNames(McpScope.All | (McpScope)8, out var unnamedScope);

        Assert.Equal(["mcp:read", "mcp:delete"], scopeNames);
        Assert.Equal(McpScope.Write | (McpScope)8, unnamedScope);
    }

    [Fact]
    public void GetScopeNames_NamesABitNoMemberNames_WhenTheAppNamesIt()
    {
        var toolScopes = CreateToolScopes(names => names[(McpScope)8] = "mcp:export");

        var scopeNames = toolScopes.GetScopeNames((McpScope)8, out var unnamedScope);

        Assert.Equal(["mcp:export"], scopeNames);
        Assert.Equal(McpScope.None, unnamedScope);
    }

    [Fact]
    public void GetScopeNames_NamesNothing_ForNoScope()
    {
        var toolScopes = CreateToolScopes(names => names[McpScope.Read] = "mcp:read");

        Assert.Empty(toolScopes.GetScopeNames(McpScope.None, out var unnamedScope));
        Assert.Equal(McpScope.None, unnamedScope);
    }

    [Fact]
    public void Tools_AreBuiltOnFirstUse_NotWhenTheScopesAreCreated()
    {
        var builtTools = 0;
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<McpServerTool>(_ =>
        {
            builtTools++;
            return ScopedTools.Create(ScopedTools.ReadTool);
        });
        services.AddSingleton<McpToolScopes>();
        using var provider = services.BuildServiceProvider();

        var toolScopes = provider.GetRequiredService<McpToolScopes>();
        var builtBeforeUse = builtTools;
        _ = toolScopes.AllRequiredScopes;

        Assert.Equal(0, builtBeforeUse);
        Assert.Equal(1, builtTools);
    }

    private static McpToolScopes CreateToolScopes(Action<IDictionary<McpScope, string>> nameScopes, params string[] toolNames)
    {
        var services = new ServiceCollection();
        services.Configure<McpScopeOptions>(options => nameScopes(options.ScopeNames));
        foreach (var toolName in toolNames)
        {
            services.AddSingleton(ScopedTools.Create(toolName));
        }

        services.AddSingleton<McpToolScopes>();

        return services.BuildServiceProvider().GetRequiredService<McpToolScopes>();
    }
}
