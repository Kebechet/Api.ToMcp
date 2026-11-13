using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Minimal API endpoints, mapped by <c>app.MapGet(...)</c> and the like, as tools: where the generator finds them and
/// their routes, how it names and selects them, what it sends, and MCP010 and MCP011 where it cannot follow them. The
/// binding rules run against a real app in <see cref="GeneratedToolsAgainstMinimalApiTests"/>. See
/// https://github.com/Kebechet/Api.ToMcp/issues/11.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string UnbuildableEndpointId = "MCP010";
    private const string SeveralHttpMethodsId = "MCP011";
    private const string EndpointsLeftOutId = "MCP012";

    /// <summary>
    /// AllExceptExcluded mode with the opt-in that makes it select Minimal API endpoints too.
    /// </summary>
    private const string MinimalApisConfig =
        @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [], ""minimalApis"": true }";

    private const string MinimalApiUsings = @"#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;
";

    private const string MinimalApiModels = @"
namespace TestApi
{
    public sealed class Order
    {
        public int Id { get; set; }
        public string? Note { get; set; }
    }

    public sealed class OrderService
    {
    }
}
";

    [Theory]
    [InlineData("MapGet", "GET", "GetAsync(route, cancellationToken)", "McpScope.Read")]
    [InlineData("MapPost", "POST", "PostAsync(route, null, cancellationToken)", "McpScope.Write")]
    [InlineData("MapPut", "PUT", "PutAsync(route, null, cancellationToken)", "McpScope.Write")]
    [InlineData("MapPatch", "PATCH", "PatchAsync(route, null, cancellationToken)", "McpScope.Write")]
    [InlineData("MapDelete", "DELETE", "DeleteAsync(route, cancellationToken)", "McpScope.Delete")]
    public void Endpoint_GetsATool_CallingItWithItsHttpMethod(string mapMethod, string httpMethod, string invokerCall, string scope)
    {
        var source = MinimalApi($@"app.{mapMethod}(""/api/orders/{{id}}"", (int id) => id);");
        var verb = httpMethod.Substring(0, 1) + httpMethod.Substring(1).ToLowerInvariant();
        var toolClassName = $"Endpoints_{verb}ApiOrdersByIdTool";

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, toolClassName);

        Assert.Contains($"[McpServerTool(Name = \"Endpoints_{verb}ApiOrdersById\"", tool);
        Assert.Contains("var route = $\"/api/orders/{routeid}\";", tool);
        Assert.Contains(invokerCall, tool);
        Assert.Contains($"BeforeInvokeAsync({scope}, cancellationToken)", tool);
        Assert.Contains($"[Description(\"Invokes {httpMethod} /api/orders/{{id}}\")]", tool);
        Assert.Contains($"typeof({toolClassName})", RunGenerator(source, MinimalApisConfig));
    }

    [Fact]
    public void Endpoint_MappedByAStaticCall_GetsATool()
    {
        var source = MinimalApi(@"EndpointRouteBuilderExtensions.MapGet(app, ""/ping"", () => ""pong"");");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_GetPingTool");

        Assert.Contains("var route = $\"/ping\";", tool);
    }

    [Theory]
    [InlineData(@"private const string Route = ""/api/orders""; public static void Map(WebApplication app) { app.MapGet(Route, () => 1); }")]
    [InlineData(@"private const string Api = ""/api""; public static void Map(WebApplication app) { app.MapGet($""{Api}/orders"", () => 1); }")]
    [InlineData(@"public static void Map(WebApplication app) { app.MapGet(""/api/"" + ""orders"", () => 1); }")]
    public void Endpoint_TakesARoutePattern_FromAConstant(string members)
    {
        var source = MinimalApiClass(members);

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_GetApiOrdersTool");

        Assert.Contains("var route = $\"/api/orders\";", tool);
    }

    [Fact]
    public void Endpoint_TakesThePrefixes_OfNestedRouteGroupsHeldInVariables()
    {
        var source = MinimalApi(@"
            var api = app.MapGroup(""/api"").WithTags(""Shop"").RequireAuthorization();
            var orders = api.MapGroup(""orders/"");
            orders.MapGet(""/{id:int}"", (int id) => id);
            api.MapGroup(""/products"").MapGet("""", () => 1);");

        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Equal("var route = $\"/api/orders/{routeid}\";", GetRouteLine(generated, "Endpoints_GetApiOrdersByIdTool"));
        Assert.Equal("var route = $\"/api/products\";", GetRouteLine(generated, "Endpoints_GetApiProductsTool"));
        AssertCompiles(source, MinimalApisConfig);
    }

    [Fact]
    public void Endpoint_MappedInAnExtensionMethod_TakesThePrefixOfEachCallOfIt()
    {
        // The prefix comes from the callers, in another file too; each call maps the endpoint at a route of its own.
        const string caller = MinimalApiUsings + @"
namespace TestApi
{
    public static class Program2
    {
        public static void Map(WebApplication app)
        {
            app.MapGroup(""/v1"").MapOrders();
            var v2 = app.MapGroup(""/v2"");
            OrderEndpoints.MapOrders(v2);
        }
    }
}
";
        const string endpoints = MinimalApiUsings + @"
namespace TestApi
{
    public static class OrderEndpoints
    {
        public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder builder)
        {
            var orders = builder.MapGroup(""/orders"");
            orders.MapGet(""/{id}"", (int id) => id);
            return builder;
        }
    }
}
";
        var compilation = CreateCompilation(caller, endpoints);
        var runResult = CreateDriver(MinimalApisConfig).RunGenerators(compilation).GetRunResult();
        var generated = string.Join("\n", runResult.Results.SelectMany(x => x.GeneratedSources).Select(x => x.SourceText.ToString()));

        Assert.Equal("var route = $\"/v1/orders/{routeid}\";", GetRouteLine(generated, "OrderEndpoints_GetV1OrdersByIdTool"));
        Assert.Equal("var route = $\"/v2/orders/{routeid}\";", GetRouteLine(generated, "OrderEndpoints_GetV2OrdersByIdTool"));
        Assert.Empty(runResult.Diagnostics);
    }

    [Fact]
    public void Endpoint_MappedByAClassCalledThroughItsInterface_TakesThePrefixOfThatCall()
    {
        // Apps commonly list classes that map endpoints and call each through an interface of their own.
        const string source = MinimalApiUsings + @"
namespace TestApi
{
    public interface IEndpointModule
    {
        void MapEndpoints(IEndpointRouteBuilder builder);
    }

    public sealed class OrderModule : IEndpointModule
    {
        public void MapEndpoints(IEndpointRouteBuilder builder) => builder.MapGet(""/orders/{id}"", (int id) => id).WithName(""GetOrder"");
    }

    public abstract class ModuleBase
    {
        public abstract void Map(IEndpointRouteBuilder builder);
    }

    public sealed class ItemModule : ModuleBase
    {
        public override void Map(IEndpointRouteBuilder builder) => builder.MapGet(""/items"", () => 1).WithName(""GetItems"");
    }

    public static class Startup
    {
        public static void Map(WebApplication app, IEnumerable<IEndpointModule> modules, ModuleBase itemModule)
        {
            var api = app.MapGroup(""/api"");
            foreach (var module in modules)
            {
                module.MapEndpoints(api);
            }

            itemModule.Map(api);
        }
    }
}
";
        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Equal("var route = $\"/api/orders/{routeid}\";", GetRouteLine(generated, "OrderModule_GetOrderTool"));
        Assert.Equal("var route = $\"/api/items\";", GetRouteLine(generated, "ItemModule_GetItemsTool"));
        Assert.Empty(GetGeneratorDiagnostics(source, MinimalApisConfig));
    }

    [Fact]
    public void Endpoint_OfAHandlerMappedAtSeveralRoutes_GetsANameForEachRoute()
    {
        // A method group, a .WithName and a ToolName are the same for each call of the method that maps them, and the SDK
        // keeps one tool of a name, so each route's name starts with the words that tell its prefix apart.
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app)
        {
            app.MapGroup(""/api/v1/admin"").MapOrders();
            app.MapGroup(""/api/v2/admin"").MapOrders();
        }

        public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder builder)
        {
            builder.MapGet(""/orders/{id}"", GetOrder);
            builder.MapGet(""/items/{id}"", (int id) => id).WithName(""GetItem"");
            builder.MapGet(""/lines/{id}"", [McpExpose(ToolName = ""get_line"")] (int id) => id);
            builder.MapGet(""/notes/{id}"", (int id) => id);
            return builder;
        }

        private static int GetOrder(int id) => id;");
        const string selectingOne = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Endpoints.V2GetOrder""] }";

        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Empty(GetGeneratorDiagnostics(source, MinimalApisConfig));
        Assert.Equal("var route = $\"/api/v1/admin/orders/{routeid}\";", GetRouteLine(generated, "Endpoints_V1GetOrderTool"));
        Assert.Equal("var route = $\"/api/v2/admin/orders/{routeid}\";", GetRouteLine(generated, "Endpoints_V2GetOrderTool"));
        Assert.Contains("[McpServerTool(Name = \"Endpoints_V1GetItem\"", generated);
        Assert.Contains("[McpServerTool(Name = \"Endpoints_V2GetItem\"", generated);
        Assert.Contains("[McpServerTool(Name = \"V1_get_line\"", generated);
        Assert.Contains("[McpServerTool(Name = \"V2_get_line\"", generated);
        Assert.Contains("[McpServerTool(Name = \"Endpoints_GetApiV1AdminNotesById\"", generated);
        Assert.Contains("[McpServerTool(Name = \"Endpoints_GetApiV2AdminNotesById\"", generated);
        Assert.Equal("Endpoints_V2GetOrderTool", Assert.Single(GetToolClassNames(RunGenerator(source, selectingOne)), x => x.Contains("GetOrder")));
    }

    [Fact]
    public void Endpoint_MappedOnWhatAnAppsOwnMethodReturns_TakesItsGroups()
    {
        // A fluent method that returns its group, and one in ASP.NET Core's namespace that returns a sub-group of it: the
        // generator reads what each returns rather than trusting a method by its namespace.
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app)
        {
            app.MapGroup(""/api"").MapTodos().MapUsers();
            app.MapGroup(""/shop"").MapV1().MapGet(""/orders"", () => 1).WithName(""Orders"");
        }

        public static RouteGroupBuilder MapTodos(this RouteGroupBuilder group)
        {
            group.MapGet(""/todos"", () => 1).WithName(""Todos"");
            return group;
        }

        public static RouteGroupBuilder MapUsers(this RouteGroupBuilder group)
        {
            group.MapGet(""/users"", () => 1).WithName(""Users"");
            return group;
        }") + @"
namespace Microsoft.AspNetCore.Builder
{
    public static class VersionGroups
    {
        public static RouteGroupBuilder MapV1(this RouteGroupBuilder group) => group.MapGroup(""/v1"");
    }
}
";
        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Empty(GetGeneratorDiagnostics(source, MinimalApisConfig));
        Assert.Equal("var route = $\"/api/todos\";", GetRouteLine(generated, "Endpoints_TodosTool"));
        Assert.Equal("var route = $\"/api/users\";", GetRouteLine(generated, "Endpoints_UsersTool"));
        Assert.Equal("var route = $\"/shop/v1/orders\";", GetRouteLine(generated, "Endpoints_OrdersTool"));
    }

    [Fact]
    public void Endpoint_MappedOnWhatALibrarysMethodReturns_IsFollowedOnlyThroughAConventionThatReturnsItsReceiver()
    {
        // A library's code cannot be read: its generic TBuilder convention returns the builder it is given, while a method
        // that returns a RouteGroupBuilder may return a sub-group.
        const string library = MinimalApiUsings + @"
namespace Microsoft.AspNetCore.Builder
{
    public static class LibraryConventions
    {
        public static TBuilder WithAudit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder => builder;

        public static RouteGroupBuilder MapV1(this RouteGroupBuilder group) => group.MapGroup(""/v1"");
    }
}
";
        var source = MinimalApi(@"
            app.MapGroup(""/api"").WithAudit().MapGet(""/audited"", () => 1).WithName(""Audited"");
            app.MapGroup(""/api"").MapV1().MapGet(""/orders"", () => 1).WithName(""Orders"");");
        var compilation = CreateCompilation(source).AddReferences(CompileToReference(library, "Library"));

        var runResult = CreateDriver(MinimalApisConfig).RunGenerators(compilation).GetRunResult();
        var generated = string.Join("\n", runResult.Results.SelectMany(x => x.GeneratedSources).Select(x => x.SourceText.ToString()));
        var warning = Assert.Single(runResult.Diagnostics);

        Assert.Equal("var route = $\"/api/audited\";", GetRouteLine(generated, "Endpoints_AuditedTool"));
        Assert.DoesNotContain("Endpoints_OrdersTool", generated);
        Assert.Equal(UnbuildableEndpointId, warning.Id);
        Assert.EndsWith(
            ": the generator cannot tell which route group 'app.MapGroup(\"/api\").MapV1()' gives, as it follows only MapGroup, ASP.NET Core's conventions and the project's own methods that return a builder",
            warning.GetMessage());
    }

    [Fact]
    public void Endpoint_MappedOnAParameterANullCheckChooses_TakesTheBranchEachCallTakes()
    {
        // The pattern of registering every IEndpoint class: a group when the caller passes one, else the app itself.
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app)
        {
            app.MapEndpoints();
            app.MapEndpoints(app.MapGroup(""/v2""));
            app.MapCoalesced(null);
        }

        public static WebApplication MapEndpoints(this WebApplication app, RouteGroupBuilder? routeGroupBuilder = null)
        {
            IEndpointRouteBuilder builder = routeGroupBuilder is null ? app : routeGroupBuilder;
            builder.MapGet(""/items"", () => 1);
            return app;
        }

        public static WebApplication MapCoalesced(this WebApplication app, RouteGroupBuilder? group)
        {
            var builder = group ?? (IEndpointRouteBuilder)app.MapGroup(""/fallback"");
            builder.MapGet(""/things"", () => 1);
            return app;
        }");

        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Empty(GetGeneratorDiagnostics(source, MinimalApisConfig));
        Assert.Equal("var route = $\"/items\";", GetRouteLine(generated, "Endpoints_GetItemsTool"));
        Assert.Equal("var route = $\"/v2/items\";", GetRouteLine(generated, "Endpoints_GetV2ItemsTool"));
        Assert.Equal("var route = $\"/fallback/things\";", GetRouteLine(generated, "Endpoints_GetFallbackThingsTool"));
    }

    [Fact]
    public void Endpoint_MappedOnAConditionThatIsNoNullCheckOfAParameter_GetsNoToolAndWarning()
    {
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app, bool versioned)
        {
            IEndpointRouteBuilder builder = versioned ? app.MapGroup(""/v1"") : app;
            builder.MapGet(""/items"", () => 1);
        }");

        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == UnbuildableEndpointId);

        Assert.EndsWith(
            ": the generator cannot tell which route group 'versioned ? app.MapGroup(\"/v1\") : app' is, as it decides only a null check of a parameter",
            warning.GetMessage());
    }

    [Fact]
    public void Endpoint_MappedInTheCallbackOfUseEndpoints_IsMappedOnTheRoot()
    {
        var source = MinimalApi(@"app.UseRouting(); app.UseEndpoints(endpoints => { endpoints.MapGet(""/legacy"", () => 1); });");

        Assert.Equal("var route = $\"/legacy\";", GetRouteLine(RunGenerator(source, MinimalApisConfig), "Endpoints_GetLegacyTool"));
    }

    [Theory]
    [InlineData(@"public static void Map(WebApplication app) { var route = ""/x""; app.MapGet(route, () => 1); }", "the generator cannot read its route pattern, as it is not a constant string")]
    [InlineData(@"public static void Map(WebApplication app, string prefix) { app.MapGroup(prefix).MapGet(""/x"", () => 1); }", "the generator cannot read the prefix of the route group 'app.MapGroup(prefix)', as it is not a constant string")]
    [InlineData(@"public static void Map(WebApplication app) { var group = app.MapGroup(""/a""); group = app.MapGroup(""/b""); group.MapGet(""/x"", () => 1); }", "the generator cannot tell which route group 'group' is, as it is assigned again after its declaration")]
    [InlineData(@"public static void MapAll(IEndpointRouteBuilder builder) { builder.MapGet(""/x"", () => 1); }", "the generator finds no call of 'Endpoints.MapAll' in the project to tell which route group 'builder' is")]
    [InlineData(@"public static void MapAll(IEndpointRouteBuilder builder, int depth) { builder.MapGet(""/x"", () => 1); MapAll(builder.MapGroup(""/y""), depth - 1); } public static void Map(WebApplication app) { MapAll(app, 1); }", "'Endpoints.MapAll' calls itself, so the generator cannot tell which route group 'builder' is")]
    [InlineData(@"private static IEndpointRouteBuilder Builder = null!; public static void Map(WebApplication app) { Builder.MapGet(""/x"", () => 1); }", "the generator cannot tell which route group 'Builder' is")]
    [InlineData(@"public static void Map(WebApplication app) { Delegate handler = () => 1; app.MapGet(""/x"", handler); }", "the generator cannot see its handler, which is neither a lambda nor a method")]
    [InlineData(@"public static void Map(WebApplication app) { var methods = new[] { ""GET"" }; app.MapMethods(""/x"", methods, () => 1); }", "the generator cannot read the HTTP methods passed to MapMethods, as they are not a list of constants written in the call")]
    public void Endpoint_TheGeneratorCannotFollow_GetsNoToolAndWarning(string members, string reason)
    {
        var source = MinimalApiClass(members);

        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == UnbuildableEndpointId);
        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.EndsWith($": {reason}", warning.GetMessage());
        Assert.StartsWith("No MCP tool is generated for endpoint 'Endpoints.", warning.GetMessage());
        Assert.StartsWith("Map", GetLocatedText(warning.Location, source));
        Assert.DoesNotContain("[McpServerTool(", generated);
        AssertCompiles(source, MinimalApisConfig);
    }

    [Fact]
    public void Endpoint_WithARouteValueNothingFills_GetsNoToolAndWarning()
    {
        var source = MinimalApi(@"app.MapGet(""/orders/{id}"", (HttpContext context) => context.Request.RouteValues[""id""]);");

        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == UnbuildableEndpointId);

        Assert.Equal(
            "No MCP tool is generated for endpoint 'Endpoints.GetOrdersById': no parameter or property fills the route value '{id}' of GET /orders/{id}",
            warning.GetMessage());
        Assert.Equal("MapGet", GetLocatedText(warning.Location, source));
    }

    [Fact]
    public void EndpointWarnings_ComeOnlyForSelectedEndpoints_AndAreSilencedByMcpIgnore()
    {
        var source = MinimalApi(@"
            var route = ""/x"";
            app.MapGet(route, [McpIgnore] () => 1);
            app.MapGet(route, () => 2).WithName(""NotListed"");");
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Endpoints.Listed""] }";

        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == UnbuildableEndpointId);

        Assert.StartsWith("No MCP tool is generated for endpoint 'Endpoints.NotListed'", warning.GetMessage());
        Assert.DoesNotContain(GetGeneratorDiagnostics(source, config), x => x.Id == UnbuildableEndpointId);
    }

    [Fact]
    public void Endpoint_TakesItsHandler_FromALambdaALocalFunctionOrAMethod()
    {
        const string source = MinimalApiUsings + MinimalApiModels + @"
namespace TestApi
{
    public static class Endpoints
    {
        public static void Map(WebApplication app, OrderHandlers handlers)
        {
            app.MapGet(""/lambda/{id}"", (int id) => id);
            app.MapGet(""/local/{id}"", GetLocal);
            app.MapGet(""/static/{id}"", OrderHandlers.GetStatic);
            app.MapGet(""/instance/{id}"", handlers.GetInstance);

            static int GetLocal(int id) => id;
        }
    }

    public sealed class OrderHandlers
    {
        public static int GetStatic(int id) => id;

        public int GetInstance(int id) => id;
    }
}
";
        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Equal("var route = $\"/lambda/{routeid}\";", GetRouteLine(generated, "Endpoints_GetLambdaByIdTool"));
        Assert.Equal("var route = $\"/local/{routeid}\";", GetRouteLine(generated, "Endpoints_GetLocalTool"));
        // A method group names the tool after its method and the class that declares it.
        Assert.Equal("var route = $\"/static/{routeid}\";", GetRouteLine(generated, "OrderHandlers_GetStaticTool"));
        Assert.Equal("var route = $\"/instance/{routeid}\";", GetRouteLine(generated, "OrderHandlers_GetInstanceTool"));
        AssertCompiles(source, MinimalApisConfig);
    }

    [Fact]
    public void Endpoint_HandledByARequestDelegate_GetsAToolWithoutArguments()
    {
        var source = MinimalApi(@"app.MapGet(""/ping"", async context => await context.Response.WriteAsync(""pong""));");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_GetPingTool");

        Assert.Contains("IMcpHttpInvoker invoker,\n            System.Threading.CancellationToken cancellationToken = default", tool.Replace("\r", ""));
        Assert.DoesNotContain("UseStructuredContent", tool);
    }

    [Fact]
    public void Endpoint_IsNamedByWithName_ThenEndpointName_ThenItsMethod_ThenItsRoute()
    {
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app)
        {
            app.MapGet(""/a"", GetA).WithName(""ListOrders"");
            app.MapGet(""/b"", [EndpointName(""FindOrder"")] () => 1);
            app.MapGet(""/c"", GetA);
            app.MapGet(""/orders/{orderId}/line-items"", (int orderId) => orderId);
            app.MapGet(""/d"", () => 1).WithName(""orders.get-all"");
        }

        private static int GetA() => 1;");

        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Contains("[McpServerTool(Name = \"Endpoints_ListOrders\", Title = \"List orders\",", generated);
        Assert.Contains("[McpServerTool(Name = \"Endpoints_FindOrder\", Title = \"Find order\",", generated);
        Assert.Contains("[McpServerTool(Name = \"Endpoints_GetA\", Title = \"Get A\",", generated);
        Assert.Contains("[McpServerTool(Name = \"Endpoints_GetOrdersByOrderIdLineItems\", Title = \"Get orders by order id line items\",", generated);
        // A name is any string; the tool's class name keeps only what C# allows.
        Assert.Contains("[McpServerTool(Name = \"Endpoints_orders.get-all\",", generated);
        Assert.Contains("public static class Endpoints_orders_get_allTool", generated);
        AssertCompiles(source, MinimalApisConfig);
    }

    [Fact]
    public void Endpoint_FollowsTheToolNameFormat_AndMcpExpose()
    {
        var source = MinimalApi(@"
            app.MapGet(""/a"", () => 1).WithName(""ListOrders"");
            app.MapGet(""/b"", [McpExpose(ToolName = ""find_order"", Title = ""Find an order"", Description = ""Finds it."")] () => 1).WithDescription(""Ignored."");");
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""minimalApis"": true, ""naming"": { ""toolNameFormat"": ""{Action}_via_{Controller}"" } }";

        var generated = RunGenerator(source, config);

        Assert.Contains("[McpServerTool(Name = \"ListOrders_via_Endpoints\"", generated);
        Assert.Contains("[McpServerTool(Name = \"find_order\", Title = \"Find an order\"", generated);
        Assert.Contains("[Description(\"Finds it.\")]", generated);
    }

    [Theory]
    [InlineData(@"[""Endpoints""]", true, true)]
    [InlineData(@"[""Endpoints.ListOrders""]", true, false)]
    [InlineData(@"[""Endpoints.GetApiItems""]", false, true)]
    [InlineData(@"[]", false, false)]
    public void Endpoint_IsSelected_ByTheClassThatDeclaresItsHandler_OrByItsName(string include, bool hasListOrders, bool hasGetItems)
    {
        var source = MinimalApi(@"
            app.MapGet(""/api/orders"", () => 1).WithName(""ListOrders"");
            app.MapGet(""/api/items"", () => 1);");
        var config = $@"{{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": {include} }}";

        var generated = RunGenerator(source, config);

        Assert.Equal(hasListOrders, generated.Contains("class Endpoints_ListOrdersTool"));
        Assert.Equal(hasGetItems, generated.Contains("class Endpoints_GetApiItemsTool"));
    }

    [Fact]
    public void AllExceptExcluded_WithoutTheOptIn_SelectsNoEndpoint_AndWarnsAboutNone()
    {
        // An app that updates the package must not find endpoints exposed that were never tools, such as an admin reset.
        var source = MinimalApi(@"
            app.MapPost(""/admin/reset"", () => 1);
            var route = ""/x"";
            app.MapGet(route, () => 2);") + ControllerNextToEndpoints;

        var generated = RunGenerator(source, AllExceptExcludedConfig);
        var diagnostics = GetGeneratorDiagnostics(source, AllExceptExcludedConfig);

        Assert.Contains("class ProductsController_GetAllTool", generated);
        Assert.DoesNotContain("class Endpoints_", generated);
        var info = Assert.Single(diagnostics);
        Assert.Equal(EndpointsLeftOutId, info.Id);
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Equal(
            "AllExceptExcluded mode selects none of the project's 2 Minimal API endpoints, as generator.json does not set \"minimalApis\": set it to true to select them, or to false to keep them out without this message",
            info.GetMessage());
    }

    [Fact]
    public void AllExceptExcluded_WithTheOptIn_SelectsEveryEndpoint_AndWarnsAboutThoseItCannotFollow()
    {
        var source = MinimalApi(@"
            app.MapPost(""/admin/reset"", () => 1);
            var route = ""/x"";
            app.MapGet(route, () => 2);") + ControllerNextToEndpoints;

        var generated = RunGenerator(source, MinimalApisConfig);
        var diagnostics = GetGeneratorDiagnostics(source, MinimalApisConfig);

        Assert.Contains("class ProductsController_GetAllTool", generated);
        Assert.Contains("class Endpoints_PostAdminResetTool", generated);
        Assert.Equal(UnbuildableEndpointId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void AllExceptExcluded_WithMinimalApisFalse_SelectsNoEndpoint_AndSaysNothing()
    {
        // No MCP006 either: the project has endpoints, but none it chose to give a tool.
        var source = MinimalApi(@"app.MapPost(""/admin/reset"", () => 1);");
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""minimalApis"": false }";

        Assert.DoesNotContain("[McpServerTool(", RunGenerator(source, config));
        Assert.Empty(GetGeneratorDiagnostics(source, config));
    }

    [Theory]
    [InlineData("AllExceptExcluded")]
    [InlineData("SelectedOnly")]
    public void Endpoint_IsSelected_ByAnIncludeEntryOrMcpExpose_WithoutTheOptIn(string mode)
    {
        var source = MinimalApi(@"
            app.MapGet(""/listed"", () => 1).WithName(""Listed"");
            app.MapGet(""/exposed"", [McpExpose] () => 1).WithName(""Exposed"");
            app.MapGet(""/plain"", () => 1).WithName(""Plain"");
            var route = ""/x"";
            app.MapGet(route, () => 1).WithName(""Unbuildable"");") + ControllerNextToEndpoints;
        var config = $@"{{ ""schemaVersion"": 1, ""mode"": ""{mode}"", ""include"": [""Endpoints.Listed"", ""Endpoints.Unbuildable""] }}";

        var generated = RunGenerator(source, config);
        var diagnostics = GetGeneratorDiagnostics(source, config);

        Assert.Contains("class Endpoints_ListedTool", generated);
        Assert.Contains("class Endpoints_ExposedTool", generated);
        Assert.DoesNotContain("class Endpoints_PlainTool", generated);
        // An endpoint the project chose still gets the warning when it cannot become a tool.
        var warning = Assert.Single(diagnostics, x => x.Id == UnbuildableEndpointId);
        Assert.StartsWith("No MCP tool is generated for endpoint 'Endpoints.Unbuildable'", warning.GetMessage());
    }

    [Fact]
    public void Endpoint_ThatAnIncludeAndAnExcludeEntryBothName_IsLeftOut_InAllExceptExcludedMode()
    {
        var source = MinimalApi(@"app.MapGet(""/listed"", () => 1).WithName(""Listed"");");
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""include"": [""Endpoints""], ""exclude"": [""Endpoints.Listed""] }";

        Assert.DoesNotContain("[McpServerTool(", RunGenerator(source, config));
    }

    [Fact]
    public void IncludeEntry_ThatNamesAController_SelectsNoEndpointOfAClassOfTheSameName()
    {
        // "Orders" selected OrdersController before endpoints became tools; it must not now expose an admin endpoint
        // whose handler a class Orders declares.
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app)
        {
            app.MapPost(""/admin/reset"", Orders.Reset);
            app.MapGet(""/orders/export"", Orders.Export);
        }") + @"
namespace TestApi
{
    public static class Orders
    {
        public static int Reset() => 1;

        public static int Export() => 1;
    }

    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet]
        public int GetAll() => 1;
    }
}
";
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Orders""] }";
        const string namingAnEndpoint = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Orders"", ""Orders.Export""] }";

        var info = Assert.Single(GetGeneratorDiagnostics(source, config));

        Assert.Equal("OrdersController_GetAllTool", Assert.Single(GetToolClassNames(RunGenerator(source, config))));
        Assert.Equal("MCP013", info.Id);
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Equal(
            "The include entry 'Orders' in generator.json names a controller and the class 'Orders', which declares Minimal API endpoints; it selects only the controller's actions: to select an endpoint as well, name it by an entry no controller has, such as 'Orders.Reset', or mark it [McpExpose]",
            info.GetMessage());
        Assert.Equal(new[] { "OrdersController_GetAllTool", "Orders_ExportTool" }, GetToolClassNames(RunGenerator(source, namingAnEndpoint)));
    }

    [Fact]
    public void ExcludeEntry_NamingAnEndpointByItsRoute_MatchesIt_WithoutTheOptIn()
    {
        // Without the opt-in, the generator analyzes only the endpoints an entry or [McpExpose] concerns; one an entry
        // names is analyzed, so it keeps the name its route gives it and the entry does not name nothing.
        var source = MinimalApi(@"var api = app.MapGroup(""/api""); api.MapGet(""/items"", () => 1);");
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""AllExceptExcluded"", ""exclude"": [""Endpoints.GetApiItems""] }";

        Assert.Empty(GetGeneratorDiagnostics(source, config));
        Assert.DoesNotContain("[McpServerTool(", RunGenerator(source, config));
    }

    [Fact]
    public void ProjectOfEndpoints_ThatSelectsNone_GetsNoWarning_UntilItOptsIn()
    {
        // Before endpoints became tools, such a project got no MCP006; an update must not start failing its build.
        var source = MinimalApi(@"app.MapGet(""/api/orders"", () => 1);");

        Assert.Empty(GetGeneratorDiagnostics(source, @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [] }"));
        Assert.Empty(GetGeneratorDiagnostics(source, generatorJson: null));
    }

    [Fact]
    public void Endpoint_IsExposedOrIgnored_ByMcpExposeAndMcpIgnore_OnItsHandlerOrClass()
    {
        const string source = MinimalApiUsings + @"
namespace TestApi
{
    public static class Endpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapGet(""/exposed"", [McpExpose] () => 1);
            app.MapGet(""/plain"", () => 1);
            app.MapGet(""/exposed-method"", Exposed);
            app.MapGet(""/admin"", AdminHandlers.Get);
            app.MapGet(""/ignored-method"", Ignored);
        }

        [McpExpose]
        private static int Exposed() => 1;

        [McpIgnore]
        private static int Ignored() => 1;
    }

    [McpExpose]
    public static class AdminHandlers
    {
        public static int Get() => 1;
    }
}
";
        const string selectedOnly = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Endpoints.Ignored""] }";

        var generated = RunGenerator(source, selectedOnly);

        Assert.Contains("class Endpoints_GetExposedTool", generated);
        Assert.Contains("class Endpoints_ExposedTool", generated);
        Assert.Contains("class AdminHandlers_GetTool", generated);
        Assert.DoesNotContain("class Endpoints_GetPlainTool", generated);
        Assert.DoesNotContain("class Endpoints_IgnoredTool", generated);
        Assert.DoesNotContain("class Endpoints_IgnoredTool", RunGenerator(source, MinimalApisConfig));
    }

    [Fact]
    public void ConfigEntry_ThatMisspellsAnEndpoint_GetsMcp008WithTheNearestName()
    {
        var source = MinimalApi(@"app.MapGet(""/api/orders"", () => 1).WithName(""ListOrders"");");
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""Endpoints.ListOrder""] }";

        var warning = Assert.Single(GetGeneratorDiagnostics(source, config), x => x.Id == "MCP008");

        Assert.Contains("the nearest name in the project is 'Endpoints.ListOrders'", warning.GetMessage());
    }

    [Fact]
    public void NoTools_FromAProjectOfEndpoints_SuggestsItsClassesAndEndpoints()
    {
        var source = MinimalApi(@"app.MapGet(""/api/orders"", () => 1).WithName(""ListOrders"");");
        const string config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [], ""minimalApis"": true }";

        var warning = Assert.Single(GetGeneratorDiagnostics(source, config), x => x.Id == "MCP006");

        Assert.Contains("classes such as 'Endpoints' or endpoints such as 'Endpoints.ListOrders'", warning.GetMessage());
    }

    [Fact]
    public void NoTools_WhenEverySelectedEndpointGetsMcp010_PointsAtMcp010()
    {
        var source = MinimalApi(@"var route = ""/x""; app.MapGet(route, () => 1);");

        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == "MCP006");

        Assert.Equal(
            "No MCP tools are generated: AllExceptExcluded mode selects only Minimal API endpoints the generator cannot give a tool. MCP010 says at each of them why and what to change, or select other endpoints or actions: in generator.json, or with [McpExpose].",
            warning.GetMessage());
    }

    [Fact]
    public void Endpoint_IsDescribed_ByItsMetadata_ThenItsXmlDocs()
    {
        const string source = MinimalApiUsings + @"
namespace TestApi
{
    public static class Endpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapGet(""/a"", [EndpointDescription(""Attribute description."")] [EndpointSummary(""Attribute summary."")] () => 1)
                .WithSummary(""Summary."").WithDescription(""Description."").WithName(""A"");
            app.MapGet(""/b"", [EndpointDescription(""Attribute description."")] () => 1).WithSummary(""Summary."").WithName(""B"");
            app.MapGet(""/c"", [Description(""Lambda description."")] () => 1).WithSummary(""Summary."").WithName(""C"");
            app.MapGet(""/d"", [Description(""Lambda description."")] () => 1).WithName(""D"");
            app.MapGet(""/e/{id}"", Documented).WithName(""E"");
            app.MapGet(""/f"", ([Description(""How many to take."")] int take) => take).WithName(""F"");
        }

        /// <summary>Gets a documented thing.</summary>
        /// <param name=""id"">Its id.</param>
        private static int Documented(int id) => id;
    }
}
";
        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Contains("[Description(\"Description.\")]", GetToolClass(generated, "Endpoints_ATool"));
        Assert.Contains("[Description(\"Attribute description.\")]", GetToolClass(generated, "Endpoints_BTool"));
        Assert.Contains("[Description(\"Summary.\")]", GetToolClass(generated, "Endpoints_CTool"));
        Assert.Contains("[Description(\"Lambda description.\")]", GetToolClass(generated, "Endpoints_DTool"));
        Assert.Contains("[Description(\"Gets a documented thing.\")]", GetToolClass(generated, "Endpoints_ETool"));
        Assert.Contains("[Description(\"Its id.\")] int id", GetToolClass(generated, "Endpoints_ETool"));
        Assert.Contains("[Description(\"How many to take.\")] int take", GetToolClass(generated, "Endpoints_FTool"));
    }

    [Fact]
    public void Endpoint_TakesItsScopeAndHints_LikeAnAction()
    {
        const string source = MinimalApiUsings + @"
namespace TestApi
{
    public static class Endpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapPost(""/search"", [McpExpose(Scope = McpScope.Read)] () => 1).WithName(""Search"");
            app.MapPost(""/reset"", [McpExpose(Destructive = true)] () => 1).WithName(""Reset"");
            app.MapDelete(""/items"", () => 1).WithName(""Clear"");
            app.MapGet(""/admin"", AdminHandlers.Get);
        }
    }

    [McpExpose(Scope = McpScope.Write)]
    public static class AdminHandlers
    {
        public static int Get() => 1;
    }
}
";
        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);", GetScopeCheck(generated, "Endpoints_SearchTool"));
        Assert.Contains("ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false", GetToolAttributeLine(generated, "Endpoints_SearchTool"));
        Assert.Contains("ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false", GetToolAttributeLine(generated, "Endpoints_ResetTool"));
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Delete, cancellationToken);", GetScopeCheck(generated, "Endpoints_ClearTool"));
        Assert.Contains("ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false", GetToolAttributeLine(generated, "Endpoints_ClearTool"));
        // A class's [McpExpose(Scope)] applies to the endpoints whose handlers it declares.
        Assert.Equal("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", GetScopeCheck(generated, "AdminHandlers_GetTool"));
    }

    [Fact]
    public void MapMethods_WithOneHttpMethod_GetsATool_AndWithSeveral_WarnsAboutTheOthers()
    {
        var source = MinimalApi(@"
            app.MapMethods(""/one"", new[] { ""patch"" }, () => 1).WithName(""One"");
            app.MapMethods(""/head-get"", new[] { HttpMethods.Head, HttpMethods.Get }, () => 1).WithName(""HeadGet"");
            app.MapMethods(""/several"", [""GET"", ""POST"", ""DELETE""], () => 1).WithName(""Several"");
            app.MapMethods(""/head"", new List<string> { ""HEAD"" }, () => 1).WithName(""Head"");");

        var generated = RunGenerator(source, MinimalApisConfig);
        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig));

        Assert.Contains("await invoker.PatchAsync(route, null, cancellationToken)", GetToolClass(generated, "Endpoints_OneTool"));
        Assert.Contains("await invoker.GetAsync(route, cancellationToken)", GetToolClass(generated, "Endpoints_HeadGetTool"));
        Assert.Contains("await invoker.GetAsync(route, cancellationToken)", GetToolClass(generated, "Endpoints_SeveralTool"));
        // A tool sends no HEAD, so an endpoint mapped for it alone gets no tool, as an action does.
        Assert.DoesNotContain("Endpoints_HeadTool", generated);
        Assert.Equal(SeveralHttpMethodsId, warning.Id);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("The MCP tool of endpoint 'Endpoints.Several' calls it with GET only, though it is mapped for POST, DELETE as well", warning.GetMessage());
        Assert.Equal("MapMethods", GetLocatedText(warning.Location, source));
        AssertCompiles(source, MinimalApisConfig);
    }

    [Fact]
    public void Endpoint_TakingStringValues_GetsAToolThatTakesAnArrayOfStrings()
    {
        // System.Text.Json cannot fill a StringValues, so a tool taking one could never be called.
        var source = MinimalApi(@"
            app.MapGet(""/tags"", (Microsoft.Extensions.Primitives.StringValues tags, Microsoft.Extensions.Primitives.StringValues? more) => 1).WithName(""Tags"");");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_TagsTool");

        Assert.Contains("[Description(\"Parameter: tags\")] string[] tags,", tool);
        Assert.Contains("[Description(\"Parameter: more\")] string[]? more = null,", tool);
        Assert.Contains("AddQueryValue(queryParts, \"tags\", tags);", tool);
        Assert.Contains("AddQueryValue(queryParts, \"more\", more);", tool);
    }

    [Fact]
    public void Endpoint_OfNullableObliviousCode_TakesAReferenceParameterForOptional()
    {
        // ASP.NET Core takes a parameter whose nullability is not known to be not-null for optional, so the endpoint
        // does without the header, and the tool without the name.
        var source = MinimalApiClass(@"
#nullable disable
        public static void Map(WebApplication app)
        {
            app.MapGet(""/x"", ([FromHeader(Name = ""X-Tenant"")] string tenant, string name) => 1).WithName(""X"");
        }
#nullable enable");

        CreateDriver(MinimalApisConfig).RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out var output, out var generatorDiagnostics);
        var tool = GetToolClass(RunGenerator(source, MinimalApisConfig), "Endpoints_XTool");
        var generatedWarnings = output.GetDiagnostics()
            .Where(x => x.Severity >= DiagnosticSeverity.Warning && x.Location.SourceTree is not null && x.Location.SourceTree != output.SyntaxTrees.First())
            .ToList();

        Assert.Empty(generatorDiagnostics);
        Assert.Contains("[Description(\"Parameter: name\")] string? name = null,", tool);
        Assert.DoesNotContain("tenant", tool);
        Assert.False(generatedWarnings.Any(), string.Join(Environment.NewLine, generatedWarnings));
    }

    [Fact]
    public void MapMethods_WithLowerCaseHttpMethods_InfersABody_AsAspNetCoreDoes()
    {
        // ASP.NET Core compares the methods with "GET" and the others case and all to decide that it infers no body, so
        // an endpoint mapped for "get" still reads Order from the body.
        var source = MinimalApi(@"
            app.MapMethods(""/y"", new[] { ""post"", ""get"" }, (Order order) => 1).WithName(""Y"");
            app.MapMethods(""/z"", new[] { ""POST"", ""GET"" }, (Order order) => 1).WithName(""Z"");");

        var generated = RunGenerator(source, MinimalApisConfig);

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(order);", GetToolClass(generated, "Endpoints_YTool"));
        Assert.Contains("await invoker.PostAsync(route, bodyJson, cancellationToken)", GetToolClass(generated, "Endpoints_YTool"));
        Assert.Contains("await invoker.PostAsync(route, null, cancellationToken)", GetToolClass(generated, "Endpoints_ZTool"));
        AssertCompiles(source, MinimalApisConfig);
    }

    [Theory]
    [InlineData(@"(int id, string? name, OrderStatus status = OrderStatus.Open) => 1", "GET", @"var route = $""/orders/{routeid}"";|AddQueryValue(queryParts, ""name"", name);|AddQueryValue(queryParts, ""status"", status == global::TestApi.OrderStatus.Open ? null : status);")]
    [InlineData(@"([FromQuery(Name = ""q"")] string query, [FromRoute] int id) => 1", "GET", @"AddQueryValue(queryParts, ""q"", query);|var route = $""/orders/{routeid}"";")]
    [InlineData(@"(int id, int[] tags) => 1", "GET", @"AddQueryValue(queryParts, ""tags"", tags);")]
    [InlineData(@"(int id, Order order) => 1", "POST", @"var bodyJson = JsonSerializer.Serialize(order);")]
    [InlineData(@"(int id, int[] tags) => 1", "POST", @"var bodyJson = JsonSerializer.Serialize(tags);")]
    [InlineData(@"(int id, [FromBody] Order order, OrderService service, IEnumerable<Order> all) => 1", "POST", @"var bodyJson = JsonSerializer.Serialize(order);")]
    [InlineData(@"(int id, Order order, IOrderRepository repository, ShopContext db, Func<int> clock) => 1", "PUT", @"var bodyJson = JsonSerializer.Serialize(order);")]
    [InlineData(@"(int id, IList<Order> orders) => 1", "PATCH", @"var bodyJson = JsonSerializer.Serialize(orders);")]
    [InlineData(@"(int id, Microsoft.AspNetCore.Identity.Data.LoginRequest login) => 1", "POST", @"var bodyJson = JsonSerializer.Serialize(login);")]
    [InlineData(@"(int id, ProblemDetails problem, System.Net.Http.HttpClient http) => 1", "POST", @"var bodyJson = JsonSerializer.Serialize(problem);")]
    [InlineData(@"(int id, Order order, Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser> users) => 1", "PUT", @"var bodyJson = JsonSerializer.Serialize(order);")]
    public void EndpointParameter_IsSent_WhereTheMinimalApiBindsIt(string handler, string httpMethod, string expectedLines)
    {
        var mapMethod = "Map" + httpMethod.Substring(0, 1) + httpMethod.Substring(1).ToLowerInvariant();
        var source = MinimalApiClass($@"
        public static void Map(WebApplication app)
        {{
            app.{mapMethod}(""/orders/{{id}}"", {handler}).WithName(""Tool"");
        }}") + @"
namespace TestApi
{
    public enum OrderStatus { Open = 1, Closed = 2 }
    public interface IOrderRepository { }
    public class ShopContext : Microsoft.EntityFrameworkCore.DbContext { }
}

namespace Microsoft.EntityFrameworkCore
{
    public abstract class DbContextBase { }
    public class DbContext : DbContextBase { }
}
";
        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_ToolTool");

        foreach (var line in expectedLines.Split('|'))
        {
            Assert.Contains(line, tool);
        }
    }

    [Theory]
    [InlineData(@"(int id, OrderService service, IOrderRepository repository, HttpContext context, HttpRequest request, CancellationToken ct, System.Security.Claims.ClaimsPrincipal user, [FromHeader] string? tenant) => 1", "GET")]
    [InlineData(@"(int id, Order filter) => 1", "GET")]
    [InlineData(@"(int id, Order filter) => 1", "DELETE")]
    [InlineData(@"(int id, [FromServices] Order order, [Microsoft.Extensions.DependencyInjection.FromKeyedServices(""k"")] Order keyed) => 1", "POST")]
    public void EndpointParameter_ThatTheRequestDoesNotCarry_IsNoArgument(string handler, string httpMethod)
    {
        // On GET and DELETE a Minimal API infers no body, so a complex parameter without an attribute is a service.
        var mapMethod = "Map" + httpMethod.Substring(0, 1) + httpMethod.Substring(1).ToLowerInvariant();
        var source = MinimalApiClass($@"
        public static void Map(WebApplication app)
        {{
            app.{mapMethod}(""/orders/{{id}}"", {handler}).WithName(""Tool"");
        }}") + @"
namespace TestApi
{
    public interface IOrderRepository { }
}
";
        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_ToolTool");

        Assert.Contains("IMcpHttpInvoker invoker,\n            [Description(\"Parameter: id\")] int id,\n            System.Threading.CancellationToken cancellationToken = default", tool.Replace("\r", ""));
        Assert.DoesNotContain("bodyJson", tool);
    }

    [Theory]
    [InlineData(@"([FromForm] string name) => 1", "it reads 'name' from a form, which a tool does not send")]
    [InlineData(@"(IFormFile file) => 1", "it reads 'file' from a form, which a tool does not send")]
    [InlineData(@"([FromHeader(Name = ""X-Tenant"")] string tenant) => 1", "it reads 'tenant' from a header, which a tool does not send")]
    [InlineData(@"(Stream body) => 1", "it reads the request body of 'body' as a stream, whose format the generator cannot know")]
    [InlineData(@"(Paging paging) => 1", "it binds 'paging' through TestApi.Paging.BindAsync, which reads the request in a way the generator cannot know")]
    [InlineData(@"(Order order, OrderService service) => 1", "the generator cannot tell which of 'order' and 'service' it reads from the body and which are services: mark the services [FromServices], or the body [FromBody]")]
    [InlineData(@"([FromBody] Order a, [FromBody] Order b) => 1", "it reads 'a' and 'b' from the body, of which a request has one")]
    [InlineData(@"([AsParameters] Lookup lookup, int id) => 1", "two of its inputs are named 'Id', which the endpoint reads from the same value and a tool cannot tell apart")]
    [InlineData(@"(Secret secret) => 1", "its input 'secret' is of type TestApi.Endpoints.Secret, which the generated tool cannot refer to")]
    public void Endpoint_ReadingWhatAToolCannotSend_GetsNoToolAndWarning(string handler, string reason)
    {
        var source = MinimalApiClass($@"
        public static void Map(WebApplication app)
        {{
            app.MapPost(""/orders/{{id}}"", {handler});
        }}

        private sealed class Secret
        {{
        }}") + @"
namespace TestApi
{
    public sealed class Paging
    {
        public static ValueTask<Paging?> BindAsync(HttpContext context) => ValueTask.FromResult<Paging?>(new Paging());
    }

    public record Lookup(int Id, string? Note);
}
";
        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == UnbuildableEndpointId);

        Assert.EndsWith($": {reason}", warning.GetMessage());
        Assert.DoesNotContain("[McpServerTool(", RunGenerator(source, MinimalApisConfig));
    }

    [Fact]
    public void Endpoint_WithAnInternalParameterType_GetsAnInternalToolThatCompiles()
    {
        // A public tool method cannot take an internal type (CS0051), which a handler often does.
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app)
        {
            app.MapPost(""/todos"", (CreateTodo todo) => 1).WithName(""Create"");
            app.MapGet(""/todos"", (TodoStatus status, TodoStatus[] others) => 1).WithName(""List"");
            app.MapPost(""/lines"", (List<Line> lines) => 1).WithName(""Lines"");
            app.MapGet(""/public/{id}"", (int id) => 1).WithName(""Public"");
        }") + @"
namespace TestApi
{
    internal sealed record CreateTodo(string Title);
    internal enum TodoStatus { Open, Done }
    internal sealed class Line { public int Id { get; set; } }
}
";
        var generated = RunGenerator(source, MinimalApisConfig);

        AssertCompiles(source, MinimalApisConfig);
        Assert.Contains("internal static class Endpoints_CreateTool", generated);
        Assert.Contains("internal static class Endpoints_ListTool", generated);
        Assert.Contains("internal static class Endpoints_LinesTool", generated);
        Assert.Contains("public static class Endpoints_PublicTool", generated);
    }

    [Fact]
    public void Endpoint_OfATopLevelProgram_TakingARecordDeclaredThere_GetsAToolThatCompiles()
    {
        // A record declared without a modifier at the end of Program.cs is internal.
        const string program = MinimalApiUsings + @"
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var api = app.MapGroup(""/api"");
api.MapGet(""/todos/{id}"", (int id) => id);
api.MapPost(""/todos"", (Todo todo) => todo);
app.Run();

record Todo(int Id, string Title);
";
        var compilation = CreateCompilation(program).WithOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        CreateDriver(MinimalApisConfig).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        var generated = string.Join("\n", output.SyntaxTrees.Skip(1).Select(x => x.ToString()));
        var errors = output.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToList();

        Assert.Empty(generatorDiagnostics);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));
        Assert.Contains("internal static class Program_PostApiTodosTool", generated);
        Assert.Contains("public static class Program_GetApiTodosByIdTool", generated);
    }

    [Fact]
    public void Endpoint_OfTheJsonPatchDocsSample_SendsThePatchDocumentAsTheBody()
    {
        // https://learn.microsoft.com/en-us/aspnet/core/web-api/jsonpatch?view=aspnetcore-10.0: a class of the framework
        // is the body, not a service, and the context next to it is one.
        var source = MinimalApiClass(@"
        public static void Map(WebApplication app)
        {
            var group = app.MapGroup(""/customers"");
            group.MapPatch(""/{id}"", (AppDb db, string id, JsonPatchDocument<Order> patchDoc) => 1).WithName(""PatchCustomer"");
        }").Replace("using Api.ToMcp.Abstractions;", "using Api.ToMcp.Abstractions;\nusing Microsoft.AspNetCore.JsonPatch.SystemTextJson;") + @"
namespace TestApi
{
    public class AppDb : Microsoft.EntityFrameworkCore.DbContext { }
}

namespace Microsoft.EntityFrameworkCore
{
    public class DbContext { }
}

namespace Microsoft.AspNetCore.JsonPatch.SystemTextJson
{
    public class JsonPatchDocument<TModel> where TModel : class
    {
        public List<object> Operations { get; } = new();
    }
}
";
        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_PatchCustomerTool").Replace("\r", "");

        Assert.Contains("[Description(\"Parameter: id\")] string id,\n            [Description(\"Parameter: patchDoc\")] Microsoft.AspNetCore.JsonPatch.SystemTextJson.JsonPatchDocument<TestApi.Order> patchDoc,", tool);
        Assert.Contains("var bodyJson = JsonSerializer.Serialize(patchDoc);", tool);
        Assert.Contains("await invoker.PatchAsync(route, bodyJson, cancellationToken)", tool);
    }

    [Fact]
    public void Endpoint_ThatReadsABodyOnAGet_GetsNoToolAndWarning()
    {
        var source = MinimalApi(@"app.MapGet(""/orders"", ([FromBody] Order filter) => 1);");

        var warning = Assert.Single(GetGeneratorDiagnostics(source, MinimalApisConfig), x => x.Id == UnbuildableEndpointId);

        Assert.EndsWith(": it reads 'filter' from the body, which a tool sends only with POST, PUT and PATCH", warning.GetMessage());
    }

    [Fact]
    public void AsParameters_SendsEachMember_AsAParameterOfItsOwn()
    {
        const string source = MinimalApiUsings + @"
namespace TestApi
{
    public static class Endpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapGet(""/orders/{id}"", ([AsParameters] OrderQuery query) => 1).WithName(""Record"");
            app.MapGet(""/search"", ([AsParameters] SearchFilter filter, CancellationToken ct) => 1).WithName(""Class"");
        }
    }

    /// <summary>Which order to read.</summary>
    /// <param name=""Id"">The order's id.</param>
    /// <param name=""Page"">The page of its lines.</param>
    public record OrderQuery(int Id, [FromQuery(Name = ""p"")] int Page = 1, [FromServices] IServiceProvider Services = null!);

    public sealed class SearchFilter
    {
        /// <summary>City to search in.</summary>
        public string? City { get; set; }

        [Description(""Most results to return."")]
        public int Take { get; set; }

        public int Ignored { get; }
    }
}
";
        var generated = RunGenerator(source, MinimalApisConfig);
        var record = GetToolClass(generated, "Endpoints_RecordTool");
        var filter = GetToolClass(generated, "Endpoints_ClassTool");

        Assert.Contains("[Description(\"The order's id.\")] int Id,", record);
        Assert.Contains("[Description(\"The page of its lines.\")] int? Page = 1,", record);
        Assert.Contains("var route = $\"/orders/{routeId}\";", record);
        Assert.Contains("AddQueryValue(queryParts, \"p\", Page == 1 ? null : Page);", record);
        Assert.DoesNotContain("IServiceProvider", record);
        Assert.Contains("[Description(\"Most results to return.\")] int Take,", filter);
        Assert.Contains("[Description(\"City to search in.\")] string? City = null,", filter);
        Assert.Contains("AddQueryValue(queryParts, \"City\", City);", filter);
        Assert.DoesNotContain("Ignored", filter);
        AssertCompiles(source, MinimalApisConfig);
    }

    [Theory]
    [InlineData("() => new Order()", "global::TestApi.Order", "true")]
    [InlineData("async () => { await Task.Yield(); return new Order(); }", "global::TestApi.Order", "true")]
    [InlineData("() => 5", "int", "false")]
    [InlineData("() => TypedResults.Ok(new Order())", "global::TestApi.Order", "true")]
    [InlineData("() => TypedResults.Ok(5)", "int", "false")]
    [InlineData("() => TypedResults.Created(\"/orders/1\", new Order())", "global::TestApi.Order", "true")]
    [InlineData("Task<Results<Ok<Order>, NotFound>> (int? id) => Task.FromResult<Results<Ok<Order>, NotFound>>(TypedResults.NotFound())", "global::TestApi.Order", "true")]
    [InlineData("Results<Ok<int>, NotFound, BadRequest<string>> () => TypedResults.Ok(5)", "int", "false")]
    [InlineData("Results<Ok<int>, NoContent> () => TypedResults.NoContent()", "int", "true")]
    [InlineData("[ProducesResponseType<Order>(200)] () => Results.Ok(new Order())", "global::TestApi.Order", "true")]
    public void Endpoint_AdvertisesItsSuccessType_AsAnOutputSchema(string handler, string outputType, string isOutputNullable)
    {
        var source = MinimalApi($@"app.MapGet(""/orders"", {handler}).WithName(""Get"");");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_GetTool");

        Assert.Contains($"UseStructuredContent = true, OutputSchemaType = typeof({outputType}),", tool);
        Assert.Contains($"McpGeneratedTool(IsOutputNullable = {isOutputNullable})", tool);
    }

    [Theory]
    [InlineData("() => \"text\"")]
    [InlineData("() => Results.Ok(new Order())")]
    [InlineData("() => new { Id = 1 }")]
    [InlineData("() => TypedResults.Json(new Order())")]
    [InlineData("Results<Ok<Order>, JsonHttpResult<Order>> () => TypedResults.Ok(new Order())")]
    [InlineData("Results<Ok<Order>, Ok<int>> () => TypedResults.Ok(new Order())")]
    [InlineData("() => { }")]
    public void Endpoint_WithoutOneKnownJsonSuccessType_HasNoOutputSchema(string handler)
    {
        var source = MinimalApi($@"app.MapGet(""/orders"", {handler}).WithName(""Get"");");

        var tool = GenerateCompilingToolClass(source, MinimalApisConfig, "Endpoints_GetTool");

        Assert.DoesNotContain("UseStructuredContent", tool);
        Assert.Contains("public static async Task<CallToolResult> InvokeAsync(", tool);
    }

    [Theory]
    [InlineData(@"app.MapGet(""/mcp"", [McpExpose] () => 1);")]
    [InlineData(@"app.MapGet(""/mcp/status"", [McpExpose] () => 1);")]
    [InlineData(@"app.MapGet(""MCP/status"", [McpExpose] () => 1);")]
    [InlineData(@"app.MapGroup(""/Mcp"").MapGet(""/status"", [McpExpose] () => 1);")]
    [InlineData(@"app.MapGet(""~/mcp/status"", [McpExpose] () => 1);")]
    [InlineData(@"app.MapGroup(""~/mcp"").MapGet(""/status"", [McpExpose] () => 1);")]
    [InlineData(@"app.MapGroup(""~/Mcp/"").MapGet(""status"", [McpExpose] () => 1);")]
    public void Endpoint_AtTheMcpRouteOrUnderIt_IsNeverExposed(string statement)
    {
        var source = MinimalApi(statement);

        Assert.DoesNotContain("[McpServerTool(", RunGenerator(source, MinimalApisConfig));
    }

    [Theory]
    [InlineData(@"app.MapGet(""/api/mcp-status"", [McpExpose] () => 1);", "/api/mcp-status")]
    [InlineData(@"app.MapGet(""/api/mcp"", [McpExpose] () => 1);", "/api/mcp")]
    [InlineData(@"app.MapGet(""/mcpservers"", [McpExpose] () => 1);", "/mcpservers")]
    [InlineData(@"app.MapGroup(""/api"").MapGet(""/mcp"", [McpExpose] () => 1);", "/api/mcp")]
    [InlineData(@"app.MapGroup(""/{tenant}"").MapGet(""/mcp"", [McpExpose] (string tenant) => tenant);", "/{routetenant}/mcp")]
    public void Endpoint_WhoseRouteOnlyHoldsMcp_GetsItsTool(string statement, string route)
    {
        var generated = RunGenerator(MinimalApi(statement), MinimalApisConfig);

        Assert.Contains("[McpServerTool(", generated);
        Assert.Contains($"var route = $\"{route}\";", generated);
    }

    [Theory]
    [InlineData(@"app.MapGet(""~/api/status"", [McpExpose] () => 1);", "/api/status")]
    [InlineData(@"app.MapGroup(""~/api"").MapGet(""/status"", [McpExpose] () => 1);", "/api/status")]
    [InlineData(@"app.MapGroup(""~/api/"").MapGet(""~/status"", [McpExpose] () => 1);", "/api/status")]
    public void Endpoint_WithAnAppRelativePatternOrGroup_IsCalledWhereTheAppServesIt(string statement, string route)
    {
        // RoutePatternParser drops a leading "~/" from each pattern; "/~/api/status" answers 404.
        var generated = RunGenerator(MinimalApi(statement), MinimalApisConfig);

        Assert.Contains($"var route = $\"{route}\";", generated);
    }

    [Fact]
    public void AllExceptExcluded_WithoutTheOptIn_CountsAnEndpointWhoseRouteOnlyHoldsMcp_ButNoneUnderMcp()
    {
        var source = MinimalApi(@"
            app.MapGet(""/mcp/health"", () => 1);
            app.MapGet(""/api/mcp-status"", () => 2);") + ControllerNextToEndpoints;

        var info = Assert.Single(GetGeneratorDiagnostics(source, AllExceptExcludedConfig));

        Assert.Equal(EndpointsLeftOutId, info.Id);
        Assert.StartsWith("AllExceptExcluded mode selects none of the project's 1 Minimal API endpoint,", info.GetMessage());
    }

    [Fact]
    public void Generator_GeneratesAnEndpointsToolAgain_AfterAnEditToTheFileThatGivesItsPrefix()
    {
        // The route comes from a call in another file, so an edit there has to reach the endpoint's tool.
        const string callerFile = "Program.cs";
        const string endpointsFile = "OrderEndpoints.cs";
        const string caller = MinimalApiUsings + @"
namespace TestApi
{
    public static class Startup
    {
        public static void Map(WebApplication app) => app.MapGroup(""/v1"").MapOrders();
    }
}
";
        const string endpoints = MinimalApiUsings + @"
namespace TestApi
{
    public static class OrderEndpoints
    {
        public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder builder)
        {
            builder.MapGet(""/orders/{id}"", (int id) => id).WithName(""GetOrder"");
            return builder;
        }
    }
}
";
        var compilation = CreateCompilationOfFiles((callerFile, caller), (endpointsFile, endpoints));
        var driver = CreateTrackingDriver(MinimalApisConfig).RunGenerators(compilation);
        Assert.Contains("$\"/v1/orders/{routeid}\"", GetGeneratedSources(driver)["OrderEndpoints_GetOrderTool.g.cs"]);

        driver = driver.RunGenerators(ReplaceText(compilation, callerFile, "\"/v1\"", "\"/v2\""));

        Assert.Contains("$\"/v2/orders/{routeid}\"", GetGeneratedSources(driver)["OrderEndpoints_GetOrderTool.g.cs"]);
        AssertSourceOutputReasons(driver, TrackingNames.ToolSources, IncrementalStepRunReason.Modified);
    }

    private const string ControllerNextToEndpoints = @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [HttpGet]
        public int GetAll() => 1;
    }
}
";

    /// <summary>
    /// A class <c>Endpoints</c> whose <c>Map(WebApplication app)</c> runs the statements.
    /// </summary>
    private static string MinimalApi(string statements)
    {
        return MinimalApiClass($@"
        public static void Map(WebApplication app)
        {{
            {statements}
        }}");
    }

    private static string MinimalApiClass(string members)
    {
        return MinimalApiUsings + MinimalApiModels + $@"
namespace TestApi
{{
    public static class Endpoints
    {{
        {members}
    }}
}}
";
    }

    /// <summary>
    /// The tool classes the generated registration lists, in its order.
    /// </summary>
    private static string[] GetToolClassNames(string generated)
    {
        return System.Text.RegularExpressions.Regex.Matches(generated, @"^\s*typeof\((\w+)\),\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(x => x.Groups[1].Value)
            .ToArray();
    }

    private static string GenerateCompilingToolClass(string source, string generatorJson, string toolClassName)
    {
        var errors = CompileWithGeneratedTools(source, generatorJson);
        Assert.False(errors.Any(), string.Join(Environment.NewLine, errors));

        return GetToolClass(RunGenerator(source, generatorJson), toolClassName);
    }
}
