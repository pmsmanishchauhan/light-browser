using System.IO;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class BlockerTests : DataFolderTest
{
    [Theory]
    [InlineData("https://doubleclick.net/x.js")]
    [InlineData("https://ad.doubleclick.net/ddm/ad?x=1")]                 // subdomain of a listed domain
    [InlineData("http://A.B.C.DoubleClick.NET:8080/p.gif")]               // any depth, any case, any port
    [InlineData("https://adservice.google.com/adsid/google/ui")]
    [InlineData("https://static.criteo.net/js/ld/publishertag.js")]
    public void Listed_domains_and_their_subdomains_are_blocked(string url) =>
        Assert.True(new RequestBlocker().IsBlocked(url));

    [Theory]
    [InlineData("https://notdoubleclick.net/")]                           // lookalike, not a subdomain
    [InlineData("https://doubleclick.net.example.com/")]
    [InlineData("https://www.google.com/")]                               // only adservice.google.com is listed
    [InlineData("https://www.booking.com/index.html")]
    [InlineData("https://admin.booking.com/hotel/hoteladmin")]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("data:text/plain,hello")]
    [InlineData("about:blank")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Everything_else_passes(string? url) => Assert.False(new RequestBlocker().IsBlocked(url));

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM  ", "example.com")]
    [InlineData("example.com # a comment", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData("0.0.0.0 ads.example.com", "ads.example.com")]
    [InlineData("127.0.0.1\tads.example.com", "ads.example.com")]
    [InlineData("||ads.example.com^", "ads.example.com")]
    [InlineData("||ads.example.com^$third-party", "ads.example.com")]
    [InlineData("a-b.example.co.uk.", "a-b.example.co.uk")]
    public void Lines_in_the_common_list_formats_are_understood(string line, string domain) =>
        Assert.Equal(domain, RequestBlocker.ParseLine(line));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# only a comment")]
    [InlineData("localhost")]                  // no dot: would be a TLD or a host name, never useful
    [InlineData("com")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0 localhost")]
    [InlineData("two words here")]
    [InlineData("exa mple.com")]
    [InlineData("bad_domain.com")]
    [InlineData("ex..ample.com")]
    [InlineData(".example.com")]
    [InlineData("http://example.com/path")]
    [InlineData(null)]
    public void Junk_lines_are_ignored(string? line) => Assert.Null(RequestBlocker.ParseLine(line));

    [Fact]
    public void Your_own_domains_are_added_to_the_built_in_ones()
    {
        var blocker = new RequestBlocker(new[] { "# mine", "tracker.test", "0.0.0.0 ads.other.test", "garbage line here" });

        Assert.True(blocker.IsBlocked("https://px.tracker.test/a.gif"));
        Assert.True(blocker.IsBlocked("https://ads.other.test/"));
        Assert.False(blocker.IsBlocked("https://other.test/"));
        Assert.True(blocker.IsBlocked("https://doubleclick.net/"));
        Assert.Equal(RequestBlocker.BuiltIn.Length + 2, blocker.Count);
    }

    [Fact]
    public void The_built_in_list_is_clean_and_keeps_real_sites_working()
    {
        foreach (var d in RequestBlocker.BuiltIn) Assert.Equal(d, RequestBlocker.ParseLine(d));
        Assert.Equal(RequestBlocker.BuiltIn.Length, RequestBlocker.BuiltIn.Distinct().Count());

        var blocker = new RequestBlocker();
        foreach (var site in new[] { "google.com", "facebook.com", "youtube.com", "booking.com", "expedia.com", "agoda.com",
                                     "makemytrip.com", "goibibo.com", "airbnb.com", "microsoft.com", "amazon.com", "cloudflare.com" })
            Assert.False(blocker.IsBlocked($"https://www.{site}/"), site);
    }

    [Fact]
    public void Load_creates_an_editable_file_the_first_time_and_reads_it_afterwards()
    {
        Assert.False(File.Exists(RequestBlocker.FilePath));

        var first = RequestBlocker.Load();
        Assert.True(File.Exists(RequestBlocker.FilePath));
        Assert.Equal(RequestBlocker.BuiltIn.Length, first.Count);   // the template adds nothing

        File.AppendAllText(RequestBlocker.FilePath, "mine.example\r\n");
        Assert.True(RequestBlocker.Load().IsBlocked("https://x.mine.example/"));
    }

    [Fact]
    public void A_missing_data_folder_does_not_stop_the_app()
    {
        Directory.Delete(Dir, true);

        Assert.True(RequestBlocker.Load().IsBlocked("https://doubleclick.net/"));
    }

    [Fact]
    public void Blocking_is_on_by_default_and_the_choice_is_remembered()
    {
        Assert.True(AppSettings.Load().BlockTrackers);

        var s = AppSettings.Load();
        s.BlockTrackers = false;
        s.Save();

        Assert.False(AppSettings.Load().BlockTrackers);
    }
}
