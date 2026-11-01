using System.IO;

namespace LightBrowser;

// Blocks requests to known ad and tracking domains inside the browser (a content blocker, not a system
// firewall). A request is blocked when its host, or any parent domain of it, is on the list. The list is a short
// built-in set plus your own blocklist.txt (one domain per line; "0.0.0.0 domain" hosts-file lines and
// "||domain^" lines are accepted, "#" starts a comment).
public sealed class RequestBlocker
{
    // Whole-domain ad networks and trackers that pages do not need to work. Deliberately short and
    // conservative: sites that also serve real content (google.com, facebook.com...) are not on it.
    public static readonly string[] BuiltIn =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com", "google-analytics.com",
        "2mdn.net", "adnxs.com", "adsrvr.org", "advertising.com", "amazon-adsystem.com", "criteo.com", "criteo.net",
        "taboola.com", "outbrain.com", "rubiconproject.com", "pubmatic.com", "openx.net", "casalemedia.com",
        "indexww.com", "smartadserver.com", "moatads.com", "scorecardresearch.com", "quantserve.com", "quantcount.com",
        "bluekai.com", "demdex.net", "krxd.net", "everesttech.net", "mathtag.com", "rlcdn.com", "adform.net",
        "sharethrough.com", "teads.tv", "yieldmo.com", "media.net", "bidswitch.net", "contextweb.com", "lijit.com",
        "33across.com", "tapad.com", "agkn.com", "hotjar.com", "hotjar.io", "mouseflow.com", "crazyegg.com",
        "mixpanel.com", "clarity.ms", "ads-twitter.com", "adsymptotic.com", "serving-sys.com", "zedo.com",
    };

    readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    // Requests blocked since the app started.
    public int Blocked { get; set; }

    public int Count => _domains.Count;

    public RequestBlocker(IEnumerable<string>? userLines = null)
    {
        foreach (var d in BuiltIn) _domains.Add(d);
        foreach (var line in userLines ?? Enumerable.Empty<string>())
            if (ParseLine(line) is { } domain) _domains.Add(domain);
    }

    public static string FilePath => Path.Combine(ProfileStore.Root, "blocklist.txt");

    const string Template =
        "# Your own blocked domains for LightBrowser, one per line. A domain also blocks its subdomains.\r\n" +
        "# Lines starting with # are ignored. Hosts-file lines (0.0.0.0 example.com) and ||example.com^ also work.\r\n" +
        "# Restart the app after editing.\r\n";

    // Reads blocklist.txt (and creates it with instructions the first time, so it is easy to find).
    public static RequestBlocker Load()
    {
        try
        {
            if (!File.Exists(FilePath)) { Directory.CreateDirectory(ProfileStore.Root); File.WriteAllText(FilePath, Template); }
            return new RequestBlocker(File.ReadLines(FilePath).ToList());
        }
        catch (Exception)
        {
            return new RequestBlocker();   // unreadable list: built-in set only
        }
    }

    // True when the URL's host, or a parent domain of it, is on the list. Never true for a bare TLD.
    public bool IsBlocked(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.Host.TrimEnd('.');
        while (host.Contains('.'))
        {
            if (_domains.Contains(host)) return true;
            host = host[(host.IndexOf('.') + 1)..];
        }
        return false;
    }

    // One list line to a domain, or null for comments, blanks and anything that is not a domain.
    public static string? ParseLine(string? line)
    {
        var text = (line ?? "").Trim();
        var hash = text.IndexOf('#');
        if (hash >= 0) text = text[..hash].Trim();
        if (text == "") return null;

        if (text.StartsWith("||"))   // ||example.com^ style
        {
            text = text[2..];
            var end = text.IndexOfAny(new[] { '^', '/', '$' });
            if (end >= 0) text = text[..end];
        }
        else
        {
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out _)) text = parts[1];   // hosts file line
            else if (parts.Length != 1) return null;
        }

        text = text.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.StartsWith("*.")) text = text[2..];
        if (text.Contains('.') && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
            !text.StartsWith('.') && !text.Contains("..") && !System.Net.IPAddress.TryParse(text, out _))
            return text;
        return null;
    }
}
