using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Api.ToMcp.Abstractions.Scopes;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Runs generated tools against real MVC. The test controllers and the tools the generator emits for them are compiled
/// into one assembly, MVC serves the controllers on a loopback port, and each tool sends its request there; every action
/// echoes what MVC bound. A binding rule the generator gets wrong fails here even when the emitted code looks right.
/// </summary>
public sealed class GeneratedToolsAgainstMvcTests : IClassFixture<GeneratedToolsAgainstMvcTests.MvcHost>
{
    private const string Controllers = @"
namespace TestApi
{
    [ApiController]
    [Route(""api"")]
    public class EchoController : ControllerBase
    {
        [HttpGet(""attr/{id:int}"")]
        public object GetAttributed(AttributedQuery q) => q;

        [HttpPost(""attr/{id:int}"")]
        public object PostAttributed(AttributedQuery q) => q;

        [HttpGet(""query-only"")]
        public object GetQueryOnly(QueryOnlyFilter q) => q;

        [HttpGet(""aliased/{orderId}"")]
        public object GetAliased(AliasedRouteQuery q) => q;

        [HttpGet(""from-route/{orderId}"")]
        public object GetFromRoute([FromRoute] AliasedRouteQuery q) => q;

        [HttpPost(""record-property"")]
        public object PostRecordProperty(RecordPropertyFilter q) => q;

        [HttpPost(""record-parameter"")]
        public object PostRecordParameter(RecordParameterFilter q) => q;

        [HttpPost(""record-with-query"")]
        public object PostRecordWithQuery(RecordWithQueryProperty q) => q;

        [HttpPost(""named-binder"")]
        public object PostNamedBinder(NamedModelBinderFilter q) => q;

        [HttpPost(""named-bind-property"")]
        public object PostNamedBindProperty(NamedBindPropertyFilter q) => q;

        [HttpPost(""keyword/{namespace}/{id}"")]
        public object PostKeyword(string @namespace, KeywordQuery q, [FromBody] ProductFilter @object, [FromQuery] string? @class)
            => new { @namespace, q.Id, q.@event, @class, @object.City };

        [HttpGet(""private-getter/{id}"")]
        public object GetPrivateGetter(PrivateGetterQuery q) => new { q.Id, q.Echo };

        [HttpGet(""two/{id}"")]
        public object GetTwo(AttributedQuery a, NotedShipment b) => new { aId = a.Id, a.City, bId = b.Id, b.Note };

        [HttpGet(""direct/{id}"")]
        public object GetDirect(int id, AttributedQuery q) => new { id, qId = q.Id, q.City };

        [HttpGet(""order"")]
        public object GetOrder(OrderId orderId) => new { orderId = orderId.Value };

        [HttpPost(""body-id/{value}"")]
        public object PostBodyId([FromBody] OrderId id) => new { id = id.Value };

        [HttpPost(""upper/{Id}"")]
        public object PostUpper(int id) => new { id };
    }

    [Route(""plain"")]
    public class PlainEchoController : ControllerBase
    {
        [HttpPost(""search"")]
        public object PostSearch(ProductFilter filter) => filter;

        [HttpGet(""search"")]
        public object GetSearch(ProductFilter? filter) => filter ?? new ProductFilter();

        [HttpPut(""shipments/{id:int}"")]
        public object PutShipment(Shipment shipment) => shipment;

        [HttpPost(""simple"")]
        public object PostSimple(string name, int? count) => new { name, count };
    }
}
";

    private const string OrderGuid = "0189dfd3-5bd1-a760-bed1-d1d5751e4e8c";

    private readonly MvcHost _host;

    public GeneratedToolsAgainstMvcTests(MvcHost host)
    {
        _host = host;
    }

    [Theory]
    [InlineData("EchoController_GetAttributedTool", @"{""q"":{""id"":7,""city"":""Brno"",""note"":""n1"",""tenant"":""t1""}}", @"{""id"":7,""city"":""Brno"",""note"":""n1"",""tenant"":null}")]
    [InlineData("EchoController_PostAttributedTool", @"{""q"":{""id"":7,""city"":""Brno"",""note"":""n1""}}", @"{""id"":7,""city"":""Brno"",""note"":""n1"",""tenant"":null}")]
    [InlineData("EchoController_GetQueryOnlyTool", @"{""q"":{""city"":""Brno"",""note"":""n1""}}", @"{""city"":""Brno"",""note"":""n1""}")]
    [InlineData("EchoController_GetAliasedTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno""}}", @"{""order"":""" + OrderGuid + @""",""city"":""Brno""}")]
    [InlineData("EchoController_GetFromRouteTool", @"{""q"":{""order"":""" + OrderGuid + @""",""city"":""Brno""}}", @"{""order"":""" + OrderGuid + @""",""city"":""Brno""}")]
    [InlineData("EchoController_PostRecordPropertyTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostRecordParameterTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostRecordWithQueryTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostNamedBinderTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostNamedBindPropertyTool", @"{""q"":{""city"":""Brno"",""page"":3}}", @"{""city"":""Brno"",""page"":3}")]
    [InlineData("EchoController_PostKeywordTool", @"{""namespace"":""ns"",""q"":{""id"":7,""event"":""launch""},""class"":""c1"",""object"":{""city"":""Brno""}}", @"{""namespace"":""ns"",""id"":7,""event"":""launch"",""class"":""c1"",""city"":""Brno""}")]
    [InlineData("EchoController_GetPrivateGetterTool", @"{""q"":{""id"":7}}", @"{""id"":7,""echo"":null}")]
    [InlineData("EchoController_GetTwoTool", @"{""a"":{""id"":7,""city"":""Brno""},""b"":{""id"":7,""note"":""n1""}}", @"{""aId"":7,""city"":""Brno"",""bId"":7,""note"":""n1""}")]
    [InlineData("EchoController_GetDirectTool", @"{""id"":7,""q"":{""id"":7,""city"":""Brno""}}", @"{""id"":7,""qId"":7,""city"":""Brno""}")]
    [InlineData("EchoController_GetOrderTool", @"{""orderId"":{""value"":""" + OrderGuid + @"""}}", @"{""orderId"":""" + OrderGuid + @"""}")]
    [InlineData("EchoController_PostBodyIdTool", @"{""id"":{""value"":""" + OrderGuid + @"""}}", @"{""id"":""" + OrderGuid + @"""}")]
    [InlineData("EchoController_PostUpperTool", @"{""id"":7}", @"{""id"":7}")]
    [InlineData("PlainEchoController_PostSearchTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno""}")]
    [InlineData("PlainEchoController_GetSearchTool", @"{""filter"":{""city"":""Brno""}}", @"{""city"":""Brno""}")]
    [InlineData("PlainEchoController_PutShipmentTool", @"{""shipment"":{""id"":9,""note"":""fragile""}}", @"{""id"":9,""note"":""fragile""}")]
    [InlineData("PlainEchoController_PostSimpleTool", @"{""name"":""a"",""count"":2}", @"{""name"":""a"",""count"":2}")]
    public async Task Tool_DeliversItsArguments_AsMvcBindsThem(string toolClassName, string arguments, string expectedEcho)
    {
        var (statusCode, body) = await _host.InvokeToolAsync(toolClassName, arguments);

        Assert.True(statusCode == 200, $"{toolClassName}: {statusCode} {body}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedEcho), JsonNode.Parse(body)), $"{toolClassName}: expected {expectedEcho}, MVC bound {body}");
    }

    /// <summary>
    /// The test controllers with their generated tools, served by MVC on a loopback port the OS picks.
    /// </summary>
    public sealed class MvcHost : IAsyncLifetime
    {
        private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web);

        private Assembly _assembly = null!;
        private WebApplication _app = null!;
        private RecordingInvoker _invoker = null!;

        public async Task InitializeAsync()
        {
            _assembly = CompileWithGeneratedTools(SimpleTypeSources.Usings + SimpleTypeSources.Types + Controllers);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddControllers().AddApplicationPart(_assembly);
            _app = builder.Build();
            _app.MapControllers();
            await _app.StartAsync();

            var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _invoker = new RecordingInvoker(new HttpClient { BaseAddress = new Uri(address) });
        }

        public async Task DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        /// <summary>
        /// Calls a generated tool with arguments deserialized from JSON, as the MCP server does, and returns what the
        /// action answered.
        /// </summary>
        public async Task<(int StatusCode, string Body)> InvokeToolAsync(string toolClassName, string arguments)
        {
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            var argumentValues = JsonNode.Parse(arguments)!.AsObject();
            var parameterValues = toolMethod
                .GetParameters()
                .Select(x => x.ParameterType == typeof(IMcpHttpInvoker) ? _invoker
                    : x.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                    : argumentValues[x.Name!]?.Deserialize(x.ParameterType, ArgumentOptions))
                .ToArray();

            await (Task)toolMethod.Invoke(null, parameterValues)!;

            return _invoker.LastResponse;
        }

        private static Assembly CompileWithGeneratedTools(string source)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
            var compilation = CSharpCompilation.Create(
                $"ToolsAgainstMvc{Guid.NewGuid():N}",
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var generatorConfig = new InMemoryAdditionalText("Mcp/generator.json", @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [] }");

            CSharpGeneratorDriver
                .Create(new McpToolGenerator())
                .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(generatorConfig))
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            using var assemblyStream = new MemoryStream();
            var emitResult = outputCompilation.Emit(assemblyStream);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

            // The default context resolves the converters [TypeConverter] names, and MVC finds the controllers there.
            assemblyStream.Position = 0;
            return AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
        }
    }

    private sealed class RecordingInvoker(HttpClient client) : IMcpHttpInvoker
    {
        public (int StatusCode, string Body) LastResponse { get; private set; }

        public Task<CallToolResult> GetAsync(string route, CancellationToken ct = default) => SendAsync(HttpMethod.Get, route, null, ct);

        public Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Post, route, jsonBody, ct);

        public Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Put, route, jsonBody, ct);

        public Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, route, jsonBody, ct);

        public Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, route, null, ct);

        public Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default) => Task.CompletedTask;

        private async Task<CallToolResult> SendAsync(HttpMethod method, string route, string? jsonBody, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, route);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
            }

            using var response = await client.SendAsync(request, ct);
            LastResponse = ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));

            return new CallToolResult();
        }
    }

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }
}
