using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightBrowser;

// Settings in %LocalAppData%\LightBrowser\settings.json. The usual way to change the memory limits is
// the mode buttons in the sidebar; the numbers below are only used when Mode is "Custom".
public class AppSettings
{
    public const string LowMemory = "LowMemory";
    public const string Balanced = "Balanced";
    public const string Performance = "Performance";
    public const string Custom = "Custom";

    // Max tabs loaded at once (across all profiles), minutes before an unused hidden tab is unloaded,
    // seconds a just-used tab is protected, and whether hidden tabs run in WebView2's low-memory mode.
    public readonly record struct ModeLimits(int MaxLoadedTabs, int IdleMinutes, int GraceSeconds, bool LowMemoryHiddenTabs);

    static readonly Dictionary<string, ModeLimits> Presets = new()
    {
        [LowMemory] = new(2, 3, 60, true),
        [Balanced] = new(5, 10, 120, true),
        [Performance] = new(15, 60, 120, false),
    };

    // LowMemory, Balanced, Performance, or Custom (uses the three numbers below).
    public string Mode { get; set; } = Balanced;

    public int MaxLoadedTabs { get; set; } = 5;
    public int IdleMinutes { get; set; } = 10;
    public int GraceSeconds { get; set; } = 120;

    // Extra Chromium switches for the WebView2 engine, e.g. "--process-per-site". Empty by default.
    // Only add switches you understand; none of the defaults weaken isolation or security.
    public string BrowserArguments { get; set; } = "";

    // The limits in force for the current mode.
    [JsonIgnore]
    public ModeLimits Limits =>
        Presets.TryGetValue(Mode, out var preset) ? preset : new ModeLimits(MaxLoadedTabs, IdleMinutes, GraceSeconds, true);

    static string FilePath => Path.Combine(ProfileStore.Root, "settings.json");

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            var json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            if (json != null)
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json) ?? settings;
                // A file from before modes existed: keep hand-edited numbers working as Custom.
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Mode", out _) &&
                    (settings.MaxLoadedTabs, settings.IdleMinutes, settings.GraceSeconds) != (5, 10, 120))
                    settings.Mode = Custom;
            }
            else settings.Save();   // write the defaults once so the file is there to edit
        }
        catch (Exception)
        {
            settings = new AppSettings();   // unreadable settings: use the defaults
        }
        settings.BrowserArguments ??= "";
        if (settings.Mode != Custom && !Presets.ContainsKey(settings.Mode)) settings.Mode = Balanced;
        settings.MaxLoadedTabs = Math.Clamp(settings.MaxLoadedTabs, 1, 500);
        settings.IdleMinutes = Math.Clamp(settings.IdleMinutes, 1, 1440);
        settings.GraceSeconds = Math.Clamp(settings.GraceSeconds, 0, 3600);
        return settings;
    }

    public void Save()
    {
        try
        {
            SafeFile.Write(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Mode still applies for this run; it just will not be remembered.
        }
    }
}
