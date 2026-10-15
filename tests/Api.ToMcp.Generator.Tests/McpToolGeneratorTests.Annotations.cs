using System.Linq;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Tool annotations: every generated tool states all four hints explicitly, because MCP clients read an omitted hint as
/// its spec default (not read-only, destructive, open-world), and directories such as ChatGPT's refuse a tool that
/// leaves any of them out.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Theory]
    [InlineData("HttpGet", "true", "false", "true")]
    [InlineData("HttpPost", "false", "false", "false")]
    [InlineData("HttpPut", "false", "true", "true")]
    [InlineData("HttpPatch", "false", "true", "false")]
    [InlineData("HttpDelete", "false", "true", "true")]
    public void Generator_ToolAnnotations_FollowTheHttpMethod(string httpAttribute, string readOnly, string destructive, string idempotent)
    {
        var controller = $@"
using Api.ToMcp.Abstractions;

namespace TestApi
{{
    [McpExpose]
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {{
        [{httpAttribute}(""{{id}}"")]
        public Task<string> Handle(Guid id) => Task.FromResult(string.Empty);
    }}
}}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal(
            $"[McpServerTool(Name = \"Products_Handle\", Title = \"Handle\", ReadOnly = {readOnly}, Destructive = {destructive}, Idempotent = {idempotent}, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ProductsController_HandleTool"));
    }

    [Fact]
    public void Generator_ToolAnnotations_FollowTheDeclaredScope_NotTheVerb()
    {
        // A search that takes its filter as a POST body changes nothing; calling it read-only is what lets a client skip
        // the confirmation it asks for before a write.
        const string controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [McpExpose(Scope = McpScope.Read)]
        [HttpPost(""search"")]
        public Task<string> Search(string query) => Task.FromResult(string.Empty);

        [McpExpose(Scope = McpScope.Read | McpScope.Delete)]
        [HttpPost(""purge"")]
        public Task<string> Purge(string query) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal(
            "[McpServerTool(Name = \"Reports_Search\", Title = \"Search\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ReportsController_SearchTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Reports_Purge\", Title = \"Purge\", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ReportsController_PurgeTool"));
    }

    [Fact]
    public void Generator_ToolAnnotations_ReadScope_NeverMakesAPutPatchOrDeleteReadOnly()
    {
        // A scope is a permission: a controller-wide Scope = Read loosens the grant check for every action in it, but must
        // not also tell a client that its DELETE is safe to run unconfirmed. Only POST, which serves queries too, follows it.
        const string controller = @"
using Api.ToMcp.Abstractions;
using Api.ToMcp.Abstractions.Scopes;

namespace TestApi
{
    [McpExpose(Scope = McpScope.Read)]
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [HttpPost(""search"")]
        public Task<string> Search(string query) => Task.FromResult(string.Empty);

        [HttpPut(""{id}"")]
        public Task<string> Replace(Guid id) => Task.FromResult(string.Empty);

        [HttpPatch(""{id}"")]
        public Task<string> Rename(Guid id) => Task.FromResult(string.Empty);

        [HttpDelete(""{id}"")]
        public Task<string> Remove(Guid id) => Task.FromResult(string.Empty);

        [McpExpose(ReadOnly = true)]
        [HttpDelete(""preview/{id}"")]
        public Task<string> PreviewRemove(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal(
            "[McpServerTool(Name = \"Reports_Search\", Title = \"Search\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ReportsController_SearchTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Reports_Replace\", Title = \"Replace\", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ReportsController_ReplaceTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Reports_Rename\", Title = \"Rename\", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ReportsController_RenameTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Reports_Remove\", Title = \"Remove\", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ReportsController_RemoveTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Reports_PreviewRemove\", Title = \"Preview remove\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ReportsController_PreviewRemoveTool"));
    }

    [Fact]
    public void Generator_ToolAnnotations_DeclaredOnTheAction_OverrideTheControllerAndTheDerivedValues()
    {
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [McpExpose(OpenWorld = true)]
    [Route(""api/[controller]"")]
    public class WebhooksController : ControllerBase
    {
        [HttpPost(""send"")]
        public Task<string> Send(string url) => Task.FromResult(string.Empty);

        [McpExpose(OpenWorld = false, Destructive = false, Idempotent = false)]
        [HttpPut(""{id}"")]
        public Task<string> Replace(Guid id) => Task.FromResult(string.Empty);

        [McpExpose(ReadOnly = true)]
        [HttpPost(""preview"")]
        public Task<string> Preview(string url) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal(
            "[McpServerTool(Name = \"Webhooks_Send\", Title = \"Send\", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]",
            GetToolAttributeLine(generated, "WebhooksController_SendTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Webhooks_Replace\", Title = \"Replace\", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]",
            GetToolAttributeLine(generated, "WebhooksController_ReplaceTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Webhooks_Preview\", Title = \"Preview\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]",
            GetToolAttributeLine(generated, "WebhooksController_PreviewTool"));
    }

    [Fact]
    public void Generator_ToolAnnotations_ExplicitFalse_OverridesADerivedTrue()
    {
        // The generator tells an unset hint from one set to false; reading false as "not set" would keep the derived true.
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class CountersController : ControllerBase
    {
        [McpExpose(ReadOnly = false)]
        [HttpGet(""next"")]
        public Task<string> Next() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal(
            "[McpServerTool(Name = \"Counters_Next\", Title = \"Next\", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]",
            GetToolAttributeLine(generated, "CountersController_NextTool"));
    }

    [Fact]
    public void Generator_ToolAnnotations_InheritFromTheNearestMcpExpose_OfABaseControllerOrOverriddenAction()
    {
        // As with Scope: Roslyn's GetAttributes() returns only what a symbol declares itself, so the base controller's and
        // the overridden action's [McpExpose] must be looked up, and a derived controller's own attribute hides the base's.
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [McpExpose(OpenWorld = true)]
    public abstract class ExternalControllerBase : ControllerBase
    {
        [McpExpose(Destructive = true)]
        [HttpPost(""sync"")]
        public virtual Task<string> Sync(string target) => Task.FromResult(string.Empty);
    }

    [Route(""api/[controller]"")]
    public class MirrorsController : ExternalControllerBase
    {
        [HttpGet]
        public Task<string> GetAll() => Task.FromResult(string.Empty);

        [HttpPost(""sync"")]
        public override Task<string> Sync(string target) => Task.FromResult(string.Empty);
    }

    [McpExpose(ReadOnly = true)]
    [Route(""api/[controller]"")]
    public class ArchivesController : ExternalControllerBase
    {
        [HttpPost(""push"")]
        public Task<string> Push(string target) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Equal(
            "[McpServerTool(Name = \"Mirrors_GetAll\", Title = \"Get all\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]",
            GetToolAttributeLine(generated, "MirrorsController_GetAllTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Mirrors_Sync\", Title = \"Sync\", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]",
            GetToolAttributeLine(generated, "MirrorsController_SyncTool"));
        Assert.Equal(
            "[McpServerTool(Name = \"Archives_Push\", Title = \"Push\", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]",
            GetToolAttributeLine(generated, "ArchivesController_PushTool"));
    }

    [Fact]
    public void Generator_ToolAnnotations_CompileAgainstTheRealSdk()
    {
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class ProductsController : ControllerBase
    {
        [McpExpose(ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
        [HttpDelete(""{id}"")]
        public Task<string> Delete(Guid id) => Task.FromResult(string.Empty);
    }
}
";
        var errors = CompileWithGeneratedTools(Usings + controller, AllExceptExcludedConfig);

        Assert.False(errors.Any(), string.Join(System.Environment.NewLine, errors));
    }

    private static string GetToolAttributeLine(string generated, string toolClassName)
    {
        return GetToolLine(generated, toolClassName, "[McpServerTool(");
    }
}
