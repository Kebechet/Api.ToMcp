using System.Text.Json;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
    private static readonly Guid FirstId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SecondId = Guid.Parse("66666666-7777-8888-9999-000000000000");

    [Fact]
    public async Task APropertyOfATypeWithAModelBinder_ReachesTheBinder()
    {
        var bound = await PostAsync("order", "f", new QueryOrderFilter { Id = new QueryOrderId(FirstId), City = "Q" });

        Assert.Equal(FirstId, bound.GetProperty("id").GetGuid());
        Assert.Equal("Q", bound.GetProperty("city").GetString());
    }

    [Fact]
    public async Task AParameterOfATypeWithAModelBinder_ReachesTheBinder()
    {
        var bound = await PostAsync("id", "id", new QueryOrderId(FirstId));

        Assert.Equal(FirstId, bound.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ValuesOfATypeWithAModelBinder_InANestedObjectAndACollection_ReachTheBinder()
    {
        var bound = await PostAsync("nested", "o", new QueryOrderOuter
        {
            Inner = new QueryOrderFilter { Id = new QueryOrderId(SecondId) },
            Ids = [new QueryOrderId(FirstId), new QueryOrderId(SecondId)]
        });

        Assert.Equal(SecondId, bound.GetProperty("inner").GetGuid());
        Assert.Equal([FirstId, SecondId], bound.GetProperty("ids").EnumerateArray().Select(x => x.GetGuid()));
    }

    [Fact]
    public async Task ACollectionPropertyWithAModelBinderOfItsOwn_ReachesTheBinderWithEveryItem()
    {
        var bound = await PostAsync("ids", "f", new QueryIdsFilter { Ids = [1, 2, 3], City = "Brno" });

        Assert.Equal([1, 2, 3], bound.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal("Brno", bound.GetProperty("city").GetString());
    }

    [Fact]
    public async Task APropertyARecordsConstructorSets_BindsDespiteBindNeverOnTheRecord()
    {
        var bound = await PostAsync("never-record", "r", new QueryNeverBoundRecord("Brno") { Note = "n1" });

        Assert.Equal("Brno", bound.GetProperty("city").GetString());
        Assert.Equal(JsonValueKind.Null, bound.GetProperty("note").ValueKind);
    }

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
    [HttpPost("order")]
    public IActionResult Order(QueryOrderFilter f) => Ok(new { id = f.Id.Value, f.City });

    [HttpPost("id")]
    public IActionResult Id(QueryOrderId id) => Ok(new { id = id.Value });

    [HttpPost("nested")]
    public IActionResult Nested([FromQuery] QueryOrderOuter o) => Ok(new { inner = o.Inner?.Id.Value, ids = o.Ids.Select(x => x.Value) });

    [HttpPost("ids")]
    public IActionResult Ids([FromQuery] QueryIdsFilter f) => Ok(new { ids = f.Ids, f.City });

    [HttpPost("never-record")]
    public IActionResult NeverRecord([FromQuery] QueryNeverBoundRecord r) => Ok(new { r.City, r.Note });

    [HttpPost("ct")]
    public IActionResult Cancellation(QueryCancellationFilter f) => Ok(new { f.City, query = Request.QueryString.Value });
}

[ModelBinder(typeof(QueryOrderIdBinder))]
public readonly record struct QueryOrderId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public sealed class QueryOrderIdBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value != ValueProviderResult.None && Guid.TryParse(value.FirstValue, out var id))
        {
            bindingContext.Result = ModelBindingResult.Success(new QueryOrderId(id));
        }

        return Task.CompletedTask;
    }
}

public sealed class QueryOrderFilter
{
    public QueryOrderId Id { get; set; }

    public string? City { get; set; }
}

public sealed class QueryOrderOuter
{
    public QueryOrderFilter? Inner { get; set; }

    public List<QueryOrderId> Ids { get; set; } = [];
}

public sealed class QueryIdsFilter
{
    [ModelBinder(typeof(QueryIdsBinder))]
    public int[] Ids { get; set; } = [];

    public string? City { get; set; }
}

/// <summary>
/// Reads every value under its key, as a binder of a collection does.
/// </summary>
public sealed class QueryIdsBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var values = bindingContext.ValueProvider.GetValue(bindingContext.ModelName).Values;
        bindingContext.Result = ModelBindingResult.Success(values.Select(x => int.Parse(x!, System.Globalization.CultureInfo.InvariantCulture)).ToArray());

        return Task.CompletedTask;
    }
}

[BindNever]
public sealed record QueryNeverBoundRecord(string? City)
{
    public string? Note { get; set; }
}

public sealed class QueryCancellationFilter
{
    public string? City { get; set; }

    public CancellationToken Token { get; set; }
}
