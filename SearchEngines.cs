namespace LightBrowser;

// Search engines for the address bar. Typing text that is not an address searches with the chosen engine;
// starting it with an engine's keyword ("ddg hotel rates", "b hotel rates") uses that engine just once.
public sealed record SearchEngine(string Id, string Name, string Template, string[] Keywords)
{
    public string UrlFor(string query) => Template.Replace("{q}", Uri.EscapeDataString(query));
}

public static class SearchEngines
{
    // Private by default. Anyone who picked an engine themselves keeps it (see AppSettings.SearchEngineSetByUser).
    public const string DefaultId = "duckduckgo";

    public static readonly SearchEngine[] All =
    {
        new("google", "Google", "https://www.google.com/search?q={q}", new[] { "g", "google" }),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={q}", new[] { "d", "ddg" }),
        new("bing", "Bing", "https://www.bing.com/search?q={q}", new[] { "b", "bing" }),
        new("brave", "Brave Search", "https://search.brave.com/search?q={q}", new[] { "br", "brave" }),
        new("yahoo", "Yahoo", "https://search.yahoo.com/search?p={q}", new[] { "y", "yahoo" }),
        new("ecosia", "Ecosia", "https://www.ecosia.org/search?q={q}", new[] { "e", "ecosia" }),
        new("startpage", "Startpage", "https://www.startpage.com/do/search?q={q}", new[] { "sp", "startpage" }),
        new("qwant", "Qwant", "https://www.qwant.com/?q={q}", new[] { "q", "qwant" }),
    };

    public static SearchEngine Get(string? id) =>
        All.FirstOrDefault(e => e.Id == id) ?? All.First(e => e.Id == DefaultId);

    public static bool IsKnown(string? id) => All.Any(e => e.Id == id);

    // A profile's own engine wins; an empty or unknown one means "use the app default".
    public static string Effective(string? profileEngineId, string? appEngineId) =>
        IsKnown(profileEngineId) ? profileEngineId! : Get(appEngineId).Id;

    // What the address bar does with typed text: addresses are opened (https:// added when missing),
    // anything else is searched. Returns the URL to open.
    public static string ToUrl(string? input, string? defaultEngineId)
    {
        var text = (input ?? "").Trim();
        if (text.Contains("://") || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return text;

        var space = text.IndexOf(' ');
        if (space > 0 && text[(space + 1)..].Trim() is { Length: > 0 } rest &&
            All.FirstOrDefault(e => e.Keywords.Contains(text[..space], StringComparer.OrdinalIgnoreCase)) is { } byKeyword)
            return byKeyword.UrlFor(rest);

        if (!text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
            return "https://" + text;
        return Get(defaultEngineId).UrlFor(text);
    }
}
