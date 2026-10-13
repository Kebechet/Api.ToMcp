using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Turns the invoker's result into what a generated tool with an output schema returns. A successful body is handed
/// to the MCP SDK as a JSON value rather than set as <see cref="CallToolResult.StructuredContent"/>: the SDK wraps
/// it as <c>{"result": value}</c> for clients on a protocol older than 2026-07-28 when the schema is not a plain
/// object, and it does that only for a value it builds the result from, never for a returned
/// <see cref="CallToolResult"/>.
/// </summary>
public static class McpStructuredOutput
{
    private const string JsonNull = "null";

    private static readonly JsonElement NullValue = JsonDocument.Parse(JsonNull).RootElement.Clone();

    /// <summary>
    /// An error result unchanged, so it stays text without structured content. A success's JSON body as a
    /// <see cref="JsonElement"/>, and an empty success body (a 204 for an action that returned null) as JSON null,
    /// which the schema allows whenever the generator found the action can send one. A success body that is not JSON
    /// is returned unchanged, as text.
    /// </summary>
    public static object FromResult(CallToolResult result)
    {
        if (result.IsError == true)
        {
            return result;
        }

        var body = string.Concat(result.Content.OfType<TextContentBlock>().Select(x => x.Text));
        if (string.IsNullOrWhiteSpace(body))
        {
            return NullValue;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return result;
        }
    }
}
