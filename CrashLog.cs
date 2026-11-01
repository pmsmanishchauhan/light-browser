using System.IO;

namespace LightBrowser;

// Unexpected errors are written to crash.log in the data folder (newest at the end; the file is kept under
// about 0.5 MB, the previous one becomes crash.log.old), so a problem can be looked at after the fact.
// Writing the log never throws.
public static class CrashLog
{
    public const long MaxBytes = 512 * 1024;

    public static string FilePath => Path.Combine(ProfileStore.Root, "crash.log");

    public static void Write(string source, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(ProfileStore.Root);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Delete(FilePath + ".old");
                File.Move(FilePath, FilePath + ".old");
            }
            File.AppendAllText(FilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}  v{Updater.CurrentVersion}  {Environment.OSVersion.VersionString}\r\n" +
                $"{error}\r\n\r\n");
        }
        catch (Exception)
        {
            // Nothing sensible left to do if the log itself cannot be written.
        }
    }
}

// Lets a few errors pass (the app keeps running), but gives up when they keep coming: "max" errors within
// "window" means the app is in a bad state and should close after saving.
public sealed class ErrorBudget
{
    readonly int _max;
    readonly TimeSpan _window;
    readonly Queue<DateTime> _times = new();

    public ErrorBudget(int max, TimeSpan window) { _max = max; _window = window; }

    // Records one error. True while the app may keep running; false once the budget is spent.
    public bool Record(DateTime now)
    {
        _times.Enqueue(now);
        while (_times.Count > 0 && now - _times.Peek() > _window) _times.Dequeue();
        return _times.Count < _max;
    }
}
