using System.Globalization;

namespace LightBrowser;

// Suggests names for a new profile from its group, its first home page and the names already taken.
public static class NameSuggester
{
    public static List<string> Suggest(string group, IReadOnlyList<string> homeUrls, IEnumerable<string> existingNames, int max = 3)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string name)
        {
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
        }

        group = group.Trim();
        if (group != "") Add(Numbered(group, taken, from: 1));                       // Hilton 3
        if (SiteName(homeUrls) is { } site) Add(Unique(site + " account", taken));   // Booking account
        Add(Numbered("Client", taken, from: 1));                                     // Client 7
        return result.Take(max).ToList();
    }

    // "Hilton" -> "Hilton 1", or the first free number if some are taken.
    static string Numbered(string baseName, HashSet<string> taken, int from)
    {
        var n = from;
        while (taken.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    // The plain name if free, otherwise "name 2", "name 3"...
    static string Unique(string name, HashSet<string> taken) =>
        taken.Contains(name) ? Numbered(name, taken, from: 2) : name;

    // admin.booking.com -> "Booking"; extranet.agoda.com -> "Agoda"; example.co.uk -> "Example".
    static string? SiteName(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns) continue;
            var parts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var label = parts.Length >= 3 && parts[^1].Length == 2 && parts[^2].Length <= 3 ? parts[^3] : parts[^2];
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label);
        }
        return null;
    }
}
