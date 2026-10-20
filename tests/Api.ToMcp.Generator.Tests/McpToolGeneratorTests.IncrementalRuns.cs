using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// The generator as an IDE runs it: once per edit, with the results of the run before. Whatever it keeps from that run,
/// it reports the diagnostics a run on the edited compilation reports, each pointing into the edited compilation's
/// files, where <c>#pragma warning disable</c> and <c>.editorconfig</c> severities apply to it. See
/// https://github.com/Kebechet/Api.ToMcp/issues/46.
/// </summary>
public partial class McpToolGeneratorTests
{
    private const string ControllersFile = "Controllers.cs";
    private const string FileWithoutControllers = "Clock.cs";

    private const string ClockSource = @"
namespace TestApi.Services
{
    public static class Clock
    {
        public static int Ticks() => 0;
    }
}
";

    private const string ActionWithAnUnfilledRouteValue = SimpleTypeSources.Usings + @"
namespace TestApi
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpGet(""z/{id}"")]
        public IActionResult Z() => Ok(RouteData.Values[""id""]);

        [HttpGet(""all"")]
        public IActionResult All() => Ok();
    }
}
";

    [Theory]
    [InlineData(InvalidConfigId)]
    [InlineData(NoToolsGeneratedId)]
    [InlineData(DuplicateToolNameId)]
    [InlineData(UnmatchedConfigEntryId)]
    [InlineData(UnbuildableRouteId)]
    public void Generator_ReportsWhatARunOnTheEditedCompilationReports_AfterAnEdit(string diagnosticId)
    {
        var (source, generatorJson) = GetProjectReporting(diagnosticId);
        var compilation = CreateCompilationOfFiles((ControllersFile, source), (FileWithoutControllers, ClockSource));
        var driver = CreateTrackingDriver(generatorJson).RunGenerators(compilation);
        Assert.Contains(GetDiagnostics(driver), x => x.Id == diagnosticId);

        // An edit to a file without controllers, then one that moves everything in the controllers' file down a line.
        var edits = new Func<Compilation, Compilation>[]
        {
            x => InsertText(x, FileWithoutControllers, ClockSource.Length, "// edited\n"),
            x => InsertText(x, ControllersFile, 0, "// edited\n")
        };
        foreach (var edit in edits)
        {
            compilation = edit(compilation);
            driver = driver.RunGenerators(compilation);

            var expected = DescribeDiagnostics(CreateTrackingDriver(generatorJson).RunGenerators(compilation), compilation);
            Assert.Contains(expected, x => x.StartsWith(diagnosticId, StringComparison.Ordinal));
            Assert.Equal(expected, DescribeDiagnostics(driver, compilation));
        }
    }

    [Theory]
    [InlineData(DuplicateToolNameId)]
    [InlineData(UnbuildableRouteId)]
    public void Generator_LetsAPragmaAnEditAddsSuppressTheWarning_AtTheActionItPointsAt(string diagnosticId)
    {
        // The pragma takes the place of a comment as long as it, so the edit moves no action.
        var pragma = $"#pragma warning disable {diagnosticId}";
        var placeholder = "//" + new string('-', pragma.Length - 2);
        var (source, generatorJson) = GetProjectReporting(diagnosticId);
        var compilation = CreateCompilationOfFiles((ControllersFile, placeholder + "\n" + source), (FileWithoutControllers, ClockSource));
        var driver = CreateTrackingDriver(generatorJson).RunGenerators(compilation);
        Assert.Contains(GetDiagnostics(driver), x => x.Id == diagnosticId && !x.IsSuppressed);

        compilation = ReplaceText(compilation, ControllersFile, placeholder, pragma);
        driver = driver.RunGenerators(compilation);

        Assert.DoesNotContain(GetDiagnostics(driver), x => x.Id == diagnosticId && !x.IsSuppressed);
    }

    /// <summary>
    /// A project and config for which the generator reports the diagnostic.
    /// </summary>
    private static (string Source, string GeneratorJson) GetProjectReporting(string diagnosticId)
    {
        return diagnosticId switch
        {
            InvalidConfigId => (Usings + ProductsController, @"{ ""mode"": ""Selected"", ""include"": [""ProductsController.GetAll""] }"),
            NoToolsGeneratedId => (Usings + ProductsController, StarterConfig),
            DuplicateToolNameId => (ActionsWithTheSameToolName, AllExceptExcludedConfig),
            UnmatchedConfigEntryId => (CrudControllers, @"{ ""mode"": ""AllExceptExcluded"", ""exclude"": [""Crud.Create""] }"),
            UnbuildableRouteId => (ActionWithAnUnfilledRouteValue, AllExceptExcludedConfig),
            _ => throw new ArgumentOutOfRangeException(nameof(diagnosticId), diagnosticId, null)
        };
    }

    /// <summary>
    /// A compilation of the files, each under its own path: the generator finds the syntax tree a warning points into by
    /// the path of its file.
    /// </summary>
    private static Compilation CreateCompilationOfFiles(params (string Path, string Source)[] files)
    {
        Compilation compilation = CreateCompilation(files.Select(x => x.Source).ToArray());
        var trees = compilation.SyntaxTrees.ToList();
        for (var i = 0; i < files.Length; i++)
        {
            compilation = compilation.ReplaceSyntaxTree(trees[i], trees[i].WithFilePath(files[i].Path));
        }

        return compilation;
    }

    private static Compilation InsertText(Compilation compilation, string path, int position, string text)
    {
        return EditText(compilation, path, x => new TextChange(new TextSpan(position, 0), text));
    }

    private static Compilation ReplaceText(Compilation compilation, string path, string oldText, string newText)
    {
        return EditText(compilation, path, x => new TextChange(new TextSpan(x.ToString().IndexOf(oldText, StringComparison.Ordinal), oldText.Length), newText));
    }

    private static Compilation EditText(Compilation compilation, string path, Func<SourceText, TextChange> getChange)
    {
        var tree = compilation.SyntaxTrees.Single(x => x.FilePath == path);
        var text = tree.GetText();

        return compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(text.WithChanges(getChange(text))));
    }

    private static GeneratorDriver CreateTrackingDriver(string generatorJson)
    {
        return CSharpGeneratorDriver.Create(
            new[] { new McpToolGenerator().AsSourceGenerator() },
            new AdditionalText[] { new InMemoryAdditionalText("Mcp/generator.json", generatorJson) },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
    }

    private static IReadOnlyList<Diagnostic> GetDiagnostics(GeneratorDriver driver)
    {
        return driver.GetRunResult().Diagnostics;
    }

    /// <summary>
    /// The diagnostics as text, each with its message, whether it is suppressed and where it points: in which file and
    /// span, and whether into a syntax tree of the compilation, which the compiler applies the file's severities through.
    /// </summary>
    private static List<string> DescribeDiagnostics(GeneratorDriver driver, Compilation compilation)
    {
        string DescribeLocation(Location location)
        {
            var tree = location.SourceTree is null ? "no syntax tree"
                : compilation.SyntaxTrees.Contains(location.SourceTree) ? "the compilation's syntax tree"
                : "a syntax tree of another compilation";

            return $"{location.GetLineSpan()} in {tree}";
        }

        return GetDiagnostics(driver)
            .Select(x => $"{x.Id} {x.Severity} suppressed={x.IsSuppressed} '{x.GetMessage()}' at {DescribeLocation(x.Location)}" +
                string.Concat(x.AdditionalLocations.Select(location => $" and {DescribeLocation(location)}")))
            .ToList();
    }
}
