[assembly: AssemblyFixture(typeof(VarlatchKit.Tests.TestDirectory))]

namespace VarlatchKit.Tests;

/// <summary>
/// Every file the tests write goes under one directory per run, removed
/// when the run ends.
/// </summary>
public sealed class TestDirectory : IDisposable
{
    public static string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"varlatch-tests-{Environment.ProcessId}");

    /// <summary>A path under the run's directory that does not exist yet.</summary>
    public static string Path(string prefix)
    {
        Directory.CreateDirectory(Root);
        return System.IO.Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A process a test started may still hold a file; the system
            // cleans the temporary directory in time.
        }
    }
}
