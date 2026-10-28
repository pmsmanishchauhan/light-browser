using System.IO;
using System.Text.Json;

namespace LightBrowser;

public class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Time { get; set; }   // UTC

    // For display: the title, or the address if the page had none.
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayTitle => Title == "" ? Url : Title;

    [System.Text.Json.Serialization.JsonIgnore]
    public string When => Time.ToLocalTime().ToString("d MMM yyyy, HH:mm");
}

// Local browsing history, one file per profile (history\<profile id>.json), never uploaded. Each
// profile only ever sees its own history. Only web pages are recorded, without fragments or
// credentials in the address. The newest 2000 entries per profile are kept.
public class HistoryStore
{
    public const int MaxPerProfile = 2000;

    readonly Dictionary<string, List<HistoryEntry>> _byProfile = new();
    readonly HashSet<string> _dirty = new();

    static string Dir => Path.Combine(ProfileStore.Root, "history");

    static string FileFor(string profileId) =>
        Path.Combine(Dir, new string(profileId.Where(char.IsLetterOrDigit).ToArray()) + ".json");

    List<HistoryEntry> Entries(string profileId)
    {
        if (_byProfile.TryGetValue(profileId, out var list)) return list;
        list = SafeFile.Read(FileFor(profileId), json => JsonSerializer.Deserialize<List<HistoryEntry>>(json))
               ?? new List<HistoryEntry>();
        _byProfile[profileId] = list;
        return list;
    }

    // "https://user:pw@host/p?q=1#frag" -> "https://host/p?q=1". Null for non-web addresses.
    public static string? Clean(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    // Records a finished page load. Reloading the page you are already on only refreshes its entry.
    public void Record(string profileId, string? url, string? title, DateTime? now = null)
    {
        if (Clean(url) is not { } clean) return;
        var list = Entries(profileId);
        var time = now ?? DateTime.UtcNow;
        var title2 = (title ?? "").Trim();

        if (list.Count > 0 && list[^1].Url == clean)
        {
            list[^1].Time = time;
            if (title2 != "") list[^1].Title = title2;
        }
        else
        {
            list.Add(new HistoryEntry { Url = clean, Title = title2, Time = time });
            if (list.Count > MaxPerProfile) list.RemoveRange(0, list.Count - MaxPerProfile);
        }
        _dirty.Add(profileId);
    }

    // Pages usually set their title after loading; attach it to the latest visit of that address.
    public void UpdateTitle(string profileId, string? url, string? title)
    {
        if (Clean(url) is not { } clean || string.IsNullOrWhiteSpace(title)) return;
        var list = Entries(profileId);
        for (var i = list.Count - 1; i >= 0 && i >= list.Count - 20; i--)
            if (list[i].Url == clean)
            {
                if (list[i].Title != title.Trim()) { list[i].Title = title.Trim(); _dirty.Add(profileId); }
                return;
            }
    }

    // Newest first, one row per address, matching the text in the title or address.
    public List<HistoryEntry> Search(string profileId, string query, int max)
    {
        query = query.Trim();
        var seen = new HashSet<string>();
        var result = new List<HistoryEntry>();
        var list = Entries(profileId);
        for (var i = list.Count - 1; i >= 0 && result.Count < max; i--)
        {
            var e = list[i];
            if (query != "" &&
                !e.Url.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !e.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(e.Url)) result.Add(e);
        }
        return result;
    }

    public int Count(string profileId) => Entries(profileId).Count;

    public void Clear(string profileId)
    {
        _byProfile[profileId] = new List<HistoryEntry>();
        _dirty.Remove(profileId);
        try { File.Delete(FileFor(profileId)); File.Delete(FileFor(profileId) + ".bak"); }
        catch (Exception) { }
    }

    // Writes profiles that changed. A disk problem is ignored; history is a convenience.
    public void Flush()
    {
        foreach (var id in _dirty.ToList())
        {
            try
            {
                SafeFile.Write(FileFor(id), JsonSerializer.Serialize(Entries(id)));
                _dirty.Remove(id);
            }
            catch (Exception) { }
        }
    }
}
