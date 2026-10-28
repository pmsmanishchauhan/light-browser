using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightBrowser;

public class VaultEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Site { get; set; } = "";       // address or host, e.g. admin.booking.com
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.UtcNow;

    // Host of Site, lower case, without "www.". Empty if Site is not an address.
    [System.Text.Json.Serialization.JsonIgnore]
    public string Host => PasswordVault.HostOf(Site);

    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => Username == "" ? Host : $"{Host}   -   {Username}";
}

// One encrypted password list per profile, stored only on this PC (vault\<profile id>.vault). The file is
// encrypted with Windows DPAPI for your Windows account and bound to its profile, so another Windows user
// cannot read it, and a vault copied onto a different profile cannot be opened. This is separate from the
// browser's own "Save password?" prompt (which stays inside each profile's storage and cannot be listed).
public static class PasswordVault
{
    static string Dir => Path.Combine(ProfileStore.Root, "vault");

    static string FileFor(string profileId) =>
        Path.Combine(Dir, new string(profileId.Where(char.IsLetterOrDigit).ToArray()) + ".vault");

    static byte[] Entropy(string profileId) => Encoding.UTF8.GetBytes("LightBrowser.vault.v1." + profileId);

    // An unreadable vault (other Windows user, damaged file) is moved aside rather than overwritten.
    public static List<VaultEntry> Load(string profileId) =>
        SafeFile.Read(FileFor(profileId), text =>
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(text), Entropy(profileId), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<VaultEntry>>(Encoding.UTF8.GetString(plain));
        }) ?? new List<VaultEntry>();

    public static bool Save(string profileId, IEnumerable<VaultEntry> entries)
    {
        try
        {
            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries.ToList()));
            var protectedBytes = ProtectedData.Protect(plain, Entropy(profileId), DataProtectionScope.CurrentUser);
            SafeFile.Write(FileFor(profileId), Convert.ToBase64String(protectedBytes));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Delete(string profileId)
    {
        try { File.Delete(FileFor(profileId)); File.Delete(FileFor(profileId) + ".bak"); }
        catch (Exception) { }
    }

    // "https://www.Admin.Booking.com/x" or "admin.booking.com" -> "admin.booking.com".
    public static string HostOf(string? site)
    {
        var text = (site ?? "").Trim();
        if (text == "") return "";
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    // Entries for the page's host: the same host, or a parent/child domain of it.
    public static List<VaultEntry> ForHost(IEnumerable<VaultEntry> entries, string? pageUrl)
    {
        var host = HostOf(pageUrl);
        if (host == "") return new List<VaultEntry>();
        return entries.Where(e => e.Host != "" &&
            (e.Host == host || host.EndsWith("." + e.Host) || e.Host.EndsWith("." + host))).ToList();
    }

    public static string Generate(int length = 18)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*-_";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    // JavaScript that fills the visible login form of the top page: the password field, and the last
    // visible text/email field before it. Values are passed as JSON string literals, so quotes, slashes or
    // "</script>" in a password cannot break out of the script. Returns "ok" or "no-password-field".
    public static string FillScript(string username, string password) =>
        "(function(u, p) {" +
        " function visible(e) { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(e).visibility !== 'hidden'; }" +
        " function setVal(el, v) { const d = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value'); d.set.call(el, v);" +
        "   el.dispatchEvent(new Event('input', {bubbles: true})); el.dispatchEvent(new Event('change', {bubbles: true})); }" +
        " const pw = Array.from(document.querySelectorAll('input[type=password]')).find(visible);" +
        " if (!pw) return 'no-password-field';" +
        " const scope = pw.form || document;" +
        " let user = null;" +
        " for (const i of scope.querySelectorAll('input')) {" +
        "   if (i === pw || !visible(i) || !['text', 'email', 'tel', ''].includes(i.type || '')) continue;" +
        "   if (i.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING) user = i; }" +
        " if (u && user) setVal(user, u);" +
        " setVal(pw, p);" +
        " return 'ok'; })(" + JsonSerializer.Serialize(username) + ", " + JsonSerializer.Serialize(password) + ")";
}
