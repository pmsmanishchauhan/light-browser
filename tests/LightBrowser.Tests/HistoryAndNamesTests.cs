using System.IO;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class HistoryTests : DataFolderTest
{
    [Theory]
    [InlineData("https://user:pw@Example.com/a/b?x=1#frag", "https://example.com/a/b?x=1")]
    [InlineData("http://example.com:8080/p", "http://example.com:8080/p")]
    public void Addresses_are_stored_without_credentials_or_fragments(string input, string expected) =>
        Assert.Equal(expected, HistoryStore.Clean(input));

    [Theory]
    [InlineData("about:blank")]
    [InlineData("edge://settings/passwords")]
    [InlineData("file:///C:/x.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_web_pages_are_recorded(string? url)
    {
        var h = new HistoryStore();
        h.Record("p1", url, "t");
        Assert.Equal(0, h.Count("p1"));
    }

    [Fact]
    public void Each_profile_only_sees_its_own_history()
    {
        var h = new HistoryStore();
        h.Record("alice", "https://booking.com/a", "A page");
        h.Record("bob", "https://expedia.com/b", "B page");

        Assert.Equal(new[] { "https://booking.com/a" }, h.Search("alice", "", 10).Select(e => e.Url));
        Assert.Equal(new[] { "https://expedia.com/b" }, h.Search("bob", "", 10).Select(e => e.Url));
        Assert.Empty(h.Search("alice", "expedia", 10));
    }

    [Fact]
    public void Reloading_the_current_page_refreshes_its_entry_instead_of_adding_one()
    {
        var h = new HistoryStore();
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        h.Record("p", "https://a.com/x", "Old title", t0);
        h.Record("p", "https://a.com/x", "New title", t0.AddMinutes(5));

        var e = Assert.Single(h.Search("p", "", 10));
        Assert.Equal("New title", e.Title);
        Assert.Equal(t0.AddMinutes(5), e.Time);
    }

    [Fact]
    public void Search_is_newest_first_one_row_per_address_and_matches_title_or_url()
    {
        var h = new HistoryStore();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        h.Record("p", "https://a.com/1", "Hotel Alpha", t);
        h.Record("p", "https://b.com/2", "Beta", t.AddMinutes(1));
        h.Record("p", "https://a.com/1", "Hotel Alpha", t.AddMinutes(2));   // visited again later

        Assert.Equal(new[] { "https://a.com/1", "https://b.com/2" }, h.Search("p", "", 10).Select(e => e.Url));
        Assert.Equal(new[] { "https://a.com/1" }, h.Search("p", "hotel", 10).Select(e => e.Url));
        Assert.Equal(new[] { "https://b.com/2" }, h.Search("p", "b.com", 10).Select(e => e.Url));
        Assert.Single(h.Search("p", "", 1));
    }

    [Fact]
    public void A_late_title_is_attached_to_the_latest_visit()
    {
        var h = new HistoryStore();
        h.Record("p", "https://a.com/x", "");
        h.UpdateTitle("p", "https://a.com/x#section", "Real title");
        Assert.Equal("Real title", h.Search("p", "", 1)[0].Title);
    }

    [Fact]
    public void History_is_capped_per_profile_and_keeps_the_newest()
    {
        var h = new HistoryStore();
        for (var i = 0; i < HistoryStore.MaxPerProfile + 50; i++) h.Record("p", $"https://a.com/{i}", "");
        Assert.Equal(HistoryStore.MaxPerProfile, h.Count("p"));
        Assert.Equal($"https://a.com/{HistoryStore.MaxPerProfile + 49}", h.Search("p", "", 1)[0].Url);
    }

    [Fact]
    public void History_persists_across_a_restart_and_stays_per_profile()
    {
        var h = new HistoryStore();
        h.Record("alice", "https://booking.com/a", "A");
        h.Record("bob", "https://expedia.com/b", "B");
        h.Flush();

        var after = new HistoryStore();
        Assert.Equal("https://booking.com/a", after.Search("alice", "", 5).Single().Url);
        Assert.Equal("https://expedia.com/b", after.Search("bob", "", 5).Single().Url);
        Assert.True(File.Exists(Path.Combine(Dir, "history", "alice.json")));
    }

    [Fact]
    public void Clearing_one_profile_removes_its_file_and_leaves_others()
    {
        var h = new HistoryStore();
        h.Record("alice", "https://a.com", "A");
        h.Record("bob", "https://b.com", "B");
        h.Flush();

        h.Clear("alice");

        Assert.Equal(0, h.Count("alice"));
        Assert.False(File.Exists(Path.Combine(Dir, "history", "alice.json")));
        Assert.Equal(0, new HistoryStore().Count("alice"));
        Assert.Equal(1, new HistoryStore().Count("bob"));
    }

    [Fact]
    public void A_corrupt_history_file_gives_empty_history_and_is_kept_aside()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "history"));
        File.WriteAllText(Path.Combine(Dir, "history", "p.json"), "{{ not json");

        var h = new HistoryStore();

        Assert.Equal(0, h.Count("p"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Dir, "history"), "p.json.corrupt-*"));
    }

    [Fact]
    public void A_hostile_profile_id_cannot_escape_the_history_folder()
    {
        var h = new HistoryStore();
        h.Record(@"..\..\evil", "https://a.com", "x");
        h.Flush();

        Assert.Empty(Directory.GetFiles(Dir, "evil*", SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(Path.Combine(Dir, "history", "evil.json")));
    }
}

public class NameSuggesterTests
{
    static List<string> Suggest(string group, string[] urls, params string[] taken) =>
        NameSuggester.Suggest(group, urls, taken);

    [Fact]
    public void A_group_gives_the_next_free_numbered_name()
    {
        Assert.Equal("Hilton 1", Suggest("Hilton", new string[0])[0]);
        Assert.Equal("Hilton 3", Suggest("Hilton", new string[0], "Hilton 1", "Hilton 2")[0]);
        Assert.Equal("Hilton 2", Suggest("Hilton", new string[0], "hilton 1")[0]);   // names compare ignoring case
    }

    [Fact]
    public void The_first_home_page_gives_a_site_based_name()
    {
        var s = Suggest("", new[] { "https://admin.booking.com/hotel" });
        Assert.Contains("Booking account", s);
        Assert.Contains("Booking account 2", Suggest("", new[] { "https://admin.booking.com" }, "Booking account"));
        Assert.Contains("Example account", Suggest("", new[] { "https://www.example.co.uk" }));
        Assert.DoesNotContain(Suggest("", new[] { "http://192.168.1.5/admin" }), n => n.Contains("192"));
    }

    [Fact]
    public void A_generic_client_name_is_always_available_and_unique()
    {
        Assert.Equal(new[] { "Client 1" }, Suggest("", new string[0]));
        Assert.Contains("Client 3", Suggest("", new string[0], "Client 1", "Client 2"));
    }

    [Fact]
    public void Suggestions_never_repeat_an_existing_name_and_are_at_most_three()
    {
        var existing = new[] { "Hilton 1", "Booking account", "Client 1" };
        var s = Suggest("Hilton", new[] { "https://booking.com" }, existing);

        Assert.True(s.Count <= 3);
        Assert.Empty(s.Intersect(existing, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(s.Count, s.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public class DuplicateNameTests
{
    [Fact]
    public void Copies_get_copy_then_numbered_names_and_never_stack()
    {
        var a = new Profile { Name = "a" };
        var taken = new List<string> { "a" };

        var c1 = a.Duplicate(taken); taken.Add(c1.Name);
        var c2 = a.Duplicate(taken); taken.Add(c2.Name);
        var c3 = c1.Duplicate(taken);   // copying a copy goes back to the base name

        Assert.Equal("a (copy)", c1.Name);
        Assert.Equal("a (copy 2)", c2.Name);
        Assert.Equal("a (copy 3)", c3.Name);
    }

    [Fact]
    public void An_old_chain_of_copy_suffixes_is_collapsed()
    {
        var old = new Profile { Name = "a (copy) (copy) (copy)" };
        Assert.Equal("a (copy)", old.Duplicate().Name);
    }
}

public class ProfileDisplayTests
{
    [Theory]
    [InlineData("Hilton Delhi", "H")]
    [InlineData("  marriott", "M")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    public void The_avatar_shows_the_first_letter(string name, string expected) =>
        Assert.Equal(expected, new Profile { Name = name }.Initial);
}
