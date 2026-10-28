using System.IO;
using System.Text.Json;

namespace LightBrowser;

// Memory limits. Edit %LocalAppData%\LightBrowser\settings.json and restart to change them.
public class AppSettings
{
    // Most tabs kept loaded at once across ALL profiles; older hidden tabs are unloaded
    // (their address is kept, they reload when clicked).
    public int MaxLoadedTabs { get; set; } = 5;

    // A hidden tab nobody looked at for this long is unloaded, even under the limit.
    public int IdleMinutes { get; set; } = 10;

    // A tab used within this many seconds is never unloaded (protects login popups).
    public int GraceSeconds { get; set; } = 120;

    static string FilePath => Path.Combine(ProfileStore.Root, "settings.json");

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? settings;
            else
            {
                // Write the defaults once so the file is there to edit.
                Directory.CreateDirectory(ProfileStore.Root);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception)
        {
            // Unreadable settings: use the defaults.
        }
        settings.MaxLoadedTabs = Math.Clamp(settings.MaxLoadedTabs, 1, 500);
        settings.IdleMinutes = Math.Clamp(settings.IdleMinutes, 1, 1440);
        settings.GraceSeconds = Math.Clamp(settings.GraceSeconds, 0, 3600);
        return settings;
    }
}
