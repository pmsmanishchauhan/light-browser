using System.Diagnostics;
using System.IO;

namespace LightBrowser;

// The running copy notes its process, version and exe in the data folder. A newer copy started later reads it to
// find the old one and ask it to close, so a freshly downloaded exe replaces the open app instead of only waking it.
public static class InstanceInfo
{
    public sealed record Running(int Pid, Version? Version, string Exe);

    static string FileFor(string folder) => Path.Combine(folder, "instance.txt");

    public static string Format(int pid, Version version, string exe) => $"{pid}|{version}|{exe}";

    public static Running? Parse(string? text)
    {
        var parts = (text ?? "").Trim().Split('|', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || pid <= 0) return null;
        return new Running(pid, Updater.ParseVersion(parts[1]), parts[2]);
    }

    // Only a strictly newer launch replaces a running copy. The same or an older one just brings it to the front,
    // so opening an old download never downgrades the app that is open.
    public static bool ShouldReplace(Version mine, Version? running) => running != null && mine > running;

    public static void Write(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(FileFor(folder), Format(Environment.ProcessId, Updater.CurrentVersion, Environment.ProcessPath ?? ""));
        }
        catch (Exception) { }   // only the "replace the old copy" convenience is lost
    }

    public static void Clear(string folder)
    {
        try
        {
            if (Parse(File.ReadAllText(FileFor(folder)))?.Pid == Environment.ProcessId) File.Delete(FileFor(folder));
        }
        catch (Exception) { }
    }

    // If the copy holding the data folder is older than this one, asks it to close (like clicking its close button, so
    // it saves its tabs) and returns true. False means leave it running and just wake it.
    public static bool CloseOlderCopy(string folder, Version mine)
    {
        Process? target = null;
        try
        {
            string? text = null;
            try { text = File.ReadAllText(FileFor(folder)); } catch (Exception) { }
            var info = Parse(text);
            if (info != null)
            {
                if (!ShouldReplace(mine, info.Version)) return false;
                target = Find(info);
            }
            else
            {
                // Versions before this one wrote no note. When exactly one other LightBrowser is running it is the one.
                var others = Process.GetProcesses()
                    .Where(p => p.Id != Environment.ProcessId && p.ProcessName.StartsWith("LightBrowser", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (others.Count == 1 && VersionOf(others[0]) is { } v && ShouldReplace(mine, v)) target = others[0];
            }
            return target != null && target.CloseMainWindow();
        }
        catch (Exception) { return false; }
        finally { target?.Dispose(); }
    }

    // The note could be stale (the old copy crashed and its pid was reused), so the process must be that exe.
    static Process? Find(Running info)
    {
        try
        {
            var p = Process.GetProcessById(info.Pid);
            return string.Equals(p.MainModule?.FileName, info.Exe, StringComparison.OrdinalIgnoreCase) ? p : null;
        }
        catch (Exception) { return null; }
    }

    static Version? VersionOf(Process p)
    {
        try { return Updater.ParseVersion(p.MainModule?.FileVersionInfo.ProductVersion); }
        catch (Exception) { return null; }
    }
}
