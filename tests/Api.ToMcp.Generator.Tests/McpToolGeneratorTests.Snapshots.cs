using System;
using System.IO;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// The whole emitted code of tools without an output schema, which output schemas must leave as it was. Each expected
/// file in <c>Snapshots</c> is 2.0.0's output for the same source, plus the tool annotations that 2.1.0 adds to
/// <c>[McpServerTool]</c>.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Fact]
    public void Generator_EmitsToolsWithoutAnOutputSchema_AsBefore()
    {
        const string actions = @"
        /// <summary>Finds products by name.</summary>
        /// <param name=""name"">Part of the product name.</param>
        [HttpGet]
        public IActionResult Search([FromQuery] string? name, [FromQuery] int page = 1) => Ok();

        [HttpGet(""{id}/label"")]
        public Task<string> GetLabel(Guid id) => Task.FromResult(string.Empty);

        [HttpPost]
        public Task Create([FromBody] Product product) => Task.CompletedTask;

        [HttpDelete(""{id}"")]
        public void Delete(Guid id) { }
";
        var generated = RunGenerator(StructuredOutputSource(actions, isRawBody: true), AllExceptExcludedConfig);

        AssertMatchesSnapshot("ToolsWithoutOutputSchema.txt", generated);
    }

    [Fact]
    public void Generator_EmitsTypedToolsAsBefore_WhenStructuredOutputIsOffInTheConfig()
    {
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""structuredOutput"": false }";
        const string actions = @"
        /// <summary>Gets a product.</summary>
        [HttpGet(""{id}"")]
        public ActionResult<Product> Get(Guid id) => new Product();

        [HttpPut(""{id}"")]
        public Task<ActionResult<Product>> Update(Guid id, [FromBody] Product product) => Task.FromResult<ActionResult<Product>>(product);
";
        var generated = RunGenerator(StructuredOutputSource(actions, isRawBody: true), config);

        AssertMatchesSnapshot("TypedToolsWithStructuredOutputOff.txt", generated);
    }

    private static void AssertMatchesSnapshot(string fileName, string generated)
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", fileName));

        // The emitter ends lines with Environment.NewLine, and git may check the expected file out with CRLF.
        Assert.Equal(expected.Replace("\r\n", "\n"), generated.Replace("\r\n", "\n"));
    }
}
