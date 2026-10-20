using System.Diagnostics;
using Api.ToMcp.Abstractions;
using Xunit;

namespace Api.ToMcp.Runtime.Tests;

public class IsolatedAssemblyCopyTests
{
    [Fact]
    public void Creating_KeepsTheCopies_OfAnotherTestRunThatIsStillRunning()
    {
        // Two test processes run at once when suites run side by side, as in two worktrees; deleting the other's copy
        // between its writing and reading the XML file failed one of its tests now and then.
        var runningProcessId = Process.GetProcesses().Select(x => x.Id).First(x => x != Environment.ProcessId && x != 0);
        var otherRunDirectoryPath = CreateRunDirectory(runningProcessId);
        try
        {
            using var assemblyCopy = new IsolatedAssemblyCopy(typeof(McpExposeAttribute).Assembly, xmlDocContent: null);

            Assert.True(Directory.Exists(otherRunDirectoryPath));
        }
        finally
        {
            Directory.Delete(otherRunDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void Creating_DeletesTheCopies_OfATestRunThatEnded()
    {
        // No process gets this id: Windows hands out multiples of 4, and Linux stays far below it.
        var endedRunDirectoryPath = CreateRunDirectory(int.MaxValue);

        using var assemblyCopy = new IsolatedAssemblyCopy(typeof(McpExposeAttribute).Assembly, xmlDocContent: null);

        Assert.False(Directory.Exists(endedRunDirectoryPath));
    }

    private static string CreateRunDirectory(int processId)
    {
        var directoryPath = Path.Combine(IsolatedAssemblyCopy.RootPath, $"{processId}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);

        return directoryPath;
    }
}
