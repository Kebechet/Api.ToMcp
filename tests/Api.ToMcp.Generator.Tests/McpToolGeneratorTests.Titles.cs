using System.Linq;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Tool titles: clients show a tool's title in place of its name, in a connector directory and when asking the user to
/// approve a call, and the Claude directory flags every tool that has none. Each generated tool therefore gets one.
/// </summary>
public partial class McpToolGeneratorTests
{
    [Fact]
    public void Generator_ToolTitle_IsDerivedFromTheActionName()
    {
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [McpExpose]
    [Route(""api/[controller]"")]
    public class SessionsController : ControllerBase
    {
        [HttpPost(""workouts"")]
        public Task<string> LogWorkout(string name) => Task.FromResult(string.Empty);

        [HttpGet(""rep-max"")]
        public Task<string> GetXRepMax(Guid exerciseId) => Task.FromResult(string.Empty);

        [HttpGet]
        public Task<string> GetAllAsync() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Contains("Name = \"Sessions_LogWorkout\", Title = \"Log workout\", ", GetToolAttributeLine(generated, "SessionsController_LogWorkoutTool"));
        Assert.Contains("Name = \"Sessions_GetXRepMax\", Title = \"Get X rep max\", ", GetToolAttributeLine(generated, "SessionsController_GetXRepMaxTool"));
        // The tool name keeps the method's Async suffix, which MVC drops from the action name; a title has no use for it.
        Assert.Contains("Name = \"Sessions_GetAllAsync\", Title = \"Get all\", ", GetToolAttributeLine(generated, "SessionsController_GetAllAsyncTool"));
    }

    [Fact]
    public void Generator_ToolTitle_SetOnTheAction_WinsOverTheDerivedOne()
    {
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class SessionsController : ControllerBase
    {
        [McpExpose(Title = ""Record a workout"")]
        [HttpPost(""workouts"")]
        public Task<string> LogWorkout(string name) => Task.FromResult(string.Empty);

        [McpExpose(Title = ""   "")]
        [HttpPost(""goals"")]
        public Task<string> CreateGoal(string name) => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Contains("Title = \"Record a workout\", ", GetToolAttributeLine(generated, "SessionsController_LogWorkoutTool"));
        // A blank title would leave the client nothing to show, so it counts as unset.
        Assert.Contains("Title = \"Create goal\", ", GetToolAttributeLine(generated, "SessionsController_CreateGoalTool"));
    }

    [Fact]
    public void Generator_ToolTitle_FollowsACustomToolName()
    {
        // A custom name is what the client lists, so a title derived from the method name would describe a tool the user
        // never sees by that name.
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class StatisticsController : ControllerBase
    {
        [McpExpose(ToolName = ""Statistics_GetPersonalBests"")]
        [HttpGet(""computed-bests"")]
        public Task<string> GetComputedBests(Guid exerciseId) => Task.FromResult(string.Empty);

        [McpExpose(ToolName = ""get_weekly_volume"")]
        [HttpGet(""volume"")]
        public Task<string> GetVolume() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Contains("Name = \"Statistics_GetPersonalBests\", Title = \"Get personal bests\", ", GetToolAttributeLine(generated, "StatisticsController_GetComputedBestsTool"));
        Assert.Contains("Name = \"get_weekly_volume\", Title = \"Get weekly volume\", ", GetToolAttributeLine(generated, "StatisticsController_GetVolumeTool"));
    }

    [Fact]
    public void Generator_ToolTitle_SetOnAController_DoesNotNameItsActions()
    {
        // One title cannot name every action of a controller; copying it would give each tool the same name in a listing.
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [McpExpose(Title = ""Reports"")]
    [Route(""api/[controller]"")]
    public class ReportsController : ControllerBase
    {
        [HttpGet(""daily"")]
        public Task<string> GetDaily() => Task.FromResult(string.Empty);
    }
}
";
        var generated = RunGenerator(Usings + controller, AllExceptExcludedConfig);

        Assert.Contains("Title = \"Get daily\", ", GetToolAttributeLine(generated, "ReportsController_GetDailyTool"));
    }

    [Fact]
    public void Generator_ToolTitle_WithQuotesAndBackslashes_CompilesAgainstTheRealSdk()
    {
        const string controller = @"
using Api.ToMcp.Abstractions;

namespace TestApi
{
    [Route(""api/[controller]"")]
    public class SessionsController : ControllerBase
    {
        [McpExpose(Title = ""Log a \""quick\"" workout \\ fast"")]
        [HttpPost(""workouts"")]
        public Task<string> LogWorkout(string name) => Task.FromResult(string.Empty);
    }
}
";
        var errors = CompileWithGeneratedTools(Usings + controller, AllExceptExcludedConfig);

        Assert.False(errors.Any(), string.Join(System.Environment.NewLine, errors));
    }
}
