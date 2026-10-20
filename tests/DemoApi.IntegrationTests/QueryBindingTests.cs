using System.Text.Json;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// Sends the query string a generated tool builds for an object MVC binds from the query to a real MVC action, and reads
/// back what the action bound.
/// </summary>
public class QueryBindingTests
{
    [Fact]
    public async Task AnObjectWithACancellationToken_SendsOnlyWhatTheQueryBinds()
    {
        using var cancellation = new CancellationTokenSource();

        var bound = await PostAsync("ct", "f", new QueryCancellationFilter { City = "A", Token = cancellation.Token });

        Assert.Equal("A", bound.GetProperty("city").GetString());
        Assert.Equal("?f.City=A", bound.GetProperty("query").GetString());
    }

    private static async Task<JsonElement> PostAsync(string action, string name, object value)
    {
        var queryParts = new List<string>();
        McpRequestValueFormatter.AddQueryValue(queryParts, name, value);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(QueryBindingController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        var response = await app.GetTestClient().PostAsync($"/api/query-binding/{action}?{string.Join("&", queryParts)}", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

[ApiController]
[Route("api/query-binding")]
public sealed class QueryBindingController : ControllerBase
{
    [HttpPost("ct")]
    public IActionResult Cancellation(QueryCancellationFilter f) => Ok(new { f.City, query = Request.QueryString.Value });
}

public sealed class QueryCancellationFilter
{
    public string? City { get; set; }

    public CancellationToken Token { get; set; }
}
