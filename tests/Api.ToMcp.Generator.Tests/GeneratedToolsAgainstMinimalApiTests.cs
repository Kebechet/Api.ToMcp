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
using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Runs generated tools against real Minimal API endpoints. The endpoints and the tools the generator emits for them are
/// compiled into one assembly, the app maps the endpoints on a loopback port, and each tool sends its request there;
/// every endpoint echoes what it bound. A binding rule the generator gets wrong fails here even when the emitted code
/// looks right. See https://github.com/Kebechet/Api.ToMcp/issues/11.
/// </summary>
public sealed class GeneratedToolsAgainstMinimalApiTests : IClassFixture<GeneratedToolsAgainstMinimalApiTests.MinimalApiHost>
{
    private const string Usings = @"#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
";

    private const string Endpoints = @"
namespace TestApi
{
    public sealed class EchoDto
    {
        public int Id { get; set; }
        public string? City { get; set; }
    }

    public sealed class EchoService
    {
        public string Name => ""service"";
    }

    public enum EchoStatus
    {
        Open = 1,
        Closed = 2
    }

    public readonly record struct EchoCode(string Value)
    {
        public static bool TryParse(string? text, out EchoCode code)
        {
            code = new EchoCode(text ?? """");
            return text is not null;
        }

        public override string ToString() => Value;
    }

    public record EchoQuery(int Id, [FromQuery(Name = ""p"")] int Page = 1);

    public sealed class EchoFilter
    {
        public string? City { get; set; }

        [FromQuery(Name = ""s"")]
        public EchoStatus? Status { get; set; }
    }

    public static class EchoEndpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapGet(""/values/{id:int}"", (int id, HttpRequest request, string? name, int page = 1) => new { id, name, page, query = request.QueryString.Value })
                .WithName(""Values"");
            app.MapGet(""/alias"", ([FromQuery(Name = ""q"")] string query) => new { query }).WithName(""Alias"");
            app.MapGet(""/special/{id}"", (HttpContext context, HttpRequest request, CancellationToken ct, ClaimsPrincipal user, int id) => new { id, path = request.Path.Value })
                .WithName(""Special"");
            app.MapGet(""/header"", ([FromHeader(Name = ""X-Tenant"")] string? tenant) => new { tenant }).WithName(""OptionalHeader"");
            app.MapGet(""/tags"", (string[] tags, int[] ids) => new { tags, ids }).WithName(""Tags"");
            app.MapGet(""/typed/{date}"", (DateOnly date, Guid id, EchoStatus status, EchoCode code) => new { date, id, status, code = code.Value })
                .WithName(""Typed"");
            app.MapGet(""/optional/{id?}"", (int? id, HttpRequest request) => new { id, path = request.Path.Value }).WithName(""OptionalRoute"");
            app.MapGet(""/files/{*path}"", (string path) => new { path }).WithName(""CatchAll"");
            app.MapGet(""/services"", (EchoService service, IServiceProvider services, int take) => new { service = service.Name, take }).WithName(""GetServices"");
            app.MapPost(""/body"", (EchoDto dto) => dto).WithName(""Body"");
            app.MapPost(""/body-and-services"", ([FromBody] EchoDto dto, EchoService service, IServiceProvider services) => new { dto.City, service = service.Name })
                .WithName(""BodyAndServices"");
            app.MapPost(""/array-body"", (int[] ids) => new { ids }).WithName(""ArrayBody"");
            app.MapPut(""/body/{id:int}"", (int id, EchoDto dto) => new { id, dtoId = dto.Id, dto.City }).WithName(""PutBody"");
            app.MapPatch(""/patch/{id:int}"", (int id, EchoDto? dto) => new { id, city = dto?.City }).WithName(""PatchBody"");
            app.MapDelete(""/items/{id:int}"", (int id, HttpRequest request) => new { id, method = request.Method }).WithName(""DeleteItem"");
            app.MapMethods(""/methods/{id:int}"", new[] { ""PATCH"" }, (int id, HttpRequest request) => new { id, method = request.Method }).WithName(""Methods"");
            app.MapGet(""/as/{id:int}"", ([AsParameters] EchoQuery query) => query).WithName(""AsRecord"");
            app.MapGet(""/as-filter"", ([AsParameters] EchoFilter filter) => filter).WithName(""AsFilter"");
            app.MapGet(""/derived/{orderId:int}/lines"", (int orderId) => new { orderId });

            var tenants = app.MapGroup(""/group"").MapGroup(""{tenant}"");
            tenants.MapGet(""/codes/{code}"", GetCode);
            tenants.MapGet(""/local"", Local);
            tenants.MapEchoes();

            static object Local(string tenant, int take = 3) => new { tenant, take };
        }

        public static RouteGroupBuilder MapEchoes(this RouteGroupBuilder group)
        {
            group.MapPost(""/echo"", (string tenant, EchoDto dto) => new { tenant, dto.City }).WithName(""GroupEcho"");
            return group;
        }

        private static object GetCode(string tenant, EchoCode code) => new { tenant, code = code.Value };
    }
}
";

    private const string OrderGuid = "0189dfd3-5bd1-a760-bed1-d1d5751e4e8c";

    private readonly MinimalApiHost _host;

    public GeneratedToolsAgainstMinimalApiTests(MinimalApiHost host)
    {
        _host = host;
    }

    [Theory]
    [InlineData("EchoEndpoints_ValuesTool", @"{""id"":7,""name"":""a""}", @"{""id"":7,""name"":""a"",""page"":1,""query"":""?name=a""}")]
    [InlineData("EchoEndpoints_ValuesTool", @"{""id"":7,""page"":3}", @"{""id"":7,""name"":null,""page"":3,""query"":""?page=3""}")]
    [InlineData("EchoEndpoints_AliasTool", @"{""query"":""brno""}", @"{""query"":""brno""}")]
    [InlineData("EchoEndpoints_SpecialTool", @"{""id"":7}", @"{""id"":7,""path"":""/special/7""}")]
    [InlineData("EchoEndpoints_OptionalHeaderTool", @"{}", @"{""tenant"":null}")]
    [InlineData("EchoEndpoints_TagsTool", @"{""tags"":[""a"",""b c""],""ids"":[1,2]}", @"{""tags"":[""a"",""b c""],""ids"":[1,2]}")]
    [InlineData("EchoEndpoints_TypedTool", @"{""date"":""2026-09-28"",""id"":""" + OrderGuid + @""",""status"":2,""code"":{""value"":""A-7""}}", @"{""date"":""2026-09-28"",""id"":""" + OrderGuid + @""",""status"":2,""code"":""A-7""}")]
    [InlineData("EchoEndpoints_OptionalRouteTool", @"{""id"":5}", @"{""id"":5,""path"":""/optional/5""}")]
    [InlineData("EchoEndpoints_OptionalRouteTool", @"{}", @"{""id"":null,""path"":""/optional/""}")]
    [InlineData("EchoEndpoints_CatchAllTool", @"{""path"":""a/b c""}", @"{""path"":""a/b c""}")]
    [InlineData("EchoEndpoints_GetServicesTool", @"{""take"":2}", @"{""service"":""service"",""take"":2}")]
    [InlineData("EchoEndpoints_BodyTool", @"{""dto"":{""id"":9,""city"":""Brno""}}", @"{""id"":9,""city"":""Brno""}")]
    [InlineData("EchoEndpoints_BodyAndServicesTool", @"{""dto"":{""city"":""Brno""}}", @"{""city"":""Brno"",""service"":""service""}")]
    [InlineData("EchoEndpoints_ArrayBodyTool", @"{""ids"":[1,2]}", @"{""ids"":[1,2]}")]
    [InlineData("EchoEndpoints_PutBodyTool", @"{""id"":7,""dto"":{""id"":9,""city"":""Brno""}}", @"{""id"":7,""dtoId"":9,""city"":""Brno""}")]
    [InlineData("EchoEndpoints_PatchBodyTool", @"{""id"":7,""dto"":{""city"":""Brno""}}", @"{""id"":7,""city"":""Brno""}")]
    [InlineData("EchoEndpoints_DeleteItemTool", @"{""id"":7}", @"{""id"":7,""method"":""DELETE""}")]
    [InlineData("EchoEndpoints_MethodsTool", @"{""id"":7}", @"{""id"":7,""method"":""PATCH""}")]
    [InlineData("EchoEndpoints_AsRecordTool", @"{""Id"":7,""Page"":3}", @"{""id"":7,""page"":3}")]
    [InlineData("EchoEndpoints_AsRecordTool", @"{""Id"":7}", @"{""id"":7,""page"":1}")]
    [InlineData("EchoEndpoints_AsFilterTool", @"{""City"":""Brno"",""Status"":2}", @"{""city"":""Brno"",""status"":2}")]
    [InlineData("EchoEndpoints_GetDerivedByOrderIdLinesTool", @"{""orderId"":7}", @"{""orderId"":7}")]
    [InlineData("EchoEndpoints_GetCodeTool", @"{""tenant"":""t1"",""code"":{""value"":""A-7""}}", @"{""tenant"":""t1"",""code"":""A-7""}")]
    [InlineData("EchoEndpoints_LocalTool", @"{""tenant"":""t1""}", @"{""tenant"":""t1"",""take"":3}")]
    [InlineData("EchoEndpoints_GroupEchoTool", @"{""tenant"":""t1"",""dto"":{""city"":""Brno""}}", @"{""tenant"":""t1"",""city"":""Brno""}")]
    public async Task Tool_DeliversItsArguments_AsTheMinimalApiBindsThem(string toolClassName, string arguments, string expectedEcho)
    {
        var (statusCode, body) = await _host.InvokeToolAsync(toolClassName, arguments);

        Assert.True(statusCode == StatusCodes.Status200OK, $"{toolClassName}: {statusCode} {body}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedEcho), JsonNode.Parse(body)), $"{toolClassName}: expected {expectedEcho}, the endpoint bound {body}");
    }

    [Theory]
    [InlineData("EchoEndpoints_SpecialTool", new[] { "id" })]
    [InlineData("EchoEndpoints_OptionalHeaderTool", new string[0])]
    [InlineData("EchoEndpoints_GetServicesTool", new[] { "take" })]
    [InlineData("EchoEndpoints_BodyAndServicesTool", new[] { "dto" })]
    [InlineData("EchoEndpoints_AsRecordTool", new[] { "Id", "Page" })]
    public void Tool_AsksOnlyForWhatTheRequestCarries(string toolClassName, string[] expectedArguments)
    {
        // The request itself, services, the cancellation token and a header the tool cannot send are no arguments.
        var properties = _host.GetInputSchema(toolClassName).GetProperty("properties");

        Assert.Equal(expectedArguments.Order(), properties.EnumerateObject().Select(x => x.Name).Order());
    }

    [Fact]
    public void EveryGeneratedTool_IsBuiltByTheMcpSdk()
    {
        var toolClassNames = _host.ToolClassNames;

        Assert.Equal(22, toolClassNames.Count);
        Assert.All(toolClassNames, x => _host.GetInputSchema(x));
    }

    /// <summary>
    /// The test endpoints with their generated tools, mapped by a Minimal API app on a loopback port the OS picks.
    /// </summary>
    public sealed class MinimalApiHost : IAsyncLifetime
    {
        private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web);

        private Assembly _assembly = null!;
        private WebApplication _app = null!;
        private HttpClient _client = null!;
        private RecordingInvoker _invoker = null!;

        public async Task InitializeAsync()
        {
            _assembly = CompileWithGeneratedTools(Usings + Endpoints);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(_assembly.GetType("TestApi.EchoService", throwOnError: true)!);
            _app = builder.Build();
            _assembly.GetType("TestApi.EchoEndpoints", throwOnError: true)!.GetMethod("Map")!.Invoke(null, [_app]);
            await _app.StartAsync();

            var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _client = new HttpClient { BaseAddress = new Uri(address) };
            _invoker = new RecordingInvoker(_client);
        }

        public async Task DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        /// <summary>
        /// Calls a generated tool with arguments deserialized from JSON, as the MCP server does, and returns what the
        /// endpoint answered.
        /// </summary>
        public async Task<(int StatusCode, string Body)> InvokeToolAsync(string toolClassName, string arguments)
        {
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            var argumentValues = JsonNode.Parse(arguments)!.AsObject();
            var parameterValues = toolMethod
                .GetParameters()
                .Select(x => x.ParameterType == typeof(IMcpHttpInvoker) ? _invoker
                    : x.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                    : argumentValues.TryGetPropertyValue(x.Name!, out var argument) ? argument?.Deserialize(x.ParameterType, ArgumentOptions)
                    : x.HasDefaultValue ? x.DefaultValue
                    : null)
                .ToArray();

            await (Task)toolMethod.Invoke(null, parameterValues)!;

            return _invoker.LastResponse;
        }

        public IReadOnlyList<string> ToolClassNames => _assembly.GetTypes()
            .Where(x => x.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Select(x => x.Name)
            .ToList();

        /// <summary>
        /// The input schema the MCP SDK gives a generated tool, with the invoker bound from the services, as in the app.
        /// </summary>
        public JsonElement GetInputSchema(string toolClassName)
        {
            var toolMethod = _assembly.GetType($"Api.ToMcp.Generated.{toolClassName}", throwOnError: true)!.GetMethod("InvokeAsync")!;
            using var services = new ServiceCollection().AddSingleton<IMcpHttpInvoker>(_invoker).BuildServiceProvider();

            return McpServerTool.Create(toolMethod, options: new McpServerToolCreateOptions { Services = services }).ProtocolTool.InputSchema;
        }

        private static Assembly CompileWithGeneratedTools(string source)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
            var compilation = CSharpCompilation.Create(
                $"ToolsAgainstMinimalApi{Guid.NewGuid():N}",
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var generatorConfig = new InMemoryAdditionalText("Mcp/generator.json", @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [], ""minimalApis"": true }");

            CSharpGeneratorDriver
                .Create(new McpToolGenerator())
                .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(generatorConfig))
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

            // Every endpoint becomes a tool: a warning here means one of them did not.
            Assert.Empty(generatorDiagnostics);

            using var assemblyStream = new MemoryStream();
            var emitResult = outputCompilation.Emit(assemblyStream);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

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
