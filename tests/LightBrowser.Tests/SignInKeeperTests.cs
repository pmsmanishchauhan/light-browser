using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class SignInKeeperTests : DataFolderTest
{
    [Theory]
    [InlineData(true, "www.expediapartnercentral.com", true)]
    [InlineData(true, ".expediapartnercentral.com", true)]
    [InlineData(true, "admin.booking.com", true)]
    [InlineData(false, "admin.booking.com", false)]   // already has an expiry: nothing to do
    [InlineData(true, "", false)]
    [InlineData(true, null, false)]
    [InlineData(true, ".", false)]
    public void Only_session_cookies_of_real_sites_are_kept(bool isSession, string? domain, bool keep) =>
        Assert.Equal(keep, SignInKeeper.ShouldKeep(isSession, domain, null));

    [Fact]
    public void A_cookie_of_a_blocked_tracker_domain_is_not_made_to_last()
    {
        Func<string, bool> blocked = url => url.Contains("doubleclick.net");

        Assert.False(SignInKeeper.ShouldKeep(true, ".doubleclick.net", blocked));
        Assert.True(SignInKeeper.ShouldKeep(true, ".expedia.com", blocked));
    }

    [Fact]
    public void The_built_in_blocklist_is_what_the_browser_passes_in()
    {
        Assert.False(SignInKeeper.ShouldKeep(true, "doubleclick.net", RequestBlocker.Load().IsBlocked));
        Assert.True(SignInKeeper.ShouldKeep(true, "expediapartnercentral.com", RequestBlocker.Load().IsBlocked));
    }

    [Fact]
    public void Sign_ins_are_kept_by_default_and_the_switch_is_in_settings() => Assert.True(new AppSettings().KeepSignIns);
}
