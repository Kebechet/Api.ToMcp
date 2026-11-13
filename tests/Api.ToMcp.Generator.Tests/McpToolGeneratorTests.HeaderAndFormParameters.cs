using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Api.ToMcp.Generator.Parsing;
using Api.ToMcp.Runtime;
using Api.ToMcp.Runtime.Options;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
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
    [InlineData("[FromHeader] string tenant", "\"tenant\", tenant", "[\"tenant\"] = \"tenant\"")]
    [InlineData("[FromHeader(Name = \"X-Tenant\")] string? tenant", "\"X-Tenant\", tenant", "[\"X-Tenant\"] = \"tenant\"")]
    [InlineData("[FromHeader(Name = \"X-Ids\")] int[] ids", "\"X-Ids\", ids", "[\"X-Ids\"] = \"ids\"")]
    [InlineData("[FromHeader(Name = \"X-Statuses\")] List<OrderStatus>? statuses", "\"X-Statuses\", statuses", "[\"X-Statuses\"] = \"statuses\"")]
    [InlineData("[FromHeader(Name = \"X-Order\")] OrderId order", "\"X-Order\", order", "[\"X-Order\"] = \"order\"")]
    [InlineData("[FromHeader(Name = \"If-Match\")] string? etag", "\"If-Match\", etag", "[\"If-Match\"] = \"etag\"")]
    [InlineData("[FromHeader] int page = 2", "\"page\", page == 2 ? null : page", "[\"page\"] = \"page\"")]
    public void Generator_SendsAFromHeaderParameter_AsARequestHeader(string parameter, string expectedArguments, string expectedHeaderArgument)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"x\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains("var headers = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();", tool);
        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers, {expectedArguments}))", tool);
        Assert.Contains(
            $"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Get, route) {{ Headers = headers, HeaderArguments = new System.Collections.Generic.Dictionary<string, string> {{ {expectedHeaderArgument} }} }};",
            tool);
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
        Assert.Contains(
            $"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Post, route) {{ JsonBody = bodyJson, Headers = headers, HeaderArguments = new System.Collections.Generic.Dictionary<string, string> {{ [\"Idempotency-Key\"] = \"key\" }} }};",
            tool);
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
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"name\", name))", tool);
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"f\", filter))", tool);
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"ids\", ids))", tool);
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"other\", other))", tool);
        Assert.Contains($"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.{httpMethod}, route) {{ FormBody = string.Join(\"&\", formParts) }};", tool);
        Assert.DoesNotContain("bodyJson", tool);
        Assert.DoesNotContain("queryParts", tool);
    }

    [Fact]
    public void Generator_RefusesAFormDateTheCulturesCalendarCannotHold_BeforeCallingTheApi()
    {
        var tool = GenerateCompilingSimpleTypeTool("[HttpPut(\"attr/{id:int}\")] public Task<string> Find([FromForm] AttributedQuery q, [FromForm] DateOnly day) => Task.FromResult(string.Empty);");

        Assert.Contains(
            "return new CallToolResult { IsError = true, Content = { new TextContentBlock { Text = \"The argument 'q' holds a date that the tool cannot write in the calendar of the MCP request's culture, in which it writes the form. Give a date that calendar holds; a date left out counts as 0001-01-01.\" } } };",
            tool);
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"day\", day))", tool);
        Assert.Contains("Text = \"The argument 'day' holds a date", tool);
        Assert.True(
            tool.IndexOf("TryAddFormValue", StringComparison.Ordinal) < tool.IndexOf("BeforeInvokeAsync", StringComparison.Ordinal),
            tool);
    }

    [Fact]
    public void Generator_FillsTheRouteFromAFromRouteProperty_AndSendsTheFormPropertiesUnprefixed()
    {
        // MVC reads q's [FromRoute] Id only when no form value carries the q. prefix, so the others go under their own
        // names: Note, which it reads from the form. City it reads from the query string and Tenant from a header.
        var tool = GenerateCompilingSimpleTypeTool("[HttpPut(\"attr/{id:int}\")] public Task<string> Find([FromForm] AttributedQuery q) => Task.FromResult(string.Empty);");

        Assert.Contains($"var routeq_Id = System.Uri.EscapeDataString({Formatter}.Format(q.Id));", tool);
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"Note\", q.Note))", tool);
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
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"filter\", filter))", tool);
        Assert.Contains(
            $"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Post, route) {{ Headers = headers, HeaderArguments = new System.Collections.Generic.Dictionary<string, string> {{ [\"X-Tenant\"] = \"tenant\" }}, FormBody = string.Join(\"&\", formParts) }};",
            tool);
    }

    [Fact]
    public void Generator_RenamesItsOwnVariables_ThatAnActionParameterNames()
    {
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPost(\"x\")] public Task<string> Find([FromHeader(Name = \"X-A\")] string headers, [FromForm] string formParts, [FromForm] string request) => Task.FromResult(string.Empty);");

        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers2, \"X-A\", headers))", tool);
        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts2, \"formParts\", formParts))", tool);
        Assert.Contains(
            $"var request2 = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Post, route) {{ Headers = headers2, HeaderArguments = new System.Collections.Generic.Dictionary<string, string> {{ [\"X-A\"] = \"headers\" }}, FormBody = string.Join(\"&\", formParts2) }};",
            tool);
        Assert.Contains("return await request2.SendAsync(invoker, cancellationToken);", tool);
    }

    [Fact]
    public void Generator_LeavesOutEveryHeaderAToolNeverSets_AsItLeavesOutAService()
    {
        // The AI chose every header a tool sent, so a tool sends none the request carries of its own or the app may trust
        // as set by a front-end proxy. The action gets the header its request carries, or none.
        var headerNames = RequestHeaderRules.NeverSetNames
            .Concat(RequestHeaderRules.NeverSetPrefixes.Select(x => x + "Test"))
            .Append("X-MS-CLIENT-PRINCIPAL-ID")
            .ToList();
        var parameters = string.Join(", ", headerNames.Select((x, i) => $"[FromHeader(Name = \"{x}\")] string? header{i}"));
        var source = SimpleTypeSource($"[HttpGet(\"x\")] public Task<string> Find({parameters}, int page) => Task.FromResult(string.Empty);");

        var diagnostics = GetGeneratorDiagnostics(source, AllExceptExcludedConfig);
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"x\")] public Task<string> Find({parameters}, int page) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain(diagnostics, x => x.Id == UnbuildableRouteId);
        Assert.DoesNotContain("header", tool);
        Assert.Contains("[Description(\"Parameter: page\")] int page,", tool);
        Assert.Contains("invoker.GetAsync(route, cancellationToken)", tool);
    }

    [Theory]
    [InlineData("HttpGet", "[FromHeader(Name = \"Authorization\")] string auth")]
    [InlineData("HttpGet", "[FromHeader] string authorization, [FromHeader(Name = \"X-MCP-Internal-Call\")] string internalCall, [FromHeader] string host")]
    [InlineData("HttpGet", "[FromHeader(Name = \"traceparent\")] string trace")]
    [InlineData("HttpGet", "[FromHeader(Name = \"X-Forwarded-Proto\")] string scheme")]
    [InlineData("HttpPost", "[FromHeader(Name = \"Content-Type\")] string contentType, [FromForm] string name")]
    [InlineData("HttpPut", "[FromHeader(Name = \"content-length\")] string length, [FromBody] ProductFilter filter")]
    [InlineData("HttpGet", "[FromHeader(Name = \"Cookie\")] string? cookie")]
    [InlineData("HttpGet", "[FromHeader(Name = \"X-MS-CLIENT-PRINCIPAL-ID\")] string userId = \"\"")]
    public void Generator_GivesATool_ToAnActionReadingAHeaderAToolNeverSets_WhenItsRequestMayCarryItOrTheActionDoesWithout(string httpAttribute, string parameters)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[{httpAttribute}(\"x\")] public Task<string> Find({parameters}) => Task.FromResult(string.Empty);");

        Assert.DoesNotContain("TryAddHeaderValue", tool);
        Assert.DoesNotContain(" auth", tool);
        Assert.DoesNotContain(" cookie", tool);
        Assert.DoesNotContain("userId", tool);
    }

    [Theory]
    [InlineData("HttpGet", "[FromHeader(Name = \"Cookie\")] string session", "session", "Cookie")]
    [InlineData("HttpGet", "[FromHeader(Name = \"X-MS-CLIENT-PRINCIPAL-ID\")] string userId", "userId", "X-MS-CLIENT-PRINCIPAL-ID")]
    [InlineData("HttpGet", "[FromHeader(Name = \"X-Forwarded-For\"), Microsoft.AspNetCore.Mvc.ModelBinding.BindRequired] int? hops", "hops", "X-Forwarded-For")]
    [InlineData("HttpGet", "[FromHeader(Name = \"Content-Type\")] string contentType", "contentType", "Content-Type")]
    [InlineData("HttpGet", "[FromHeader(Name = \"X-HTTP-Method-Override\"), System.ComponentModel.DataAnnotations.Required] string? method", "method", "X-HTTP-Method-Override")]
    public void Generator_GivesNoTool_ToAnActionRequiringAHeaderAToolNeverSetsAndItsRequestDoesNotCarry(string httpAttribute, string parameter, string parameterName, string headerName)
    {
        var source = SimpleTypeSource($"[{httpAttribute}(\"x\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);
        var generated = RunGenerator(source, AllExceptExcludedConfig);

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(
            $"No MCP tool is generated for action 'OrdersController.Find': its parameter '{parameterName}' is read from the request header '{headerName}', which a tool never sets from an argument and its request does not carry",
            warning.GetMessage());
        Assert.Equal("Find", GetLocatedText(warning.Location, source));
        Assert.DoesNotContain(SimpleTypeToolClassName, generated);
    }

    [Fact]
    public void RuntimeInvoker_RefusesEveryHeaderTheGeneratorNeverSets()
    {
        // The runtime keeps its own list, as the generator cannot reference it; a header missing from it would be sent by a
        // hand-written request, and one the generator lacks would be taken from the AI.
        var invoker = CreateInvokerThatSendsNothing();
        var headerNames = RequestHeaderRules.NeverSetNames.Concat(RequestHeaderRules.NeverSetPrefixes.Select(x => x + "Test"));

        foreach (var headerName in headerNames)
        {
            var request = new McpHttpRequest(HttpMethod.Get, "/x") { Headers = [new(headerName, "v")] };

            var exception = Record.Exception(() => invoker.SendAsync(request).GetAwaiter().GetResult());

            Assert.True(exception is ArgumentException, $"{headerName}: {exception}");
        }
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("Host")]
    [InlineData("X-MCP-Internal-Call")]
    [InlineData("traceparent")]
    [InlineData("tracestate")]
    [InlineData("baggage")]
    [InlineData("Correlation-Context")]
    [InlineData("Content-Type")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Content-Encoding")]
    [InlineData("Content-Language")]
    [InlineData("Content-Location")]
    [InlineData("Content-MD5")]
    [InlineData("Content-Range")]
    [InlineData("Content-Disposition")]
    [InlineData("Allow")]
    [InlineData("Expires")]
    [InlineData("Last-Modified")]
    [InlineData("Connection")]
    [InlineData("Upgrade")]
    [InlineData("TE")]
    [InlineData("Keep-Alive")]
    [InlineData("Trailer")]
    [InlineData("Proxy-Connection")]
    [InlineData("X-HTTP-Method-Override")]
    [InlineData("X-HTTP-Method")]
    [InlineData("X-Method-Override")]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Original-Host")]
    [InlineData("X-Real-IP")]
    [InlineData("True-Client-IP")]
    [InlineData("CF-Connecting-IP")]
    [InlineData("X-Client-IP")]
    [InlineData("X-Cluster-Client-IP")]
    [InlineData("X-MS-CLIENT-PRINCIPAL-ID")]
    [InlineData("X-MS-TOKEN-AAD-ID-TOKEN")]
    [InlineData("X-ARR-ClientCert")]
    [InlineData("X-Client-Cert")]
    [InlineData("X-SSL-CERT")]
    [InlineData("X-Auth-Request-Email")]
    [InlineData("Remote-User")]
    [InlineData("X-Remote-User")]
    [InlineData("X-Goog-Authenticated-User-Email")]
    [InlineData("X-Goog-IAP-JWT-Assertion")]
    [InlineData("X-Amzn-Oidc-Identity")]
    [InlineData("X-Amzn-Mtls-Clientcert")]
    [InlineData("Cf-Access-Jwt-Assertion")]
    [InlineData("X-Pomerium-Jwt-Assertion")]
    [InlineData("X-SSL-Client-Cert")]
    [InlineData("ssl-client-cert")]
    [InlineData("ssl-client-subject-dn")]
    [InlineData("ssl-client-verify")]
    public void HeaderOfTheReadmeTable_IsNoArgumentOfTheTool_AndTheRuntimeInvokerRefusesIt(string headerName)
    {
        // The table of the headers a tool never sets, one case each, so a header dropped from both lists fails here: the
        // tests that compare the lists with each other pass then.
        var source = SimpleTypeSource($"[HttpGet(\"x\")] public Task<string> Find([FromHeader(Name = \"{headerName}\")] string? header, int page) => Task.FromResult(string.Empty);");
        var request = new McpHttpRequest(HttpMethod.Get, "/x") { Headers = [new(headerName, "v")] };

        var tool = GetToolClass(RunGenerator(source, AllExceptExcludedConfig), SimpleTypeToolClassName);
        var exception = Record.Exception(() => CreateInvokerThatSendsNothing().SendAsync(request).GetAwaiter().GetResult());

        Assert.DoesNotContain("header", tool);
        Assert.Contains("[Description(\"Parameter: page\")] int page,", tool);
        Assert.True(exception is ArgumentException, $"{headerName}: {exception}");
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
    [InlineData("[FromForm] FileOrder order", "its parameter 'order' holds a form file in its property 'Lines.Attachment', which a tool cannot send")]
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

    [Theory]
    [InlineData("[FromBody] FileQuery q")]
    [InlineData("FileOrderBox q")]
    public void Generator_GivesATool_ToAnActionWhoseJsonBodyHoldsAFormFile(string parameter)
    {
        // MVC reads the body from JSON and never reads a file into it, so the tool sends the other properties. An
        // [ApiController] infers the body for a type no property of which names a binding source of its own.
        var tool = GenerateCompilingSimpleTypeTool($"[HttpPost(\"x\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(q);", tool);
    }

    [Theory]
    [InlineData("[FromHeader(Name = \"X-Tags\")] string[] tags")]
    [InlineData("[FromHeader(Name = \"X-Tags\")] List<string>? tags")]
    public void Generator_RefusesAHeaderArrayItemMvcWouldReadBackOtherwise_BeforeCallingTheApi(string parameter)
    {
        var tool = GenerateCompilingSimpleTypeTool($"[HttpGet(\"x\")] public Task<string> Find({parameter}) => Task.FromResult(string.Empty);");

        Assert.Contains(
            "Text = \"The argument 'tags' is sent as the request header 'X-Tags', one value per item, and an item cannot be empty and can hold only visible ASCII characters other than '\\\"', spaces and tabs.\"",
            tool);
    }

    [Fact]
    public void Generator_SendsAHeaderAndAForm_UnderTheNameAnyModelNameAttributeGives()
    {
        // MVC binds a parameter under the first name an IModelNameProvider gives, [Bind(Prefix)] and [ModelBinder(Name)]
        // included.
        var tool = GenerateCompilingSimpleTypeTool(
            "[HttpPost(\"x\")] public Task<string> Find([FromForm, Bind(Prefix = \"r\")] ProductFilter review, [FromHeader, ModelBinder(Name = \"X-Tenant\")] string tenant) => Task.FromResult(string.Empty);");

        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"r\", review))", tool);
        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers, \"X-Tenant\", tenant))", tool);
    }

    [Fact]
    public void Generator_MarksATool_ThatSendsHeadersOrAForm()
    {
        // AddMcpTools warns at its first call when the registered invoker cannot send them.
        var tool = GenerateCompilingSimpleTypeTool("[HttpPost(\"x\")] public Task<string> Find([FromForm] string name) => Task.FromResult(string.Empty);");
        var plain = GenerateCompilingSimpleTypeTool("[HttpPost(\"x\")] public Task<string> Find([FromQuery] string name) => Task.FromResult(string.Empty);");

        Assert.Contains("[global::Api.ToMcp.Runtime.McpGeneratedTool(SendsHeadersOrForm = true)]", tool);
        Assert.DoesNotContain("McpGeneratedTool", plain);
    }

    [Theory]
    [InlineData("[Consumes(\"multipart/form-data\")]", "", "it reads 'name' from a form, which its [Consumes] does not accept: a tool sends application/x-www-form-urlencoded")]
    [InlineData("", "[Consumes(\"application/json\", \"text/plain\")]", "it reads 'name' from a form, which its [Consumes] does not accept: a tool sends application/x-www-form-urlencoded")]
    [InlineData("[ValidateAntiForgeryToken]", "", "it reads 'name' from a form, and its [ValidateAntiForgeryToken] checks an antiforgery token, which a tool's request does not carry")]
    [InlineData("", "[AutoValidateAntiforgeryToken]", "it reads 'name' from a form, and its [AutoValidateAntiforgeryToken] checks an antiforgery token, which a tool's request does not carry")]
    [InlineData("[Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryToken]", "", "it reads 'name' from a form, and its [RequireAntiforgeryToken] checks an antiforgery token, which a tool's request does not carry")]
    public void Generator_GivesNoTool_ToAFormActionThatRefusesTheToolsForm(string actionAttribute, string controllerAttribute, string reason)
    {
        var source = SimpleTypeSources.Usings + SimpleTypeSources.Types + $@"
namespace TestApi
{{
    {controllerAttribute} {ApiControllerDeclaration}
    {{
        {actionAttribute} [HttpPost(""x"")] public Task<string> Find([FromForm] string name) => Task.FromResult(string.Empty);
    }}
}}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig), x => x.Id == UnbuildableRouteId);

        Assert.Equal($"No MCP tool is generated for action 'OrdersController.Find': {reason}", warning.GetMessage());
    }

    [Theory]
    [InlineData("[Consumes(\"application/x-www-form-urlencoded\", \"multipart/form-data\")]", "", "HttpPost")]
    [InlineData("[Consumes(\"application/*\")]", "[Consumes(\"multipart/form-data\")]", "HttpPost")]
    [InlineData("[IgnoreAntiforgeryToken]", "[ValidateAntiForgeryToken]", "HttpPost")]
    [InlineData("", "[AutoValidateAntiforgeryToken]", "HttpGet")]
    [InlineData("[Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryToken(false)]", "", "HttpPost")]
    public void Generator_GivesATool_ToAFormActionThatTakesTheToolsForm(string actionAttribute, string controllerAttribute, string httpAttribute)
    {
        var tool = GenerateCompilingSimpleTypeTool(
            $"{actionAttribute} [{httpAttribute}(\"x\")] public Task<string> Find([FromForm] string name) => Task.FromResult(string.Empty);",
            $"{controllerAttribute} {ApiControllerDeclaration}");

        Assert.Contains($"if (!{Formatter}.TryAddFormValue(formParts, \"name\", name))", tool);
    }

    [Fact]
    public void Generator_StillDecidesTheRouteEnd_AgainstAnActionWithoutATool()
    {
        // The app serves DELETE api/orders with All, which requires a cookie a tool's request never carries, so a call to
        // Delete without its id would run All all the same.
        var source = SimpleTypeSource(@"
        [HttpDelete] public Task<string> All([FromHeader(Name = ""Cookie"")] string cookie) => Task.FromResult(string.Empty);
        [HttpDelete(""{id?}"")] public Task<string> Delete(int? id) => Task.FromResult(string.Empty);");

        var tool = GetToolClass(RunGenerator(source, AllExceptExcludedConfig), "OrdersController_DeleteTool");

        Assert.Contains("[Description(\"Parameter: id\")] int? id,", tool);
        Assert.Contains("ChangesThePath(routeid, isRequired: true)", tool);
    }

    [Fact]
    public void Endpoint_SendsAHeaderItParsesFromOneString_AsARequestHeader()
    {
        var source = MinimalApi(@"
            app.MapGet(""/x"", ([FromHeader(Name = ""X-Tenant"")] string tenant, [FromHeader] int? page) => 1).WithName(""X"");");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_XTool");

        Assert.Contains("[Description(\"Parameter: tenant\")] string tenant,", tool);
        Assert.Contains("[Description(\"Parameter: page\")] int? page = null,", tool);
        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers, \"X-Tenant\", tenant))", tool);
        Assert.Contains($"if (!{Formatter}.TryAddHeaderValue(headers, \"page\", page))", tool);
        Assert.Contains(
            $"var request = new {McpHttpRequestType}(System.Net.Http.HttpMethod.Get, route) {{ Headers = headers, HeaderArguments = new System.Collections.Generic.Dictionary<string, string> {{ [\"X-Tenant\"] = \"tenant\", [\"page\"] = \"page\" }} }};",
            tool);
        Assert.DoesNotContain("queryParts", tool);
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

    /// <summary>
    /// An invoker the app constructs itself, whose client answers every request with 200 without sending it.
    /// </summary>
    private static McpHttpInvoker CreateInvokerThatSendsNothing()
    {
        return new McpHttpInvoker(
            new HttpClient(new NoRequestHandler()),
            new FixedBaseUrl(),
            new HttpContextAccessor(),
            NullLogger<McpHttpInvoker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new McpScopeOptions()));
    }

    private sealed class NoRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FixedBaseUrl : ISelfBaseUrlProvider
    {
        public string GetBaseUrl() => "http://localhost";
    }
}
