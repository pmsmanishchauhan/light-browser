using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LightBrowser;

// One release on GitHub, and the file of it that suits this install.
public sealed record ReleaseInfo(string Tag, Version Version, string PageUrl, string AssetName, string DownloadUrl, long Size, string? Sha256);

// The Update button. Asks GitHub for the latest release, downloads the matching exe only from GitHub over
// HTTPS, checks it against the SHA-256 GitHub published for that file, then swaps it in place of the running
// exe (Windows lets a running exe be renamed) and restarts. Profiles, logins and settings are separate files
// and are not touched. Nothing is contacted until you press the button.
public static class Updater
{
    const string DefaultApi = "https://api.github.com/repos/pmsmanishchauhan/light-browser/releases/latest";

    // A build that carries the .NET runtime is about 68 MB, the small one about 1 MB.
    const long StandaloneThresholdBytes = 30L * 1024 * 1024;

    // Tests point this at a local server. Without it, only GitHub download addresses are accepted.
    const string ApiOverrideVariable = "LIGHTBROWSER_UPDATE_API";

    static bool Overridden => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiOverrideVariable));

    public static string ApiUrl => Overridden ? Environment.GetEnvironmentVariable(ApiOverrideVariable)! : DefaultApi;

    public static Version CurrentVersion =>
        ParseVersion(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0);

    // "v0.3.1", "0.3.1+abc123" or "1.2.3-beta" -> 0.3.1 / 1.2.3. Null when it is not a version.
    public static Version? ParseVersion(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(new[] { '+', '-' });
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));   // 1.0 and 1.0.0 are the same release
    }

    public static bool IsStandalone(string exePath) =>
        File.Exists(exePath) && new FileInfo(exePath).Length > StandaloneThresholdBytes;

    // Only a single-file exe in a folder we can write to updates itself. A development build (exe next to
    // its .dll) or an exe somewhere read-only is updated by hand from the release page.
    public static bool CanSelfUpdate(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        if (File.Exists(Path.ChangeExtension(exePath, ".dll"))) return false;
        try
        {
            var probe = exePath + ".write-test";
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The newest stable release in the GitHub API's JSON, with the asset for this install ("standalone" or
    // "small"). Null for a draft or pre-release, or when the release has no matching exe.
    public static ReleaseInfo? ParseRelease(string json, bool standalone)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (ParseVersion(tag) is not { } version) return null;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            var wanted = standalone ? "standalone" : "small";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    !name.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = null;
                if (asset.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64)
                    sha = digest[7..].ToLowerInvariant();
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                return new ReleaseInfo(tag, version, page, name, asset.GetProperty("browser_download_url").GetString() ?? "", size, sha);
            }
            return null;
        }
        catch (Exception)
        {
            return null;   // not the JSON we expect
        }
    }

    // True when the address may be downloaded from: GitHub over HTTPS (or anything, in a test run).
    public static bool IsAllowedDownload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (Overridden) return u.Scheme is "http" or "https";
        return u.Scheme == "https" && (u.Host == "github.com" || u.Host.EndsWith(".github.com") ||
                                       u.Host.EndsWith(".githubusercontent.com"));
    }

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LightBrowser/" + CurrentVersion);   // GitHub requires one
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    // The latest release (whether or not it is newer: the caller compares), or null if there is none.
    public static async Task<ReleaseInfo?> CheckAsync(bool standalone, CancellationToken ct = default)
    {
        using var http = NewClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var json = await http.GetStringAsync(ApiUrl, cts.Token);
        return ParseRelease(json, standalone);
    }

    // Downloads to destPath and checks size, "MZ" header and SHA-256. Deletes the file if anything is wrong.
    public static async Task DownloadAsync(ReleaseInfo release, string destPath, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!IsAllowedDownload(release.DownloadUrl)) throw new InvalidOperationException("The download address is not a GitHub address.");
        if (release.Sha256 == null) throw new InvalidOperationException("GitHub published no checksum for this file.");
        try
        {
            using var http = NewClient();
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(destPath))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            Verify(destPath, release);
        }
        catch
        {
            try { File.Delete(destPath); } catch (Exception) { }
            throw;
        }
    }

    // Throws when the file is not the one GitHub described.
    public static void Verify(string path, ReleaseInfo release)
    {
        var info = new FileInfo(path);
        if (info.Length < 1024) throw new InvalidDataException("The downloaded file is too small to be the app.");
        if (release.Size > 0 && info.Length != release.Size) throw new InvalidDataException("The downloaded file is not the expected size.");
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("The downloaded file is not a Windows program.");
        stream.Position = 0;
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (release.Sha256 == null || actual != release.Sha256)
            throw new InvalidDataException("The downloaded file does not match the checksum GitHub published for it.");
    }

    // Puts newExe where currentExe is. The running exe is renamed to "<name>.old" (allowed while it runs), so a
    // failure part-way puts it back. Returns the .old path.
    public static string Swap(string currentExe, string newExe)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Move(newExe, currentExe);
        }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }
        return old;
    }

    // Removes the previous version left by the last update (it could not be deleted while it was running).
    public static void CleanupOld(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        try { File.Delete(exePath + ".old"); } catch (Exception) { }
        try { File.Delete(exePath + ".new"); } catch (Exception) { }
    }

    // Starts the app again once this process has exited (so the two never share the data folder).
    public static void RestartAfterExit(string exePath) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exePath}\"")
        {
            CreateNoWindow = true, UseShellExecute = false,
        });
}
