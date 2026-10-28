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

    public Profile() => Tabs.CollectionChanged += (_, _) => Changed(nameof(IsOpen));

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name
    {
        get => _name;
        set { _name = value; Changed(nameof(Name)); }
    }

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
        set { _notes = value; Changed(nameof(Notes)); Changed(nameof(NotesTip)); }
    }

    // In Trash: hidden from the profile list, logins kept until "Delete forever".
    public bool IsDeleted { get; set; }

    [JsonIgnore]
    public string GroupName => string.IsNullOrWhiteSpace(Group) ? "Ungrouped" : Group;

    // Null when empty so the list shows no tooltip.
    [JsonIgnore]
    public string? NotesTip => string.IsNullOrWhiteSpace(Notes) ? null : Notes;

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

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BrowserTab : INotifyPropertyChanged
{
    string _title = "New tab";

    // View is null for a restored tab that has not been opened yet; it costs no memory until selected.
    public WebView2? View { get; set; }

    public string? PendingUrl { get; set; }

    public bool IsLoading { get; set; }

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ProfileStore
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightBrowser");

    // One shared WebView2 environment: all profiles live under it and share browser processes.
    public static string DataDir => Path.Combine(Root, "WebView2Data");

    static string FilePath => Path.Combine(Root, "profiles.json");

    public ObservableCollection<Profile> Profiles { get; } = new();

    public static ProfileStore Load()
    {
        var store = new ProfileStore();
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(FilePath));
                foreach (var p in list ?? new List<Profile>()) store.Profiles.Add(p);
            }
        }
        catch (Exception)
        {
            // Corrupt profiles.json: start empty rather than crash. Cookies stay safe in WebView2Data.
        }
        return store;
    }

    public void Save()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Profiles, new JsonSerializerOptions { WriteIndented = true }));
    }
}
