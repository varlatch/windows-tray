using System.Text;

namespace VarlatchKit;

/// <summary>Files written next to their place and renamed over it, so a reader never sees half of one.</summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="text"/> as UTF-8; failures are ignored, as for any state file.</summary>
    public static bool WriteAllText(string path, string text)
    {
        var temporary = path + ".tmp-" + Environment.ProcessId;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception) { }
            return false;
        }
    }
}
