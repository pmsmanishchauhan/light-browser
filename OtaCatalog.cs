namespace LightBrowser;

public sealed record OtaPortal(string Name, string Url, string Kind, string? Note = null);

// Sign-in pages of OTA extranets and channel managers used by hotels in India, for one-click home pages.
// Only addresses that were opened in this browser's engine and showed a real sign-in page are listed (checked
// 1 Nov 2026 with "LightBrowser.exe --check-sites catalog results.json"). The check loads the page; it cannot
// sign in, so a working page does not prove your own login works. Addresses that did not load were left out
// rather than guessed (see README).
public static class OtaCatalog
{
    public const string Global = "OTA extranets";
    public const string India = "India OTAs";
    public const string Software = "Channel managers and hotel software";

    public static readonly OtaPortal[] All =
    {
        new("Booking.com Extranet", "https://admin.booking.com/", Global),
        new("Expedia Partner Central", "https://www.expediapartnercentral.com/", Global, "Also covers Hotels.com and Vrbo"),
        new("Agoda YCS", "https://ycs.agoda.com/", Global, "Opens the Agoda Partner Portal sign-in"),
        new("Airbnb Hosting", "https://www.airbnb.co.in/hosting", Global),
        new("Trip.com eBooking", "https://ebooking.trip.com/", Global),
        new("Hostelworld Inbox", "https://inbox.hostelworld.com/", Global),
        new("Google Hotel Center", "https://hotelcenter.google.com/", Global,
            "Goes to Google sign-in. Google can refuse sign-in inside embedded browsers; only the sign-in page itself was checked"),

        new("Goibibo / MakeMyTrip Extranet (Ingo-MMT)", "https://in.goibibo.com/newextranet/login", India),
        new("MakeMyTrip Partner", "https://www.makemytrip.com/hotels-partner/", India),

        new("AxisRooms (Hotelogix)", "https://app.axisrooms.com/", Software),
        new("Aiosell", "https://admin.aiosell.com/", Software),
        new("Simplotel", "https://admin.simplotel.com/", Software),
        new("Djubo", "https://app.djubo.com/", Software),
        new("SiteMinder", "https://app.siteminder.com/web/login", Software),
        new("Little Hotelier", "https://app.littlehotelier.com/", Software),
        new("Cloudbeds", "https://hotels.cloudbeds.com/login", Software),
        new("HotelRunner", "https://app.hotelrunner.com/login", Software),
        new("Beds24", "https://beds24.com/control3.php", Software),
        new("WuBook", "https://wubook.net/wauth/signin", Software),
    };

    // Portals whose name or address contains the typed text, name matches first. Used by the address bar.
    public static List<string> Matching(string query, int max)
    {
        query = query.Trim();
        if (query.Length < 2) return new List<string>();
        return All
            .Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .Select(p => p.Url)
            .ToList();
    }
}
