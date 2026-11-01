using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

// The script itself is checked against the real YouTube with tools\check-youtube.ps1 (it needs the network and
// changes whenever YouTube does); these tests guard the parts that can be checked offline.
public class YouTubeAdsTests
{
    [Theory]
    [InlineData("youtube.com")]
    [InlineData("www.youtube.com")]
    [InlineData("m.youtube.com")]
    [InlineData("music.youtube.com")]
    [InlineData("youtube-nocookie.com")]
    [InlineData("www.youtube-nocookie.com")]
    [InlineData("youtubekids.com")]
    [InlineData("www.youtubekids.com")]
    public void YouTube_addresses_are_recognised(string host) => Assert.True(YouTubeAds.IsYouTubeHost(host));

    [Theory]
    [InlineData("notyoutube.com")]
    [InlineData("evilyoutube.com")]
    [InlineData("youtube.com.evil.example")]
    [InlineData("youtube.co")]
    [InlineData("notyoutubekids.com")]
    [InlineData("booking.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_addresses_are_not(string? host) => Assert.False(YouTubeAds.IsYouTubeHost(host));

    [Fact]
    public void The_script_is_filled_in_and_safe_to_embed()
    {
        var js = YouTubeAds.Script;

        Assert.DoesNotContain("__CSS__", js);
        Assert.StartsWith("(function () {", js);
        Assert.EndsWith("})();", js);
        Assert.DoesNotContain("</script", js, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(js.Count(c => c == '{'), js.Count(c => c == '}'));
        Assert.Equal(js.Count(c => c == '('), js.Count(c => c == ')'));
        Assert.Equal(js.Count(c => c == '['), js.Count(c => c == ']'));
        Assert.Contains(YouTubeAds.Css, js);
    }

    [Fact]
    public void The_style_rules_fit_inside_a_single_quoted_JavaScript_string()
    {
        Assert.DoesNotContain('\'', YouTubeAds.Css);
        Assert.DoesNotContain('\\', YouTubeAds.Css);
        Assert.DoesNotContain('\n', YouTubeAds.Css);
        Assert.Contains("display: none !important", YouTubeAds.Css);
    }

    [Fact]
    public void The_script_only_acts_on_YouTube_and_removes_the_ad_data()
    {
        var js = YouTubeAds.Script;

        Assert.Contains("(youtube(-nocookie)?|youtubekids)\\.com", js);   // does nothing on any other site
        Assert.Contains("return;", js[..js.IndexOf("window.__lbAdBlock", StringComparison.Ordinal)]);
        foreach (var key in new[] { "adPlacements", "playerAds", "adSlots", "ytInitialPlayerResponse", "ytInitialData", "JSON.parse" })
            Assert.Contains(key, js);
    }

    [Fact]
    public void The_script_covers_feeds_shorts_detection_recovery_and_restores_the_video_afterwards()
    {
        var js = YouTubeAds.Script;

        foreach (var marker in new[] { "adSlotRenderer", "reelWatchEndpoint", "adClientParams", "onAbnormalityDetected", "loadVideoById", "saved.muted", "saved.rate" })
            Assert.Contains(marker, js);
    }

    [Fact]
    public void The_host_check_appears_once_so_the_offline_self_test_can_pretend_to_be_on_YouTube()
    {
        var js = YouTubeAds.Script;

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(js, "location\\.hostname").Count);
    }

    [Fact]
    public void The_fallback_only_touches_the_video_while_an_ad_is_really_showing()
    {
        var js = YouTubeAds.Script;

        // Jumping to the end is guarded by both the player's "ad-showing" state and the presence of ad controls.
        var jump = js.IndexOf("v.currentTime = v.duration", StringComparison.Ordinal);
        var guard = js.LastIndexOf("ad-showing", jump, StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < jump);
        Assert.Contains("ytp-ad-player-overlay", js[guard..jump]);
    }
}
