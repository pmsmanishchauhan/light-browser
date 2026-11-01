using System.IO;
using System.Text.Json;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class ProxyTests : DataFolderTest
{
    [Theory]
    [InlineData("1.2.3.4:8080", "http://1.2.3.4:8080", "1.2.3.4", 8080)]
    [InlineData("  Proxy.Example.com:3128/ ", "http://proxy.example.com:3128", "proxy.example.com", 3128)]
    [InlineData("http://h:80", "http://h:80", "h", 80)]
    [InlineData("https://h:443", "https://h:443", "h", 443)]
    [InlineData("socks5://h:1080", "socks5://h:1080", "h", 1080)]
    [InlineData("SOCKS4://h:1080", "socks4://h:1080", "h", 1080)]
    [InlineData("http://[::1]:8080", "http://[::1]:8080", "::1", 8080)]
    public void Good_addresses_are_normalised(string text, string server, string host, int port)
    {
        var spec = ProxySpec.Parse(text);

        Assert.NotNull(spec);
        Assert.Equal(server, spec!.Server);
        Assert.Equal(host, spec.Host);
        Assert.Equal(port, spec.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hostonly")]                  // a port is required
    [InlineData("http://host")]
    [InlineData("socks5://host")]
    [InlineData("user:pass@host:8080")]       // the login has its own fields
    [InlineData("http://user@host:8080")]
    [InlineData("ftp://host:21")]
    [InlineData("host:8080/path")]
    [InlineData("two words:8080")]
    [InlineData("host:99999")]
    public void Unusable_addresses_are_rejected(string? text) => Assert.Null(ProxySpec.Parse(text));

    [Fact]
    public void The_same_proxy_always_gets_the_same_data_folder_and_different_ones_differ()
    {
        var a = ProxySpec.Parse("1.1.1.1:80")!;
        var again = ProxySpec.Parse("http://1.1.1.1:80/")!;
        var b = ProxySpec.Parse("1.1.1.2:80")!;

        Assert.Equal(a.FolderName, again.FolderName);
        Assert.NotEqual(a.FolderName, b.FolderName);
        Assert.NotEqual(ProfileStore.DataDir, ProfileStore.DataDirFor(a));
        Assert.Equal(ProfileStore.DataDir, ProfileStore.DataDirFor(null));
    }

    [Fact]
    public void Only_challenges_from_the_proxy_itself_match()
    {
        var proxy = ProxySpec.Parse("proxy.example.com:3128")!;

        Assert.True(proxy.IsChallengeFrom("http://proxy.example.com:3128/"));
        Assert.True(proxy.IsChallengeFrom("http://PROXY.example.com:3128"));
        Assert.False(proxy.IsChallengeFrom("https://booking.com/"));              // a website's own sign-in
        Assert.False(proxy.IsChallengeFrom("http://proxy.example.com:8080/"));    // another port
        Assert.False(proxy.IsChallengeFrom(null));
    }

    [Fact]
    public void Engine_arguments_route_through_the_proxy_and_block_webrtc_leaks()
    {
        var settings = new AppSettings { Mode = AppSettings.Balanced };
        var proxy = ProxySpec.Parse("socks5://h:1080")!;

        var args = ProxySpec.EngineArguments(proxy, settings);

        Assert.Contains("--proxy-server=\"socks5://h:1080\"", args);
        Assert.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", args);
        Assert.Equal("", ProxySpec.EngineArguments(null, settings));
    }

    [Fact]
    public void Low_RAM_adds_no_gpu_and_your_own_arguments_win()
    {
        var proxy = ProxySpec.Parse("h:1")!;
        var low = new AppSettings { Mode = AppSettings.LowMemory };
        var custom = new AppSettings { Mode = AppSettings.LowMemory, BrowserArguments = "--process-per-site" };

        Assert.Equal("--disable-gpu", ProxySpec.EngineArguments(null, low));
        Assert.EndsWith("--disable-gpu", ProxySpec.EngineArguments(proxy, low));
        Assert.Equal("--process-per-site", ProxySpec.EngineArguments(null, custom));
        Assert.EndsWith("--process-per-site", ProxySpec.EngineArguments(proxy, custom));
        Assert.DoesNotContain("--disable-gpu", ProxySpec.EngineArguments(proxy, custom));
    }

    [Fact]
    public void The_proxy_password_is_encrypted_on_disk_and_comes_back()
    {
        var p = new Profile { Name = "Hilton", Proxy = "1.2.3.4:8080", ProxyUser = "alice", ProxyPassword = "s3cret!pass" };
        var json = JsonSerializer.Serialize(new List<Profile> { p });

        Assert.DoesNotContain("s3cret!pass", json);
        Assert.Contains("alice", json);
        var back = JsonSerializer.Deserialize<List<Profile>>(json)![0];
        Assert.Equal("s3cret!pass", back.ProxyPassword);
        Assert.Equal("alice", back.ProxyUser);
    }

    [Fact]
    public void A_password_copied_to_another_profile_cannot_be_read()
    {
        var a = new Profile { ProxyPassword = "pw" };
        var b = new Profile { ProxyPasswordProtected = a.ProxyPasswordProtected };

        Assert.Equal("pw", a.ProxyPassword);
        Assert.Equal("", b.ProxyPassword);
    }

    [Fact]
    public void An_empty_or_damaged_password_reads_as_empty()
    {
        Assert.Equal("", new Profile().ProxyPassword);
        Assert.Equal("", new Profile { ProxyPasswordProtected = "not-base64!!" }.ProxyPassword);
    }

    [Fact]
    public void Proxy_settings_survive_save_and_load_and_old_files_still_load()
    {
        var store = new ProfileStore();
        var p = new Profile { Name = "A", Proxy = "socks5://h:1080" };
        store.Profiles.Add(p);
        store.Save();
        File.WriteAllText(ProfileStore.FilePath + ".legacy", "[{\"Id\":\"abc\",\"Name\":\"Old\"}]");

        var loaded = ProfileStore.Load();
        Assert.Equal("socks5://h:1080", loaded.Profiles.Single().Proxy);
        Assert.Equal("h", loaded.Profiles.Single().ProxySpec!.Host);

        File.WriteAllText(ProfileStore.FilePath, "[{\"Id\":\"abc\",\"Name\":\"Old\"}]");
        var old = ProfileStore.Load().Profiles.Single();
        Assert.Equal("", old.Proxy);
        Assert.Null(old.ProxySpec);
    }

    [Fact]
    public void A_duplicated_profile_does_not_inherit_the_proxy()
    {
        var p = new Profile { Name = "A", Proxy = "h:1", ProxyUser = "u" };
        var copy = p.Duplicate();

        Assert.Equal("", copy.Proxy);
        Assert.Equal("", copy.ProxyUser);
    }

    [Fact]
    public void The_hover_text_names_the_proxy_but_never_the_password()
    {
        var p = new Profile { Proxy = "http://1.2.3.4:8080", ProxyPassword = "s3cret" };

        Assert.Contains("Proxy: 1.2.3.4:8080", p.Tip);
        Assert.DoesNotContain("s3cret", p.Tip);
    }
}
