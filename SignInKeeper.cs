using Microsoft.Web.WebView2.Core;

namespace LightBrowser;

// Most extranets (Expedia, Booking.com, ...) keep you signed in with "session" cookies: cookies without an expiry.
// Chromium throws those away when the last page of a profile closes and when the app exits, so every restart, and
// every idle tab that LightBrowser unloads to save RAM, signed you out. This gives them an expiry, which makes them
// ordinary cookies that are saved with the profile's other data. The site still decides when its own login ends:
// a logout or an expired server session works exactly as before.
public static class SignInKeeper
{
    public const int KeepDays = 30;

    // Session cookies are kept, except those of ad and tracker domains (they are blocked anyway, and there is no
    // reason to make them last longer). "Domain" is the cookie's domain, with or without the leading dot.
    public static bool ShouldKeep(bool isSession, string? domain, Func<string, bool>? isBlockedUrl)
    {
        if (!isSession) return false;
        var host = (domain ?? "").TrimStart('.');
        if (host == "") return false;
        return isBlockedUrl == null || !isBlockedUrl("https://" + host + "/");
    }

    // Gives the profile's session cookies an expiry. Returns how many it changed. Never throws: a page that is
    // closing, or an engine that is going away, just means nothing was changed this time.
    public static async Task<int> KeepAsync(CoreWebView2 web, Func<string, bool>? isBlockedUrl, DateTime? now = null)
    {
        try
        {
            var manager = web.CookieManager;
            var cookies = await manager.GetCookiesAsync(null);   // null: every cookie of this profile
            var expires = (now ?? DateTime.Now).AddDays(KeepDays);
            var changed = 0;
            foreach (var cookie in cookies)
            {
                if (!ShouldKeep(cookie.IsSession, cookie.Domain, isBlockedUrl)) continue;
                var kept = manager.CopyCookie(cookie);
                kept.Expires = expires;
                manager.AddOrUpdateCookie(kept);
                changed++;
            }
            return changed;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
