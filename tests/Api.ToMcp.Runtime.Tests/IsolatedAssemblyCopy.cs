using System.Reflection;
using System.Runtime.Loader;

namespace Api.ToMcp.Runtime.Tests;

/// <summary>
/// A copy of an assembly loaded from a folder of its own, so a test decides which XML documentation file sits next to
/// it. The reader looks beside <see cref="Assembly.Location"/>, and the real assemblies share one output folder with
/// every other test.
/// </summary>
internal sealed class IsolatedAssemblyCopy : IDisposable
{
    private static readonly string RootPath = Path.Combine(Path.GetTempPath(), "Api.ToMcp.Tests");

    private readonly string _directoryPath;
    private readonly AssemblyLoadContext _loadContext;

    public IsolatedAssemblyCopy(Assembly assembly, string? xmlDocContent)
    {
        DeleteCopiesOfEndedRuns();

        _directoryPath = Path.Combine(RootPath, $"{Environment.ProcessId}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directoryPath);

        var copyPath = Path.Combine(_directoryPath, Path.GetFileName(assembly.Location));
        File.Copy(assembly.Location, copyPath);
        if (xmlDocContent is not null)
        {
            File.WriteAllText(Path.ChangeExtension(copyPath, ".xml"), xmlDocContent);
        }

        _loadContext = new AssemblyLoadContext($"{nameof(IsolatedAssemblyCopy)}.{assembly.GetName().Name}", isCollectible: true);
        Assembly = _loadContext.LoadFromAssemblyPath(copyPath);
    }

    public Assembly Assembly { get; }

    public MemberInfo GetProperty(Type typeInOriginal, string propertyName)
    {
        return Assembly.GetType(typeInOriginal.FullName!)!.GetProperty(propertyName)!;
    }

    public void Dispose()
    {
        _loadContext.Unload();
        TryDeleteDirectory(_directoryPath);
    }

    /// <summary>
    /// A loaded copy stays mapped, so undeletable, until its test process ends; each run removes what earlier runs
    /// left behind. Folders of the current run are skipped: a parallel test may be about to load from one.
    /// </summary>
    private static void DeleteCopiesOfEndedRuns()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        var currentRunPrefix = $"{Environment.ProcessId}.";
        var earlierRunDirectoryPaths = Directory
            .GetDirectories(RootPath)
            .Where(x => !Path.GetFileName(x).StartsWith(currentRunPrefix, StringComparison.Ordinal));
        foreach (var earlierRunDirectoryPath in earlierRunDirectoryPaths)
        {
            TryDeleteDirectory(earlierRunDirectoryPath);
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
