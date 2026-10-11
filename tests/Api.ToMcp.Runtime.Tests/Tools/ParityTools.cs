using System.ComponentModel;
using ModelContextProtocol.Server;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Tools;

public interface IParityService
{
    string Name { get; }
}

public sealed class ParityService : IParityService
{
    public string Name => "parity";
}

/// <summary>Request used by the parity tools.</summary>
public sealed class ParityRequest
{
    /// <summary>Documented from XML.</summary>
    public string? Documented { get; set; }

    /// <summary>Declared in xunit.core, a NuGet package assembly: the build does not copy a package's .xml next to it.</summary>
    public FactAttribute? FromPackage { get; set; }
}

[McpServerToolType]
public static class StaticParityTools
{
    [McpServerTool(Name = "parity_static", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Static tool with injected services.")]
    public static string Run(
        IParityService parityService,
        McpServer server,
        [Description("Text to echo.")] string text,
        ParityRequest request,
        CancellationToken cancellationToken)
    {
        return $"{parityService.Name}:{text}:{request.Documented}";
    }

    [McpServerTool(Name = "parity_private")]
    private static string Hidden(int count)
    {
        return count.ToString();
    }
}

[McpServerToolType]
public sealed class InstanceParityTools
{
    private readonly IParityService _parityService;

    public InstanceParityTools(IParityService parityService)
    {
        _parityService = parityService;
    }

    [McpServerTool(Name = "parity_instance", Title = "Instance parity", Destructive = true)]
    public string Run([Description("Identifier.")] Guid id)
    {
        return $"{_parityService.Name}:{id}";
    }
}
