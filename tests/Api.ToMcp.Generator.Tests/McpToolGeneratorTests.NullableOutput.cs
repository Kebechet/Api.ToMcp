using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Whether an output schema has to allow null: only when the action is not proven to answer every success with a body.
/// MVC answers a null value with 204 and helpers such as <c>NoContent()</c> send no body, so an unproven action gets
/// "T or null"; a wrong "or null" costs precision, a wrong strict schema fails the call.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string StrictOutput = "[global::Api.ToMcp.Runtime.McpGeneratedTool(IsOutputNullable = false)]";
    private const string NullableOutput = "[global::Api.ToMcp.Runtime.McpGeneratedTool(IsOutputNullable = true)]";

    [Theory]
    [InlineData("public Task<Product> Get() => Task.FromResult(new Product());")]
    [InlineData("public ActionResult<int> Get() => 5;")]
    [InlineData("public ActionResult<Product> Get() => new Product();")]
    [InlineData("public ActionResult<Product> Get() => Ok(new Product());")]
    [InlineData("public ActionResult<Product> Get() { var product = Find(); if (product is null) return NotFound(); return product; } private static Product? Find() => null;")]
    [InlineData("public Task<ActionResult<Product>> Get() { var product = Find(); if (product is null) return Task.FromResult<ActionResult<Product>>(NotFound()); return Task.FromResult<ActionResult<Product>>(product); } private static Product? Find() => null;")]
    [InlineData("public ActionResult<Product> Get() { var product = Find(); return product is null ? NotFound() : product; } private static Product? Find() => null;")]
    [InlineData("public ActionResult<Product> Get(int id) => id switch { 0 => NotFound(), 1 => BadRequest(\"bad\"), 2 => Unauthorized(), 3 => Forbid(), 4 => Conflict(), 5 => UnprocessableEntity(), 6 => throw new InvalidOperationException(), _ => Ok(new Product()) };")]
    [InlineData("public ActionResult<Product> Get(int id) { if (id == 0) return StatusCode(404); if (id == 1) return Problem(); if (id == 2) return ValidationProblem(); return new Product(); }")]
    [InlineData("public ActionResult<Product> Get() => CreatedAtAction(nameof(Get), new { id = 1 }, new Product());")]
    [InlineData("public ActionResult<Product> Get() => new ObjectResult(new Product());")]
    [InlineData("public ActionResult<Product> Get() => StatusCode(201, new Product());")]
    [InlineData("public ActionResult<Product> Get() => new ObjectResult(new Product()) { StatusCode = 201 };")]
    [InlineData("public ActionResult<Product> Get(int id) { if (id == 0) return StatusCode(404, \"missing\"); return new Product(); }")]
    [InlineData("public async Task<ActionResult<Product>> Get() => await Load() ?? throw new InvalidOperationException(); private static Task<Product?> Load() => Task.FromResult<Product?>(null);")]
    [InlineData("public ActionResult<Product> Get() { Func<IActionResult> later = () => { return NoContent(); }; return Ok(new Product()); }")]
    [InlineData("public ActionResult<Product> Get() { return Ok(new Product()); IActionResult Local() { return NoContent(); } }")]
    [InlineData("[ProducesResponseType(typeof(Product), 200)] public IActionResult Get() => Ok(new Product());")]
    [InlineData("public ActionResult<Product> Get() { var product = Find(); return product!; } private static Product? Find() => null;")]
    public void Generator_MakesTheOutputSchemaStrict_WhenEverySuccessIsProvenToHaveABody(string action)
    {
        var tool = GenerateCompilingToolClass(StructuredOutputSource(action), AllExceptExcludedConfig);

        Assert.Contains(StrictOutput, tool);
    }

    [Theory]
    [InlineData("public Task<Product?> Get() => Task.FromResult<Product?>(null);")]
    [InlineData("public Task<int?> Get() => Task.FromResult<int?>(null);")]
    [InlineData("public ActionResult<Product> Get() => NoContent();")]
    [InlineData("public ActionResult<Product> Get() => Ok();")]
    [InlineData("public ActionResult<Product> Get() => Accepted();")]
    [InlineData("public ActionResult<Product> Get() => new OkResult();")]
    [InlineData("public ActionResult<Product> Get() => null!;")]
    [InlineData("public ActionResult<Product> Get() => default!;")]
    [InlineData("public ActionResult<Product> Get() => Ok(Find()); private static Product? Find() => null;")]
    [InlineData("public ActionResult<Product?> Get() => Find(); private static Product? Find() => null;")]
    [InlineData("public ActionResult<Product> Get() => StatusCode(204);")]
    [InlineData("public ActionResult<Product> Get(int code) => StatusCode(code);")]
    [InlineData("public ActionResult<Product> Get() => StatusCode(204, new Product());")]
    [InlineData("public ActionResult<Product> Get() => StatusCode(205, new Product());")]
    [InlineData("public ActionResult<Product> Get(int code) => StatusCode(code, new Product());")]
    [InlineData("public ActionResult<Product> Get() => new ObjectResult(new Product()) { StatusCode = 204 };")]
    [InlineData("public ActionResult<Product> Get() => Redirect(\"/elsewhere\");")]
    [InlineData("public ActionResult<Product> Get() => Helper(); private ActionResult Helper() => Ok(new Product());")]
    [InlineData("public ActionResult<Product> Get(bool empty) => empty ? NoContent() : Ok(new Product());")]
    [InlineData("[ProducesResponseType(StatusCodes.Status200OK)] [ProducesResponseType(StatusCodes.Status204NoContent)] public ActionResult<Product> Get() => Ok(new Product());")]
    [InlineData("[ProducesResponseType(typeof(Product), 200)] public IActionResult Get(bool empty) { if (empty) return NoContent(); return Ok(new Product()); }")]
    [InlineData("public ActionResult<Product> Get() => Ok(LegacyRepository.Find());")]
    [InlineData("public ActionResult<Product> Get() => LegacyRepository.Find();")]
    [InlineData("public ActionResult<Product> Get() => Ok(LegacyRepository.Current);")]
    [InlineData("public ActionResult<Product> Get() => Ok(LegacyRepository.CurrentProperty);")]
    [InlineData("public async Task<ActionResult<Product>> Get() => Ok(await LegacyRepository.FindAsync());")]
    [InlineData("public ActionResult<Product> Get() { var product = LegacyRepository.Find(); return Ok(product); }")]
    [InlineData("public ActionResult<Product> Get() { var product = new Product(); product = LegacyRepository.Find(); return product; }")]
    [InlineData("public ActionResult<Product> Get() { var product = LegacyRepository.Find(); if (product is null) return NotFound(); return product; }")]
    [InlineData("public Product Get() => LegacyRepository.Find();")]
    [InlineData("public Task<Product> Get() => LegacyRepository.FindAsync();")]
    [InlineData("public Task<ActionResult<Product>> Get() => Load(); private Task<ActionResult<Product>> Load() => Task.FromResult<ActionResult<Product>>(NoContent());")]
    [InlineData("[ProducesResponseType<Product>(StatusCodes.Status200OK)] public IResult Get() => Results.NoContent();")]
    [InlineData("[ProducesResponseType<Product>(StatusCodes.Status200OK)] public Task<IResult> Get() => Task.FromResult(Results.NoContent());")]
    [InlineData("[ProducesResponseType<Product>(StatusCodes.Status200OK)] public IResult Get() => TypedResults.NoContent();")]
    [InlineData("[ProducesResponseType<Product>(StatusCodes.Status200OK)] public IResult Get() => Results.Ok(new Product());")]
    public void Generator_MakesTheOutputSchemaNullable_WhenASuccessCanComeWithoutABody(string action)
    {
        var tool = GenerateCompilingToolClass(StructuredOutputSource(action), AllExceptExcludedConfig);

        Assert.Contains(NullableOutput, tool);
    }

    [Theory]
    [InlineData("public Product Get() => new Product();")]
    [InlineData("public ActionResult<Product> Get() { var product = new Product(); return product; }")]
    public void Generator_MakesTheOutputSchemaNullable_InCodeWithoutNullableAnnotations(string action)
    {
        // Without nullable annotations the compiler tracks no null state, so nothing about the value is proven.
        var source = StructuredOutputSource(action).Replace("#nullable enable", "#nullable disable");

        var tool = GenerateCompilingToolClass(source, AllExceptExcludedConfig);

        Assert.Contains(NullableOutput, tool);
    }
}
