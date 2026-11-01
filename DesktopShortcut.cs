using System.IO;

namespace LightBrowser;

// Puts a "LightBrowser" shortcut on the desktop the first time the app runs, and mends it if the exe was moved.
// A shortcut the user deletes stays deleted: once it has been offered it is only repaired, never recreated.
public static class DesktopShortcut
{
    public const string FileName = "LightBrowser.lnk";

    public enum Plan { None, Create, Repair }
    public enum Outcome { Nothing, Present, Created, Repaired, Failed }

    public static Plan Decide(bool alreadyOffered, bool linkExists, string? linkTarget, bool targetExists, string exe)
    {
        if (!linkExists) return alreadyOffered ? Plan.None : Plan.Create;
        if (string.Equals(linkTarget, exe, StringComparison.OrdinalIgnoreCase)) return Plan.None;
        // The link points somewhere else: mend it only when that place is gone (the exe was moved or renamed),
        // so a link to another copy of the app that still exists is left alone.
        return targetExists ? Plan.None : Plan.Repair;
    }

    // Where to put the shortcut. Test runs (their own data folder) leave the real desktop alone unless a folder is given.
    public static string? DesktopFolder()
    {
        var given = Environment.GetEnvironmentVariable("LIGHTBROWSER_DESKTOP_DIR");
        if (!string.IsNullOrWhiteSpace(given)) return given;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIGHTBROWSER_HOME"))) return null;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop) ? null : desktop;
    }

    public static Outcome Ensure(bool alreadyOffered, string? exe, string? folder)
    {
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return Outcome.Nothing;
        try
        {
            var link = Path.Combine(folder, FileName);
            var exists = File.Exists(link);
            var target = exists ? ReadTarget(link) : null;
            var plan = Decide(alreadyOffered, exists, target, target != null && File.Exists(target), exe);
            if (plan == Plan.None) return exists ? Outcome.Present : Outcome.Nothing;
            Write(link, exe);
            return plan == Plan.Create ? Outcome.Created : Outcome.Repaired;
        }
        catch (Exception)
        {
            return Outcome.Failed;   // no desktop, read-only, or the shell scripting host is blocked: not worth bothering anyone
        }
    }

    static dynamic Shell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("no shell"))!;

    public static string? ReadTarget(string link)
    {
        try { return (string?)Shell().CreateShortcut(link).TargetPath; }
        catch (Exception) { return null; }
    }

    public static void Write(string link, string exe)
    {
        var shortcut = Shell().CreateShortcut(link);
        shortcut.TargetPath = exe;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
        shortcut.IconLocation = exe + ",0";
        shortcut.Description = "LightBrowser - one browser, a separate sign-in for every client";
        shortcut.Save();
    }
}
