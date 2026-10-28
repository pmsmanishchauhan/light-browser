using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace LightBrowser;

public class Profile : INotifyPropertyChanged
{
    string _name = "";
    string _color = "#4F8EF7";
    string _group = "";
    string _notes = "";
    DateTime? _lastUsed;

    public Profile() => Tabs.CollectionChanged += (_, _) => Changed(nameof(IsOpen));

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name
    {
        get => _name;
        set { _name = value; Changed(nameof(Name)); Changed(nameof(Initial)); }
    }

    // First letter, shown in the coloured avatar in the profile list.
    [JsonIgnore]
    public string Initial => Name.Trim().Length > 0 ? char.ToUpperInvariant(Name.Trim()[0]).ToString() : "?";

    public string Color
    {
        get => _color;
        set { _color = value; Changed(nameof(Color)); Changed(nameof(Brush)); }
    }

    public List<string> HomeUrls { get; set; } = new();

    // Tabs that were open when the app last closed; restored the next time the profile is opened.
    public List<string> OpenUrls { get; set; } = new();

    public string Group
    {
        get => _group;
        set { _group = value; Changed(nameof(Group)); Changed(nameof(GroupName)); }
    }

    public string Notes
    {
        get => _notes;
        set { _notes = value; Changed(nameof(Notes)); Changed(nameof(Tip)); }
    }

    // UTC time the profile was last opened.
    public DateTime? LastUsed
    {
        get => _lastUsed;
        set { _lastUsed = value; Changed(nameof(LastUsed)); Changed(nameof(Tip)); }
    }

    // In Trash: hidden from the profile list, logins kept until "Delete forever".
    public bool IsDeleted { get; set; }

    [JsonIgnore]
    public string GroupName => string.IsNullOrWhiteSpace(Group) ? "Ungrouped" : Group;

    // Hover text in the profile list: notes plus when it was last used. Null when there is neither.
    [JsonIgnore]
    public string? Tip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Notes)) lines.Add(Notes.Trim());
            if (LastUsed is { } t) lines.Add($"Last used {t.ToLocalTime():d MMM yyyy, HH:mm}");
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
    }

    // True once the profile has been opened this run (it has tabs); drives the green dot.
    [JsonIgnore]
    public bool IsOpen => Tabs.Count > 0;

    [JsonIgnore]
    public Brush Brush => new BrushConverter().ConvertFromString(Color) as Brush ?? Brushes.Gray;

    // WebView2 profile names allow limited characters, so use the id rather than the display name.
    [JsonIgnore]
    public string WebViewProfileName => "p" + Id;

    [JsonIgnore]
    public ObservableCollection<BrowserTab> Tabs { get; } = new();

    [JsonIgnore]
    public BrowserTab? Active { get; set; }

    // A new profile with the same name, group, colour, notes and home pages. Logins and site data
    // are deliberately not copied: the copy starts signed out, with its own isolated storage.
    // The copy is named "<name> (copy)", then "(copy 2)", "(copy 3)"... avoiding names already in use.
    public Profile Duplicate(IEnumerable<string>? existingNames = null)
    {
        var baseName = Name.Trim();
        while (System.Text.RegularExpressions.Regex.IsMatch(baseName, @" \(copy(?: \d+)?\)$"))
            baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @" \(copy(?: \d+)?\)$", "");
        var taken = new HashSet<string>(existingNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var name = baseName + " (copy)";
        for (var n = 2; taken.Contains(name); n++) name = $"{baseName} (copy {n})";
        return new Profile
        {
            Name = name, Color = Color, Group = Group, Notes = Notes, HomeUrls = new List<string>(HomeUrls),
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BrowserTab : INotifyPropertyChanged
{
    string _title = "New tab";

    // View is null for a restored tab that has not been opened yet; it costs no memory until selected.
    public WebView2? View { get; set; }

    public string? PendingUrl { get; set; }

    // Last known address, kept so the tab can be restored even if its WebView2 died.
    public string? LastUrl { get; set; }

    public bool IsLoading { get; set; }

    // Consecutive renderer crashes without a successful load; stops endless auto-reload.
    public int Crashes { get; set; }

    // Last time this tab was on screen; the memory trimmer unloads the oldest first.
    public DateTime LastShown { get; set; } = DateTime.UtcNow;

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ProfileStore
{
    // Data folder. LIGHTBROWSER_HOME overrides it (portable use, and isolated test runs).
    public static string Root =>
        Environment.GetEnvironmentVariable("LIGHTBROWSER_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightBrowser");

    // One shared WebView2 environment: all profiles live under it and share browser processes.
    public static string DataDir => Path.Combine(Root, "WebView2Data");

    public static string FilePath => Path.Combine(Root, "profiles.json");

    public ObservableCollection<Profile> Profiles { get; } = new();

    // An unreadable profiles.json is moved aside and the backup used; the original is never overwritten
    // by an empty list, so a bad file cannot wipe your profiles.
    public static ProfileStore Load()
    {
        var store = new ProfileStore();
        var list = SafeFile.Read(FilePath, json => JsonSerializer.Deserialize<List<Profile>>(json));
        foreach (var p in list ?? new List<Profile>()) store.Profiles.Add(p);
        return store;
    }

    // False if the disk write failed (full disk, locked file); the in-memory profiles are unaffected.
    public bool Save()
    {
        try
        {
            SafeFile.Write(FilePath, JsonSerializer.Serialize(Profiles, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
