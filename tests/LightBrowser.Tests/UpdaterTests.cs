using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class UpdaterTests : DataFolderTest
{
    const string ApiVariable = "LIGHTBROWSER_UPDATE_API";

    static string Release(string tag = "v0.5.0", bool prerelease = false, bool draft = false, string? digest = null, long size = 1234,
                          string small = "LightBrowser-small.exe", string standalone = "LightBrowser-standalone.exe")
    {
        var digestJson = digest == null ? "" : $", \"digest\": \"{digest}\"";
        return $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/x/y/releases/tag/{{tag}}", "prerelease": {{prerelease.ToString().ToLower()}}, "draft": {{draft.ToString().ToLower()}},
          "assets": [
            { "name": "{{small}}", "size": {{size}}, "browser_download_url": "https://github.com/x/y/releases/download/{{tag}}/{{small}}"{{digestJson}} },
            { "name": "{{standalone}}", "size": 70000000, "browser_download_url": "https://github.com/x/y/releases/download/{{tag}}/{{standalone}}"{{digestJson}} },
            { "name": "notes.txt", "size": 5, "browser_download_url": "https://github.com/x/y/releases/download/{{tag}}/notes.txt" } ] }
        """;
    }

    static readonly string Sha64 = new('a', 64);

    // ---- versions ----

    [Theory]
    [InlineData("v0.3.1", "0.3.1")]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("0.4.0+abc123", "0.4.0")]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData("1.0", "1.0.0")]
    [InlineData(" v2.10.0 ", "2.10.0")]
    public void Versions_are_read_from_tags(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), Updater.ParseVersion(text));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData(null)]
    [InlineData("1")]
    public void Anything_else_is_not_a_version(string? text) => Assert.Null(Updater.ParseVersion(text));

    [Fact]
    public void Newer_versions_compare_as_newer_including_double_digits()
    {
        Assert.True(Updater.ParseVersion("v0.10.0")! > Updater.ParseVersion("v0.9.9")!);
        Assert.True(Updater.ParseVersion("v1.0")! == Updater.ParseVersion("1.0.0")!);
        Assert.False(Updater.ParseVersion("v0.3.1")! > Updater.ParseVersion("0.4.0")!);
    }

    // ---- reading the release ----

    [Fact]
    public void The_small_build_gets_the_small_exe_and_the_standalone_build_the_standalone_exe()
    {
        var small = Updater.ParseRelease(Release(digest: "sha256:" + Sha64), standalone: false)!;
        var big = Updater.ParseRelease(Release(digest: "sha256:" + Sha64), standalone: true)!;

        Assert.Equal("LightBrowser-small.exe", small.AssetName);
        Assert.Equal("LightBrowser-standalone.exe", big.AssetName);
        Assert.Equal(new Version(0, 5, 0), small.Version);
        Assert.Equal("v0.5.0", small.Tag);
        Assert.Equal(Sha64, small.Sha256);
        Assert.Equal(1234, small.Size);
        Assert.EndsWith("LightBrowser-small.exe", small.DownloadUrl);
        Assert.StartsWith("https://github.com/", small.PageUrl);
    }

    [Fact]
    public void A_release_without_a_checksum_is_still_read_but_has_none()
    {
        var r = Updater.ParseRelease(Release(digest: null), standalone: false)!;
        Assert.Null(r.Sha256);

        Assert.Null(Updater.ParseRelease(Release(digest: "md5:abc"), false)!.Sha256);
        Assert.Null(Updater.ParseRelease(Release(digest: "sha256:tooshort"), false)!.Sha256);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Drafts_and_pre_releases_are_ignored(bool prerelease, bool draft) =>
        Assert.Null(Updater.ParseRelease(Release(prerelease: prerelease, draft: draft), false));

    [Fact]
    public void A_release_without_a_matching_exe_or_with_odd_json_gives_nothing()
    {
        Assert.Null(Updater.ParseRelease(Release(small: "other.zip", standalone: "another.zip"), false));
        Assert.Null(Updater.ParseRelease(Release(tag: "nightly"), false));
        Assert.Null(Updater.ParseRelease("not json", false));
        Assert.Null(Updater.ParseRelease("{}", false));
        Assert.Null(Updater.ParseRelease("[]", false));
        Assert.Null(Updater.ParseRelease("", false));
    }

    // ---- where files may come from ----

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/a.exe", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/o/r/a.exe", false)]                 // not encrypted
    [InlineData("https://github.com.evil.example/a.exe", false)]       // lookalike
    [InlineData("https://evilgithub.com/a.exe", false)]
    [InlineData("https://example.com/a.exe", false)]
    [InlineData("file:///C:/a.exe", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Only_GitHub_over_https_is_a_download_source(string url, bool allowed) =>
        Assert.Equal(allowed, Updater.IsAllowedDownload(url));

    // ---- checking a downloaded file ----

    static (string Path, ReleaseInfo Release) MakeFile(Func<byte[], byte[]>? tamper = null, string? shaOverride = null, long? sizeOverride = null)
    {
        var bytes = new byte[5000];
        new Random(7).NextBytes(bytes);
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (tamper != null) bytes = tamper(bytes);
        var path = System.IO.Path.GetTempFileName();
        File.WriteAllBytes(path, bytes);
        return (path, new ReleaseInfo("v9.0.0", new Version(9, 0, 0), "https://github.com/x", "LightBrowser-small.exe", "https://github.com/x/a.exe",
                                      sizeOverride ?? 5000, shaOverride ?? sha));
    }

    [Fact]
    public void A_file_that_matches_passes()
    {
        var (path, release) = MakeFile();
        try { Updater.Verify(path, release); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_changed_byte_a_wrong_size_a_wrong_header_or_a_missing_checksum_is_refused()
    {
        foreach (var make in new Func<(string, ReleaseInfo)>[]
        {
            () => MakeFile(b => { b[3000] ^= 1; return b; }),                         // one flipped byte
            () => MakeFile(sizeOverride: 4999),                                       // size differs
            () => MakeFile(b => { b[0] = (byte)'X'; return b; }, shaOverride: null),  // not an exe (and checksum of the original)
            () => MakeFile(b => b[..500]),                                            // too small
        })
        {
            var (path, release) = make();
            try { Assert.Throws<InvalidDataException>(() => Updater.Verify(path, release)); }
            finally { File.Delete(path); }
        }

        var (p2, r2) = MakeFile();
        try { Assert.Throws<InvalidDataException>(() => Updater.Verify(p2, r2 with { Sha256 = null })); }
        finally { File.Delete(p2); }
    }

    // ---- downloading, against a local server ----

    sealed class FileServer : IDisposable
    {
        readonly HttpListener _l = new();
        public string Url { get; }
        public FileServer(byte[] body)
        {
            var port = Random.Shared.Next(30000, 40000);
            Url = $"http://localhost:{port}/";
            _l.Prefixes.Add(Url);
            _l.Start();
            Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    try
                    {
                        var ctx = await _l.GetContextAsync();
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch (Exception) { }
                }
            });
        }
        public void Dispose() => _l.Close();
    }

    [Fact]
    public async Task Download_saves_reports_progress_and_verifies()
    {
        var (source, release) = MakeFile();
        var bytes = File.ReadAllBytes(source);
        File.Delete(source);
        using var server = new FileServer(bytes);
        Environment.SetEnvironmentVariable(ApiVariable, server.Url);
        var dest = System.IO.Path.Combine(Dir, "new.exe");
        var last = 0.0;
        try
        {
            await Updater.DownloadAsync(release with { DownloadUrl = server.Url + "a.exe" }, dest, new Progress<double>(p => last = p));

            Assert.Equal(bytes, File.ReadAllBytes(dest));
            await Task.Delay(100);
            Assert.Equal(1.0, last, 3);
        }
        finally { Environment.SetEnvironmentVariable(ApiVariable, null); }
    }

    [Fact]
    public async Task A_download_that_fails_the_checksum_is_deleted_and_never_kept()
    {
        var (source, release) = MakeFile();
        var bytes = File.ReadAllBytes(source);
        File.Delete(source);
        bytes[4000] ^= 0xFF;   // the server sends a different file than GitHub described
        using var server = new FileServer(bytes);
        Environment.SetEnvironmentVariable(ApiVariable, server.Url);
        var dest = System.IO.Path.Combine(Dir, "new.exe");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Updater.DownloadAsync(release with { DownloadUrl = server.Url + "a.exe" }, dest, null));
            Assert.False(File.Exists(dest));
        }
        finally { Environment.SetEnvironmentVariable(ApiVariable, null); }
    }

    [Fact]
    public async Task Without_the_test_override_only_GitHub_addresses_download()
    {
        var (source, release) = MakeFile();
        File.Delete(source);
        var dest = System.IO.Path.Combine(Dir, "new.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Updater.DownloadAsync(release with { DownloadUrl = "https://example.com/a.exe" }, dest, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Updater.DownloadAsync(release with { Sha256 = null }, dest, null));
        Assert.False(File.Exists(dest));
    }

    // ---- replacing the running exe ----

    [Fact]
    public void A_running_exe_can_be_replaced_and_the_old_copy_removed_once_it_exits()
    {
        var exe = System.IO.Path.Combine(Dir, "app.exe");
        File.Copy(System.IO.Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        var update = System.IO.Path.Combine(Dir, "app.exe.new");
        File.WriteAllText(update, "NEW VERSION");

        using var running = Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            Thread.Sleep(300);
            var old = Updater.Swap(exe, update);

            Assert.Equal("NEW VERSION", File.ReadAllText(exe));
            Assert.True(File.Exists(old));
            Assert.False(File.Exists(update));
            Assert.False(running.HasExited);   // the old version kept running through the swap
        }
        finally { running.Kill(); running.WaitForExit(); }

        Updater.CleanupOld(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.Equal("NEW VERSION", File.ReadAllText(exe));
    }

    [Fact]
    public void A_failed_swap_puts_the_current_exe_back()
    {
        var exe = System.IO.Path.Combine(Dir, "app.exe");
        File.WriteAllText(exe, "CURRENT");

        Assert.ThrowsAny<Exception>(() => Updater.Swap(exe, System.IO.Path.Combine(Dir, "missing.new")));

        Assert.Equal("CURRENT", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void A_leftover_old_copy_from_an_earlier_update_does_not_block_the_next_one()
    {
        var exe = System.IO.Path.Combine(Dir, "app.exe");
        File.WriteAllText(exe, "V2");
        File.WriteAllText(exe + ".old", "V1");
        var update = System.IO.Path.Combine(Dir, "app.exe.new");
        File.WriteAllText(update, "V3");

        Updater.Swap(exe, update);

        Assert.Equal("V3", File.ReadAllText(exe));
        Assert.Equal("V2", File.ReadAllText(exe + ".old"));
    }

    [Fact]
    public void Only_a_lone_exe_in_a_writable_folder_updates_itself()
    {
        var exe = System.IO.Path.Combine(Dir, "LightBrowser.exe");
        File.WriteAllText(exe, "x");
        Assert.True(Updater.CanSelfUpdate(exe));
        Assert.False(File.Exists(exe + ".write-test"));

        File.WriteAllText(System.IO.Path.Combine(Dir, "LightBrowser.dll"), "x");   // a development build
        Assert.False(Updater.CanSelfUpdate(exe));

        Assert.False(Updater.CanSelfUpdate(System.IO.Path.Combine(Dir, "nothing.exe")));
        Assert.False(Updater.CanSelfUpdate(null));
    }

    [Fact]
    public void The_flavour_is_told_apart_by_size()
    {
        var small = System.IO.Path.Combine(Dir, "small.exe");
        File.WriteAllBytes(small, new byte[2048]);
        Assert.False(Updater.IsStandalone(small));

        var big = System.IO.Path.Combine(Dir, "big.exe");
        using (var f = File.Create(big)) f.SetLength(40L * 1024 * 1024);
        Assert.True(Updater.IsStandalone(big));
        Assert.False(Updater.IsStandalone(System.IO.Path.Combine(Dir, "missing.exe")));
    }

    [Fact]
    public void The_build_has_a_version_the_button_can_compare()
    {
        var version = typeof(ReleaseInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;

        Assert.NotNull(Updater.ParseVersion(version));
        Assert.True(Updater.ParseVersion(version)! >= new Version(0, 4, 0));
    }
}
