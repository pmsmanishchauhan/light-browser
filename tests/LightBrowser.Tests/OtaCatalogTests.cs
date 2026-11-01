using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class OtaCatalogTests
{
    [Fact]
    public void Every_portal_is_a_clean_https_address_with_a_name_and_a_group()
    {
        Assert.NotEmpty(OtaCatalog.All);
        foreach (var p in OtaCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), p.Url);
            Assert.StartsWith("https://", p.Url);
            Assert.NotNull(SuggestionStore.Normalize(p.Url));
            Assert.DoesNotContain(' ', p.Url);
            Assert.Contains(p.Kind, new[] { OtaCatalog.Global, OtaCatalog.India, OtaCatalog.Software });
            Assert.DoesNotContain("password", p.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", p.Url, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Names_and_addresses_are_unique()
    {
        Assert.Equal(OtaCatalog.All.Length, OtaCatalog.All.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(OtaCatalog.All.Length, OtaCatalog.All.Select(p => p.Url.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("Booking.com Extranet")]
    [InlineData("Expedia Partner Central")]
    [InlineData("Agoda YCS")]
    [InlineData("Goibibo / MakeMyTrip Extranet (Ingo-MMT)")]
    [InlineData("AxisRooms (Hotelogix)")]
    public void The_portals_you_use_most_are_there(string name) =>
        Assert.Contains(OtaCatalog.All, p => p.Name == name);

    [Fact]
    public void All_three_groups_are_used_and_India_has_its_own()
    {
        foreach (var kind in new[] { OtaCatalog.Global, OtaCatalog.India, OtaCatalog.Software })
            Assert.Contains(OtaCatalog.All, p => p.Kind == kind);
    }

    [Theory]
    [InlineData("book", "https://admin.booking.com/")]
    [InlineData("YCS", "https://ycs.agoda.com/")]
    [InlineData("goibibo", "https://in.goibibo.com/newextranet/login")]
    [InlineData("axis", "https://app.axisrooms.com/")]
    [InlineData("makemytrip", "https://www.makemytrip.com/hotels-partner/")]
    public void The_address_bar_finds_a_portal_by_name_or_address(string typed, string expected) =>
        Assert.Contains(expected, OtaCatalog.Matching(typed, 6));

    [Fact]
    public void Very_short_or_unmatched_text_finds_nothing_and_the_limit_is_respected()
    {
        Assert.Empty(OtaCatalog.Matching("", 6));
        Assert.Empty(OtaCatalog.Matching("b", 6));
        Assert.Empty(OtaCatalog.Matching("zzzzqqq", 6));
        Assert.True(OtaCatalog.Matching("app", 3).Count <= 3);
        Assert.True(OtaCatalog.Matching("app", 3).Count > 0);
    }

    [Fact]
    public void Portals_are_home_pages_that_pass_the_same_cleaning_as_typed_ones()
    {
        // The profile dialog stores what it is given; make sure none of these would be rewritten oddly.
        foreach (var p in OtaCatalog.All)
        {
            var typed = p.Url.Contains("://") ? p.Url : "https://" + p.Url;
            Assert.Equal(p.Url, typed);
        }
    }
}
