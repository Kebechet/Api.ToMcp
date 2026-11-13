using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// <c>[FromHeader]</c> and <c>[FromForm]</c> parameters, which MVC reads only from a request header and from the form
/// content. The tool took them as arguments and sent neither, so the action ran without them. See
/// https://github.com/Kebechet/Api.ToMcp/issues/49.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string McpHttpRequestType = "global::Api.ToMcp.Runtime.McpHttpRequest";

    [Theory]
    [InlineData("[FromHeader] string tenant", "\"tenant\", tenant")]
    [InlineData("[FromHeader(Name = \"X-Tenant\")] string? tenant", "\"X-Tenant\", tenant")]
    [InlineData("[FromHeader(Name = \"X-Ids\")] int[] ids", "\"X-Ids\", ids")]
    [InlineData("[FromHeader(Name = \"X-Statuses\")] List<OrderStatus>? statuses", "\"X-Statuses\", statuses")]
    [InlineData("[FromHeader(Name = \"X-Order\")] OrderId order", "\"X-Order\", order")]
    [InlineData("[FromHeader(Name = \"If-Match\")] string? etag", "\"If-Match\", etag")]
    [InlineData("[FromHeader] int page = 2", "\"page\", page == 2 ? null : page")]
    public void Generator_SendsAFromHeaderParameter_AsARequestHeader(string parameter, string expectedArguments)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"x\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains("var headers = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();", tool);
        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers, {expectedArguments}))", tool);
        Assert.Contains($"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Get, route) {{ Headers = headers }};", tool);
        Assert.Contains("return await request.SendAsync(invoker, cancellationToken);", tool);
        Assert.DoesNotContain("queryParts", tool);
        Assert.DoesNotContain("invoker.GetAsync", tool);
    }

    [Fact]
    public void Generator_RefusesAHeaderValueAnHttpHeaderCannotHold_BeforeCallingTheApi()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpGet(\"x\")] public Task<string> Find([FromHeader(Name = \"X-Tenant\")] string tenant) => Task.FromResult(string.Empty);");

        Assert.Contains(
            "return new CallToolResult { IsError = true, Content = { new TextContentBlock { Text = \"The argument 'tenant' is sent as the request header 'X-Tenant', which can hold only visible ASCII characters, spaces and tabs.\" } } };",
            tool);
        Assert.True(
            tool.IndexOf("TryAddHeaderValue", StringComparison.Ordinal) < tool.IndexOf("BeforeInvokeAsync", StringComparison.Ordinal),
            tool);
    }

    [Fact]
    public void Generator_SendsAHeaderNextToTheJsonBody()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPost(\"x\")] public Task<string> Find([FromBody] ProductFilter filter, [FromHeader(Name = \"Idempotency-Key\")] string? key) => Task.FromResult(string.Empty);");

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(filter);", tool);
        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers, \"Idempotency-Key\", key))", tool);
        Assert.Contains($"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Post, route) {{ JsonBody = bodyJson, Headers = headers }};", tool);
    }

    [Theory]
    [InlineData("HttpPost", "Post")]
    [InlineData("HttpPut", "Put")]
    [InlineData("HttpPatch", "Patch")]
    [InlineData("HttpGet", "Get")]
    [InlineData("HttpDelete", "Delete")]
    public void Generator_SendsFromFormParameters_AsFormContent(string httpAttribute, string httpMethod)
    {
        // In an [ApiController] a complex parameter without [FromForm] would be the JSON body, which MVC does not read
        // into a [FromForm] one.
        var tool = GenerateCompilingSimpleTypeTool(
            $"[{httpAttribute}(\"x\")] public Task<string> Find([FromForm] string name, [FromForm(Name = \"f\")] ProductFilter filter, [FromForm] int[]? ids, [FromForm] ProductFilter? other) => Task.FromResult(string.Empty);");

        Assert.Contains("var formParts = new System.Collections.Generic.List<string>();", tool);
        Assert.Contains($"{Formatter}.AddFormValue(formParts, \"name\", name);", tool);
        Assert.Contains($"{Formatter}.AddFormValue(formParts, \"f\", filter);", tool);
        Assert.Contains($"{Formatter}.AddFormValue(formParts, \"ids\", ids);", tool);
        Assert.Contains($"{Formatter}.AddFormValue(formParts, \"other\", other);", tool);
        Assert.Contains($"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.{httpMethod}, route) {{ FormBody = string.Join(\"&\", formParts) }};", tool);
        Assert.DoesNotContain("bodyJson", tool);
        Assert.DoesNotContain("queryParts", tool);
    }

    [Fact]
    public void Generator_FillsTheRouteFromAFromRouteProperty_AndSendsTheFormPropertiesUnprefixed()
    {
        // MVC reads q's [FromRoute] Id only when no form value carries the q. prefix, so the others go under their own
        // names: Note, which it reads from the form. City it reads from the query string and Tenant from a header.
        var tool = GenerateCompilingSimpleTypeTool("[HttpPut(\"attr/{id:int}\")] public Task<string> Find([FromForm] AttributedQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Id = System.Uri.EscapeDataString({Formatter}.Format(q.Id));", tool);
        Assert.Contains($"{Formatter}.AddFormValue(formParts, \"Note\", q.Note);", tool);
        Assert.DoesNotContain("\"City\"", tool);
        Assert.DoesNotContain("\"Tenant\"", tool);
        Assert.DoesNotContain("\"Id\"", tool);
        Assert.DoesNotContain("\"q\"", tool);
    }

    [Fact]
    public void Generator_SendsHeadersAndAForm_InOneRequest()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPost(\"x/{id}\")] public Task<string> Find(int id, [FromForm] ProductFilter filter, [FromHeader(Name = \"X-Tenant\")] string tenant, [FromQuery] string? sort) => Task.FromResult(string.Empty);");

        Assert.Contains("var route = $\"/api/orders/x/{routeid}\";", tool);
        Assert.Contains($"{Formatter}.AddQueryValue(queryParts, \"sort\", sort);", tool);
        Assert.Contains($"{Formatter}.AddFormValue(formParts, \"filter\", filter);", tool);
        Assert.Contains($"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Post, route) {{ Headers = headers, FormBody = string.Join(\"&\", formParts) }};", tool);
    }

    [Fact]
    public void Generator_RenamesItsOwnVariables_ThatAnActionParameterNames()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPost(\"x\")] public Task<string> Find([FromHeader(Name = \"X-A\")] string headers, [FromForm] string formParts, [FromForm] string request) => Task.FromResult(string.Empty);");

        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers2, \"X-A\", headers))", tool);
        Assert.Contains($"{Formatter}.AddFormValue(formParts2, \"formParts\", formParts);", tool);
        Assert.Contains($"var request2 = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Post, route) {{ Headers = headers2, FormBody = string.Join(\"&\", formParts2) }};", tool);
        Assert.Contains("return await request2.SendAsync(invoker, cancellationToken);", tool);
    }

    [Theory]
    [InlineData("[FromHeader(Name = \"Authorization\")] string? auth", "auth", "Authorization")]
    [InlineData("[FromHeader] string? authorization", "authorization", "authorization")]
    [InlineData("[FromHeader(Name = \"cookie\")] string? session", "session", "cookie")]
    [InlineData("[FromHeader(Name = \"HOST\")] string? host", "host", "HOST")]
    [InlineData("[FromHeader(Name = \"x-mcp-internal-call\")] string? internalCall", "internalCall", "x-mcp-internal-call")]
    [InlineData("[FromHeader(Name = \"Content-Type\")] string? contentType", "contentType", "Content-Type")]
    [InlineData("[FromHeader(Name = \"Content-Length\")] long? length", "length", "Content-Length")]
    [InlineData("[FromHeader(Name = \"Content-Language\")] string? language", "language", "Content-Language")]
    [InlineData("[FromHeader(Name = \"Transfer-Encoding\")] string? encoding", "encoding", "Transfer-Encoding")]
    [InlineData("[FromHeader(Name = \"traceparent\")] string? trace", "trace", "traceparent")]
    [InlineData("[FromHeader(Name = \"Baggage\")] string? baggage", "baggage", "Baggage")]
    public void Generator_GivesNoTool_ToAnActionReadingAHeaderAToolNeverSets(string parameter, string parameterName, string headerName)
    {
        var source = SimpleTypeSource($"[HttpGet(\"x\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);
        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(
            $"No MCP tool is generated for action 'OrdersController.Find': its parameter '{parameterName}' is read from the request header '{headerName}', which a tool never sets from an argument",
            warning.GetMessage());
        Assert.Equal("Find", GetLocatedText(warning.Location, source));
        Assert.DoesNotContain(SimpleTypeToolClassName, generated);
    }

    [Theory]
    [InlineData("[FromHeader(Name = \"X Tenant\")] string? tenant", "its parameter 'tenant' is read from the request header 'X Tenant', which is no valid HTTP header name")]
    [InlineData("[FromHeader] ProductFilter filter", "its parameter 'filter' is read from a header as TestApi.ProductFilter, and a tool sends a header only for a type MVC reads from one string, or an array of such")]
    [InlineData("[FromHeader] ProductFilter[] filters", "its parameter 'filters' is read from a header as TestApi.ProductFilter[], and a tool sends a header only for a type MVC reads from one string, or an array of such")]
    [InlineData("Microsoft.AspNetCore.Http.IFormFile file", "its parameter 'file' is a form file, which a tool cannot send")]
    [InlineData("[FromForm] Microsoft.AspNetCore.Http.IFormFile? file", "its parameter 'file' is a form file, which a tool cannot send")]
    [InlineData("Microsoft.AspNetCore.Http.IFormFileCollection files", "its parameter 'files' is a form file, which a tool cannot send")]
    [InlineData("Microsoft.AspNetCore.Http.IFormCollection form", "its parameter 'form' is a form file, which a tool cannot send")]
    [InlineData("List<Microsoft.AspNetCore.Http.IFormFile> files", "its parameter 'files' is a form file, which a tool cannot send")]
    [InlineData("FileQuery q", "its parameter 'q' holds a form file in its property 'Attachment', which a tool cannot send")]
    [InlineData("[FromForm] FileListQuery q", "its parameter 'q' holds a form file in its property 'Attachments', which a tool cannot send")]
    [InlineData("[FromBody] FileOrder order", "its parameter 'order' holds a form file in its property 'Lines.Attachment', which a tool cannot send")]
    [InlineData("[FromBody] ProductFilter filter, [FromForm] string name", "it reads 'filter' from a JSON body and 'name' from a form, which one request cannot carry")]
    public void Generator_GivesNoTool_ToAnActionWhoseRequestAToolCannotSend(string parameters, string reason)
    {
        var source = SimpleTypeSource($"[HttpPost(\"x\")] public Task<string> Find({parameters}) => Task.FromResult(string.Empty);") + @"
namespace TestApi
{
    public sealed class FileOrder
    {
        public List<FileOrderLine> Lines { get; set; } = new();
    }

    public sealed class FileOrderLine
    {
        public FileOrder? Order { get; set; }

        public Microsoft.AspNetCore.Http.IFormFile? Attachment { get; set; }
    }
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);
        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Equal($"No MCP tool is generated for action 'OrdersController.Find': {reason}", warning.GetMessage());
        Assert.DoesNotContain(SimpleTypeToolClassName, generated);
    }

    [Fact]
    public void Generator_StillDecidesTheRouteEnd_AgainstAnActionWithoutATool()
    {
        // The app serves DELETE api/orders with All, which reads a header no tool sets, so a call to Delete without its
        // id would run All all the same.
        var source = SimpleTypeSource(@"
        [HttpDelete] public Task<string> All([FromHeader(Name = ""Authorization"")] string? auth) => Task.FromResult(string.Empty);
        [HttpDelete(""{id?}"")] public Task<string> Delete(int? id) => Task.FromResult(string.Empty);");

        var tool = GetToolClass(RunGenerator(source, AllExceptExcludedConfig), "OrdersController_DeleteTool");

        Assert.Contains("[Description(\"Parameter: id\")] int? id,", tool);
        Assert.Contains("ChangesThePath(routeid, isRequired: true)", tool);
    }

    private static string SimpleTypeSource(string actions)
    {
        return SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
    {ApiControllerDeclaration}
    {{
        {actions}
    }}
}}
";
    }
}
