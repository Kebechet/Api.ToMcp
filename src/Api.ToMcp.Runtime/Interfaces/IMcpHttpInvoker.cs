namespace Api.ToMcp.Runtime;

public interface IMcpHttpInvoker
{
    Task<string> GetAsync(string route, CancellationToken ct = default);
    Task<string> PostAsync(string route, string? jsonBody, CancellationToken ct = default);
}
