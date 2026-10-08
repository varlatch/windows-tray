using VarlatchKit;

namespace Varlatch;

/// <summary>
/// <c>log.txt</c> in the state directory: what went wrong, for a bug report.
/// Never a credential: the app never sees one.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long Limit = 256 * 1024;

    public static string FilePath => Path.Combine(AppPaths.StateDirectory, "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.StateDirectory);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > Limit) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nowhere to write it.
            }
        }
    }
}
