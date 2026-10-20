using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace Api.ToMcp.Generator.Tests.BuildTargets;

/// <summary>
/// Runs the package's build targets, which find, create and register generator.json, through MSBuild on a minimal
/// project. A mistake there leaves the generator without its config, which no generator test can see, and broke this
/// repo's Linux CI once: https://github.com/Kebechet/Api.ToMcp/issues/17.
/// </summary>
/// <remarks>
/// A folder cased differently, such as mcp/ beside the default Mcp/, exists apart from it only on a case-sensitive file
/// system. On Windows each test makes its directory case-sensitive where the system allows it. Where it stays
/// case-insensitive, the case-variant tests check what such a build gets instead.
/// </remarks>
public sealed class ConfigTargetsTests : IDisposable
{
    private const string TargetsFolder = "BuildTargets";
    private const string PackageTargetsFileName = "Kebechet.Api.ToMcp.targets";
    private const string GeneratorTargetsFileName = "Api.ToMcp.Generator.targets";
    private const string AdditionalFileMarker = "AdditionalFile=";
    private const string Config = @"{ ""schemaVersion"": 1, ""mode"": ""SelectedOnly"", ""include"": [""ProductsController""] }";

    private readonly string _projectDirectory = Path.Combine(Path.GetTempPath(), "ApiToMcp.TargetsTests", Guid.NewGuid().ToString("N"));
    private readonly bool _isCaseSensitive;

    public ConfigTargetsTests()
    {
        Directory.CreateDirectory(_projectDirectory);
        TryMakeCaseSensitive(_projectDirectory);
        _isCaseSensitive = IsCaseSensitive(_projectDirectory);
    }

    public static TheoryData<string?, string?> ProjectsWithAConfigInALowerCaseFolder => new()
    {
        { null, null },
        // The setting is about writing a starter config; it does not make an existing config one case away any less so.
        { "ApiToMcpAutoCreateConfig=false", null },
        // The README has users list the config themselves; on a case-sensitive file system that names a missing file.
        { null, @"<AdditionalFiles Include=""Mcp\generator.json"" />" },
    };

    [Fact]
    public void BothCopiesOfTheTargets_AreIdentical()
    {
        // The demo imports the generator's copy to build the way a consumer of the package does with the package's copy.
        Assert.Equal(File.ReadAllText(GetTargetsPath(PackageTargetsFileName)), File.ReadAllText(GetTargetsPath(GeneratorTargetsFileName)));
    }

    [Fact]
    public void Build_WritesAndRegistersAStarterConfig_WhenThereIsNone()
    {
        var starterPath = Path.Combine(_projectDirectory, "Mcp", "generator.json");

        var result = Build();

        Assert.True(File.Exists(starterPath), result.Output);
        Assert.Contains(@"""include"": []", File.ReadAllText(starterPath));
        Assert.StartsWith($"{starterPath} : warning MCP100: ", result.GetWarning("MCP100"));
        Assert.Equal(new[] { starterPath }, result.AdditionalFiles);
    }

    [Fact]
    public void Build_WritesNoStarterConfig_WhenAutoCreateIsOff()
    {
        var result = Build("ApiToMcpAutoCreateConfig=false");

        Assert.False(Directory.Exists(Path.Combine(_projectDirectory, "Mcp")), result.Output);
        Assert.Null(result.GetWarning("MCP100"));
        Assert.Empty(result.AdditionalFiles);
    }

    [Theory]
    [MemberData(nameof(ProjectsWithAConfigInALowerCaseFolder))]
    public void Build_GivesTheGeneratorTheConfigInALowerCaseFolder(string? properties, string? items)
    {
        var configPath = WriteConfig("mcp");

        var result = Build(properties, items);

        var additionalFile = Assert.Single(result.AdditionalFiles);
        Assert.True(File.Exists(additionalFile), $"The generator gets {additionalFile}, which does not exist.{Environment.NewLine}{result.Output}");
        Assert.Equal(Config, File.ReadAllText(additionalFile));
        Assert.Equal(new[] { "mcp" }, GetConfigFolderNames());
        Assert.Null(result.GetWarning("MCP100"));
        // On a case-insensitive file system the configured Mcp/generator.json is this very file: nothing to warn of.
        if (_isCaseSensitive)
        {
            Assert.StartsWith($"{configPath} : warning MCP101: ", result.GetWarning("MCP101"));
        }
    }

    [Fact]
    public void Build_WritesAStarterConfig_BesideAnUnrelatedGeneratorJson()
    {
        WriteConfig("Tools");
        var starterPath = Path.Combine(_projectDirectory, "Mcp", "generator.json");

        var result = Build();

        Assert.True(File.Exists(starterPath), result.Output);
        Assert.NotNull(result.GetWarning("MCP100"));
        Assert.Null(result.GetWarning("MCP101"));
        Assert.Equal(new[] { starterPath }, result.AdditionalFiles);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private BuildResult Build(string? properties = null, string? items = null)
    {
        var projectPath = Path.Combine(_projectDirectory, "Fixture.proj");
        File.WriteAllText(projectPath, $@"<Project>
  <ItemGroup>{items}</ItemGroup>
  <Import Project=""{GetTargetsPath(PackageTargetsFileName)}"" />
  <Target Name=""CoreCompile"">
    <Message Importance=""high"" Text=""{AdditionalFileMarker}%(AdditionalFiles.FullPath)"" Condition=""'@(AdditionalFiles)' != ''"" />
  </Target>
</Project>");

        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "msbuild", projectPath, "-t:CoreCompile", "-nologo", "-nodeReuse:false", "-tl:false", "-v:minimal" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (properties != null)
        {
            startInfo.ArgumentList.Add($"-p:{properties}");
        }

        // The test host inherits these from the dotnet test run; they would steer this MSBuild by that build's settings.
        foreach (var name in startInfo.Environment.Keys.Where(x => x.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "MSBuild did not finish within two minutes.");

        var output = standardOutput.Result + standardError.Result;
        Assert.True(process.ExitCode == 0, output);

        return new BuildResult(output);
    }

    private string WriteConfig(string folderName)
    {
        var configPath = Path.Combine(_projectDirectory, folderName, "generator.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, Config);

        return configPath;
    }

    private IEnumerable<string> GetConfigFolderNames()
    {
        return Directory.GetDirectories(_projectDirectory)
            .Select(x => Path.GetFileName(x))
            .Where(x => string.Equals(x, "mcp", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetTargetsPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, TargetsFolder, fileName);
    }

    private static void TryMakeCaseSensitive(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Needs an empty directory, and fails where Windows lacks the feature; the tests then run case-insensitive.
            using var process = Process.Start(new ProcessStartInfo("fsutil.exe")
            {
                ArgumentList = { "file", "setCaseSensitiveInfo", directory, "enable" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            process?.WaitForExit(30_000);
        }
        catch (Win32Exception)
        {
        }
    }

    private static bool IsCaseSensitive(string directory)
    {
        var probePath = Path.Combine(directory, "probe");
        File.WriteAllText(probePath, string.Empty);
        try
        {
            return !File.Exists(Path.Combine(directory, "PROBE"));
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private sealed class BuildResult
    {
        private readonly string[] _lines;

        public BuildResult(string output)
        {
            Output = output;
            _lines = output.Split('\n').Select(x => x.Trim()).ToArray();
        }

        public string Output { get; }

        public IReadOnlyList<string> AdditionalFiles => _lines
            .Where(x => x.StartsWith(AdditionalFileMarker, StringComparison.Ordinal))
            .Select(x => x.Substring(AdditionalFileMarker.Length))
            .ToList();

        public string? GetWarning(string code)
        {
            return _lines.FirstOrDefault(x => x.Contains($"warning {code}: ", StringComparison.Ordinal));
        }
    }
}
