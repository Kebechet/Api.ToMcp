using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Api.ToMcp.Runtime.Tests.Tools;

/// <summary>A request with properties a generated tool never sends to the API.</summary>
public sealed class UnsentRequest
{
    [JsonRequired]
    public string? City { get; set; }

    public string? Note { get; set; }

    public CancellationToken Token { get; set; }

    [JsonRequired]
    public IFormFile? File { get; set; }

    public List<IFormFile> Files { get; set; } = [];

    public IFormCollection? Form { get; set; }

    public string Secret { private get; set; } = "default-secret";

    public string RevealSecret() => Secret;
}

/// <summary>A record whose constructor sets a property a generated tool never sends.</summary>
public sealed record UnsentRecord(string? Name, IFormFile? File, CancellationToken Token);

/// <summary>Shaped like a tool the generator emits for an action that takes <see cref="UnsentRequest"/>.</summary>
[McpServerToolType]
public static class GeneratedUnsentTools
{
    [McpServerTool(Name = "generated_unsent")]
    [Description("Generated tool whose request has properties it never sends.")]
    public static CallToolResult Run(UnsentRequest request, UnsentRecord? upload = null)
    {
        return new CallToolResult { Content = [new TextContentBlock { Text = $"{request.City}:{request.RevealSecret()}:{upload?.Name}" }] };
    }
}

/// <summary>A hand-written tool, which gets the deserialized request itself, so every property it can set counts.</summary>
[McpServerToolType]
public static class HandWrittenUnsentTools
{
    [McpServerTool(Name = "handwritten_unsent")]
    [Description("Hand-written tool that takes the same request.")]
    public static string Run(UnsentRequest request)
    {
        return request.RevealSecret();
    }
}
