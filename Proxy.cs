using System.Security.Cryptography;
using System.Text;

namespace LightBrowser;

// A proxy server a profile sends all its traffic through, so each client account can come from its own IP.
// Accepted forms: "host:port" (HTTP), or "http://", "https://", "socks4://", "socks5://" followed by host:port.
// Sign-in details are kept apart from the address (see Profile.ProxyUser / ProxyPassword).
public sealed record ProxySpec(string Server, string Host, int Port)
{
    static readonly string[] Schemes = { "http", "https", "socks4", "socks5" };

    // Null for empty or unusable text. The result is normalised: lower case, scheme always present.
    public static ProxySpec? Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw == "" || raw.Contains(' ') || raw.Contains('@')) return null;
        raw = raw.TrimEnd('/');
        if (!raw.Contains("://")) raw = "http://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (!Schemes.Contains(uri.Scheme) || uri.Host == "" || uri.IsDefaultPort && !raw.EndsWith(":" + uri.Port)) return null;
        if (uri.PathAndQuery != "/" || uri.Fragment != "") return null;
        var host = uri.Host.Trim('[', ']').ToLowerInvariant();   // .NET keeps the brackets of an IPv6 literal
        var shown = host.Contains(':') ? "[" + host + "]" : host;   // IPv6 literal
        return new ProxySpec($"{uri.Scheme}://{shown}:{uri.Port}", host, uri.Port);
    }

    // Each distinct proxy runs its own browser engine (the proxy is an engine-wide setting), kept in its own
    // data folder. A profile's logins live in that folder, so they stay with the proxy they were made through.
    public string FolderName
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Server));
            return "WebView2Data-px-" + Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
        }
    }

    // True when a sign-in request comes from this proxy and not from a website.
    public bool IsChallengeFrom(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        string.Equals(u.Host.Trim('[', ']'), Host, StringComparison.OrdinalIgnoreCase) && u.Port == Port;

    // Chromium switches for the engine. A proxied engine also stops WebRTC from sending UDP outside the
    // proxy, which would otherwise show the real address to a website.
    public static string EngineArguments(ProxySpec? proxy, AppSettings settings)
    {
        var extra = !string.IsNullOrWhiteSpace(settings.BrowserArguments) ? settings.BrowserArguments.Trim()
            : settings.Mode == AppSettings.LowMemory ? "--disable-gpu" : "";
        if (proxy == null) return extra;
        return $"--proxy-server=\"{proxy.Server}\" --force-webrtc-ip-handling-policy=disable_non_proxied_udp {extra}".Trim();
    }
}

// The proxy password is stored encrypted for this Windows account (DPAPI) and bound to the profile id,
// never as plain text in profiles.json.
public static class ProxySecret
{
    static byte[] Entropy(string profileId) => Encoding.UTF8.GetBytes("LightBrowser.proxy.v1." + profileId);

    public static string Protect(string profileId, string password) =>
        password == "" ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy(profileId), DataProtectionScope.CurrentUser));

    // Empty when there is none or it cannot be read (another Windows user, damaged value).
    public static string Unprotect(string profileId, string stored)
    {
        if (stored == "") return "";
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy(profileId), DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return "";
        }
    }
}
