namespace LightBrowser;

// One row in the list under the address bar.
//   go      - what Enter does with the typed address
//   search  - what Enter does with typed words
//   history - a page this profile visited (title and address)
//   site    - a site that loaded before, or a profile in the same group uses
//   portal  - a known OTA sign-in page
public sealed record AddressSuggestion(string Kind, string Url, string Title, string Detail)
{
    public string Glyph => Kind switch { "search" => "", "go" => "", "history" => "", _ => "" };
}

public static class AddressSuggestions
{
    public const int Max = 8;
    const int MaxHistory = 4;

    // The first row says what Enter will do. Then pages from this profile's own history (never another profile's),
    // then sites, then OTA portals, each address once.
    public static List<AddressSuggestion> Build(string? typed, string? engineId, IEnumerable<HistoryEntry> history,
                                                IEnumerable<string> sites, IEnumerable<OtaPortal> portals)
    {
        var text = (typed ?? "").Trim();
        var rows = new List<AddressSuggestion>();
        if (text.Length == 0) return rows;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        rows.Add(Primary(text, engineId));
        seen.Add(Key(rows[0].Url));

        foreach (var h in history)
            if (rows.Count(r => r.Kind == "history") < MaxHistory && seen.Add(Key(h.Url)))
                rows.Add(new AddressSuggestion("history", h.Url, h.DisplayTitle, Pretty(h.Url)));

        foreach (var s in sites)
            if (rows.Count < Max && seen.Add(Key(s)))
                rows.Add(new AddressSuggestion("site", s, Pretty(s), "Site"));

        foreach (var p in portals)
            if (rows.Count < Max && seen.Add(Key(p.Url)))
                rows.Add(new AddressSuggestion("portal", p.Url, p.Name, Pretty(p.Url)));

        return rows.Take(Max).ToList();
    }

    static AddressSuggestion Primary(string text, string? engineId)
    {
        var url = SearchEngines.ToUrl(text, engineId);
        if (SearchEngines.IsAddress(text)) return new AddressSuggestion("go", url, "Go to " + Pretty(url), "");

        // A keyword ("ddg cats") picks that engine for this search only; say so, so there is no surprise.
        var byKeyword = SearchEngines.ByKeyword(text, out var typedQuery);
        var engine = byKeyword ?? SearchEngines.Get(engineId);
        var query = byKeyword != null ? typedQuery : text;
        return new AddressSuggestion("search", url, $"Search {engine.Name} for \"{query}\"", "");
    }

    // "https://www.Host.com/path/" -> "host.com/path": what a person reads, not what the browser needs.
    public static string Pretty(string url)
    {
        var s = url.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        return s.TrimEnd('/');
    }

    // Two addresses that differ only by scheme, "www." or a trailing slash are the same row.
    static string Key(string url) => Pretty(url);
}
