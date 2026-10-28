using System.IO;
using System.Text.Json;

namespace LightBrowser;

// Common suggested websites: sites that loaded successfully in any tab. Stored only on this PC
// and shared by every group; each group's own sites are derived separately (ForGroup) from its
// profiles' Home pages. Only the origin (scheme + host + non-default port) is kept, never paths
// or query strings.
public class SuggestionStore
{
    const int Max = 200;

    static string FilePath => Path.Combine(ProfileStore.Root, "suggestions.json");

    readonly List<string> _sites = new();

    // Oldest first.
    public IReadOnlyList<string> Sites => _sites;

    public static SuggestionStore Load()
    {
        var store = new SuggestionStore();
        var saved = SafeFile.Read(FilePath, json => JsonSerializer.Deserialize<List<string>>(json));
        foreach (var site in saved ?? new List<string>())
            if (Normalize(site) is { } origin && !store.Contains(origin)) store._sites.Add(origin);
        return store;
    }

    // "https://Host:443/path?x=1" -> "https://host". Null for anything that is not a web address.
    public static string? Normalize(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.Scheme + "://" + uri.Authority;
    }

    // A site counts as working only when the top-level page finished loading and the final status,
    // after redirects, is 2xx. Network, TLS or timeout errors, aborted loads, and 3xx/4xx/5xx
    // (including 401/403 "login required" responses) are not verification.
    public static bool IsVerified(bool navigationSucceeded, int httpStatus) =>
        navigationSucceeded && httpStatus is >= 200 and < 300;

    bool Contains(string origin) => _sites.Contains(origin, StringComparer.OrdinalIgnoreCase);

    // Returns true when the site was new and has been added.
    public bool Add(string? url)
    {
        if (Normalize(url) is not { } origin || Contains(origin)) return false;
        _sites.Add(origin);
        if (_sites.Count > Max) _sites.RemoveAt(0);
        Save();
        return true;
    }

    public bool AddIfVerified(string? url, bool navigationSucceeded, int httpStatus) =>
        IsVerified(navigationSucceeded, httpStatus) && Add(url);

    // Sites the other profiles in a group use as Home pages. Separate from the common list.
    public static List<string> ForGroup(string group, IEnumerable<Profile> profiles, Profile? except = null)
    {
        group = group.Trim();
        if (group == "") return new List<string>();
        return profiles
            .Where(p => p != except && !p.IsDeleted && string.Equals(p.Group.Trim(), group, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.HomeUrls)
            .Select(Normalize).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Sites whose host contains the typed text, host-prefix matches first.
    public List<string> Matching(string query, int max)
    {
        query = query.Trim();
        var scheme = query.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) query = query[(scheme + 3)..];
        if (query.Length == 0) return new List<string>();

        return _sites
            .Select(s => (Site: s, Host: s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..]))
            .Where(x => x.Host.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Host.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                                    || x.Host.StartsWith("www." + query, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .Select(x => x.Site)
            .ToList();
    }

    void Save()
    {
        try
        {
            SafeFile.Write(FilePath, JsonSerializer.Serialize(_sites));
        }
        catch (Exception)
        {
            // Disk problem: the list still works for this run, it just will not persist.
        }
    }
}
