using System.IO;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class SuggestionTests : DataFolderTest
{
    [Theory]
    [InlineData("https://Admin.Booking.com/hotel/x?token=abc#top", "https://admin.booking.com")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080")]
    [InlineData("https://example.com:443/", "https://example.com")]
    [InlineData("https://user:secret@example.com/p", "https://example.com")]
    public void Normalize_keeps_only_the_origin(string input, string expected) =>
        Assert.Equal(expected, SuggestionStore.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("about:blank")]
    [InlineData("javascript:alert(1)")]
    public void Normalize_rejects_anything_that_is_not_a_web_address(string? input) =>
        Assert.Null(SuggestionStore.Normalize(input));

    [Theory]
    [InlineData(true, 200, true)]
    [InlineData(true, 204, true)]
    [InlineData(true, 299, true)]
    [InlineData(true, 301, false)]    // a redirect that was not followed is inconclusive
    [InlineData(true, 401, false)]    // login required: not proof the site works
    [InlineData(true, 403, false)]
    [InlineData(true, 404, false)]
    [InlineData(true, 500, false)]
    [InlineData(true, 0, false)]      // no status reported
    [InlineData(false, 200, false)]   // navigation failed or was aborted
    [InlineData(false, 0, false)]
    public void Only_a_completed_load_with_a_2xx_status_counts_as_verified(bool succeeded, int status, bool expected) =>
        Assert.Equal(expected, SuggestionStore.IsVerified(succeeded, status));

    [Fact]
    public void Failed_or_inconclusive_checks_are_never_saved()
    {
        var store = new SuggestionStore();
        Assert.False(store.AddIfVerified("https://down.example.com/", false, 0));
        Assert.False(store.AddIfVerified("https://login.example.com/", true, 401));
        Assert.False(store.AddIfVerified("https://gone.example.com/", true, 404));
        Assert.False(store.AddIfVerified("ftp://files.example.com/", true, 200));
        Assert.Empty(store.Sites);
        Assert.Empty(SuggestionStore.Load().Sites);   // and nothing reached disk

        Assert.True(store.AddIfVerified("https://ok.example.com/page?x=1", true, 200));
        Assert.Equal(new[] { "https://ok.example.com" }, store.Sites);
    }

    [Fact]
    public void Duplicates_are_ignored_whatever_the_case_or_path()
    {
        var store = new SuggestionStore();
        Assert.True(store.Add("https://Booking.com/a"));
        Assert.False(store.Add("https://booking.com/b?x=1"));
        Assert.False(store.Add("HTTPS://BOOKING.COM"));
        Assert.True(store.Add("http://booking.com"));       // different scheme is a different site
        Assert.Equal(2, store.Sites.Count);
    }

    [Fact]
    public void Suggestions_persist_across_a_restart()
    {
        var first = new SuggestionStore();
        first.Add("https://expedia.com/x");
        first.Add("https://agoda.com");

        var afterRestart = SuggestionStore.Load();

        Assert.Equal(new[] { "https://expedia.com", "https://agoda.com" }, afterRestart.Sites);
    }

    [Fact]
    public void A_corrupt_suggestions_file_gives_an_empty_list_instead_of_an_error()
    {
        File.WriteAllText(Path.Combine(Dir, "suggestions.json"), "{{{");
        var store = SuggestionStore.Load();
        Assert.Empty(store.Sites);
        Assert.True(store.Add("https://ok.com"));            // still usable afterwards
        Assert.Single(SuggestionStore.Load().Sites);
    }

    [Fact]
    public void Hand_edited_or_hostile_entries_are_cleaned_on_load()
    {
        File.WriteAllText(Path.Combine(Dir, "suggestions.json"),
            "[\"https://a.com/path?q=1\",\"HTTPS://A.COM\",\"javascript:alert(1)\",\"\",\"https://b.com\"]");
        Assert.Equal(new[] { "https://a.com", "https://b.com" }, SuggestionStore.Load().Sites);
    }

    [Fact]
    public void The_list_is_capped_and_drops_the_oldest()
    {
        var store = new SuggestionStore();
        for (var i = 0; i < 230; i++) store.Add($"https://site{i}.example.com");
        Assert.Equal(200, store.Sites.Count);
        Assert.Equal("https://site30.example.com", store.Sites[0]);
        Assert.Equal("https://site229.example.com", store.Sites[^1]);
    }

    [Fact]
    public void Matching_finds_by_host_and_ranks_prefix_matches_first()
    {
        var store = new SuggestionStore();
        store.Add("https://admin.booking.com");
        store.Add("https://booking.com");
        store.Add("https://www.bookmyshow.com");
        store.Add("https://expedia.com");

        Assert.Equal(new[] { "https://booking.com", "https://admin.booking.com" }, store.Matching("booking", 5));
        Assert.Equal(new[] { "https://booking.com", "https://admin.booking.com" }, store.Matching("https://booking.com", 5));   // scheme in the query is ignored
        Assert.Empty(store.Matching("zzz", 5));
        Assert.Empty(store.Matching("  ", 5));
        Assert.Single(store.Matching("book", 1));
    }

    // ---- common vs group-specific suggestions -----------------------------------------------

    static Profile P(string name, string group, params string[] homes) =>
        new() { Name = name, Group = group, HomeUrls = homes.ToList() };

    [Fact]
    public void Group_sites_come_from_the_groups_other_profiles_and_stay_separate_from_the_common_list()
    {
        var a = P("A", "Hilton", "https://admin.booking.com/x", "https://expedia.com");
        var b = P("B", "hilton", "https://EXPEDIA.com/y", "https://agoda.com");   // same group, different case
        var c = P("C", "Marriott", "https://marriott-only.com");
        var common = new SuggestionStore();
        common.Add("https://booking.com");

        var hilton = SuggestionStore.ForGroup("Hilton", new[] { a, b, c });

        Assert.Equal(new[] { "https://admin.booking.com", "https://expedia.com", "https://agoda.com" }, hilton);
        Assert.DoesNotContain("https://marriott-only.com", hilton);   // other groups' sites do not leak
        Assert.DoesNotContain("https://booking.com", hilton);          // common sites are not copied into groups
        Assert.Equal(new[] { "https://booking.com" }, common.Sites);   // and group sites are not added to common
    }

    [Fact]
    public void Group_sites_skip_the_profile_being_edited_deleted_profiles_and_empty_groups()
    {
        var editing = P("Edit", "G", "https://mine.com");
        var other = P("Other", "G", "https://shared.com");
        var trashed = P("Trashed", "G", "https://trashed.com");
        trashed.IsDeleted = true;
        var all = new[] { editing, other, trashed };

        Assert.Equal(new[] { "https://shared.com" }, SuggestionStore.ForGroup("G", all, except: editing));
        Assert.Empty(SuggestionStore.ForGroup("", all));
        Assert.Empty(SuggestionStore.ForGroup("   ", all));
        Assert.Empty(SuggestionStore.ForGroup("NoSuchGroup", all));
    }
}
