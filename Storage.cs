using System.IO;

namespace LightBrowser;

// Crash-safe local files. A write goes to a temp file and is swapped in, with the previous version
// kept as ".bak", so an interrupted write cannot leave a half-written file. A file that cannot be
// read is moved aside (".corrupt-<time>"), never overwritten, and the ".bak" is tried instead.
public static class SafeFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }

    // Returns what parse produced from the file or, failing that, from its backup; null if there is
    // neither. parse should throw or return null for unusable content.
    public static T? Read<T>(string path, Func<string, T?> parse) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                if (parse(File.ReadAllText(candidate)) is { } value) return value;
            }
            catch (Exception)
            {
                // fall through: unusable
            }
            MoveAside(candidate);
        }
        return null;
    }

    static void MoveAside(string file)
    {
        try { File.Move(file, file + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true); }
        catch (Exception) { }
    }
}
