using Api.ToMcp.Generator.Parsing;
using Xunit;

namespace Api.ToMcp.Generator.Tests.Parsing;

public class ToolTitleBuilderTests
{
    [Theory]
    [InlineData("LogWorkout", "Log workout")]
    [InlineData("GetTrainingDayHeaders", "Get training day headers")]
    [InlineData("Search", "Search")]
    [InlineData("GetXRepMax", "Get X rep max")]
    [InlineData("MCPTools", "MCP tools")]
    [InlineData("GetTop10Users", "Get top 10 users")]
    [InlineData("get_report_summary", "Get report summary")]
    [InlineData("list-open-orders", "List open orders")]
    public void ToSentence_Identifier_ReadsAsASentence(string identifier, string expectedTitle)
    {
        Assert.Equal(expectedTitle, ToolTitleBuilder.ToSentence(identifier));
    }

    [Fact]
    public void ToSentence_IdentifierWithoutAWord_IsKeptSoTheTitleIsNeverEmpty()
    {
        Assert.Equal("__", ToolTitleBuilder.ToSentence("__"));
    }

    [Theory]
    [InlineData(null, "LogWorkout", "Log workout")]
    [InlineData("", "LogWorkout", "Log workout")]
    [InlineData("Sessions_RecordWorkout", "LogWorkout", "Record workout")]
    [InlineData("RecordWorkout", "LogWorkout", "Record workout")]
    [InlineData("record_workout", "LogWorkout", "Record workout")]
    [InlineData("Sessions_", "LogWorkout", "Sessions")]
    public void Build_CustomToolName_WinsBecauseItIsWhatTheClientLists(string? customToolName, string actionName, string expectedTitle)
    {
        Assert.Equal(expectedTitle, ToolTitleBuilder.Build(customToolName, actionName));
    }
}
