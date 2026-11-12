using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class AddressSuggestionTests : DataFolderTest
{
    static HistoryEntry Page(string url, string title = "") => new() { Url = url, Title = title, Time = DateTime.UtcNow };

    static List<AddressSuggestion> Build(string text, IEnumerable<HistoryEntry>? history = null, IEnumerable<string>? sites = null,
                                         IEnumerable<OtaPortal>? portals = null) =>
        AddressSuggestions.Build(text, "duckduckgo", history ?? Array.Empty<HistoryEntry>(), sites ?? Array.Empty<string>(),
                                 portals ?? Array.Empty<OtaPortal>());

    // ---- what Enter does ----

    [Theory]
    [InlineData("booking.com", true)]
    [InlineData("localhost:3000", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("https://x.test/a", true)]
    [InlineData("about:blank", true)]
    [InlineData("hotel rates", false)]
    [InlineData("hotel", false)]
    [InlineData("ddg cats.com", false)]   // a keyword search, even though the query looks like an address
    [InlineData("", false)]
    public void Text_is_an_address_exactly_when_Enter_would_open_it(string text, bool address) =>
        Assert.Equal(address, SearchEngines.IsAddress(text));

    [Fact]
    public void The_first_row_says_where_Enter_goes_for_an_address()
    {
        var rows = Build("booking.com");

        Assert.Equal("go", rows[0].Kind);
        Assert.Equal("https://booking.com", rows[0].Url);
        Assert.Equal("Go to booking.com", rows[0].Title);
    }

    [Fact]
    public void The_first_row_says_which_engine_Enter_searches_with_for_words()
    {
        var rows = Build("hotel rates");

        Assert.Equal("search", rows[0].Kind);
        Assert.Equal("Search DuckDuckGo for \"hotel rates\"", rows[0].Title);
        Assert.Equal(SearchEngines.ToUrl("hotel rates", "duckduckgo"), rows[0].Url);
    }

    [Fact]
    public void A_keyword_names_the_engine_for_that_search_only()
    {
        var rows = Build("b hotel rates");

        Assert.Equal("Search Bing for \"hotel rates\"", rows[0].Title);
        Assert.Equal("https://www.bing.com/search?q=hotel%20rates", rows[0].Url);
    }

    [Fact]
    public void Enter_and_the_first_row_always_agree()
    {
        foreach (var text in new[] { "booking.com", "hotel rates", "g cats", "localhost:5000", "x" })
        Assert.Equal(SearchEngines.ToUrl(text, "duckduckgo"), Build(text)[0].Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_typed_gives_no_rows(string? text) => Assert.Empty(AddressSuggestions.Build(text, "duckduckgo", Array.Empty<HistoryEntry>(), Array.Empty<string>(), Array.Empty<OtaPortal>()));

    // ---- the other rows ----

    [Fact]
    public void History_comes_before_sites_and_portals_with_title_and_a_readable_address()
    {
        var rows = Build("book",
            history: new[] { Page("https://admin.booking.com/hotel/hoteladmin/extranet_ng/manage/home.html", "Extranet - Home") },
            sites: new[] { "https://www.booking.com" },
            portals: new[] { new OtaPortal("Booking.com Extranet", "https://admin.booking.com/", "x") });

        Assert.Equal(new[] { "search", "history", "site", "portal" }, rows.Select(r => r.Kind));
        Assert.Equal("Extranet - Home", rows[1].Title);
        Assert.Equal("admin.booking.com/hotel/hoteladmin/extranet_ng/manage/home.html", rows[1].Detail);
        Assert.Equal("booking.com", rows[2].Title);
    }

    [Fact]
    public void A_page_without_a_title_shows_its_address()
    {
        var rows = Build("x", history: new[] { Page("https://x.example/a") });

        Assert.Equal("https://x.example/a", rows[1].Title);
    }

    [Fact]
    public void The_same_address_is_listed_once_even_when_scheme_www_or_slash_differ()
    {
        var rows = Build("booking",
            history: new[] { Page("https://www.booking.com/", "Booking.com") },
            sites: new[] { "https://booking.com", "http://www.booking.com/" },
            portals: new[] { new OtaPortal("Booking", "https://booking.com", "x") });

        Assert.Equal(2, rows.Count);   // the Enter row and one Booking row
        Assert.Equal("history", rows[1].Kind);
    }

    [Fact]
    public void An_address_that_is_the_first_row_is_not_repeated_below()
    {
        var rows = Build("booking.com", history: new[] { Page("https://booking.com/", "Booking") }, sites: new[] { "https://booking.com" });

        Assert.Single(rows);
    }

    [Fact]
    public void At_most_four_history_rows_and_eight_in_total()
    {
        var history = Enumerable.Range(1, 10).Select(i => Page($"https://h{i}.example/", "H" + i));
        var sites = Enumerable.Range(1, 10).Select(i => $"https://s{i}.example");

        var rows = Build("example", history, sites);

        Assert.Equal(4, rows.Count(r => r.Kind == "history"));
        Assert.Equal(AddressSuggestions.Max, rows.Count);
        Assert.Equal("H1", rows.First(r => r.Kind == "history").Title);   // the order given (newest first) is kept
    }

    [Theory]
    [InlineData("https://www.Host.com/path/", "Host.com/path")]
    [InlineData("http://host.com", "host.com")]
    [InlineData("https://host.com:8443/a?b=1", "host.com:8443/a?b=1")]
    [InlineData("host.com/", "host.com")]
    public void Addresses_are_shown_without_scheme_www_or_trailing_slash(string url, string shown) =>
        Assert.Equal(shown, AddressSuggestions.Pretty(url));

    [Theory]
    [InlineData("go", "")]
    [InlineData("search", "")]
    [InlineData("history", "")]
    [InlineData("site", "")]
    [InlineData("portal", "")]
    public void Each_kind_has_an_icon(string kind, string glyph) => Assert.Equal(glyph, new AddressSuggestion(kind, "u", "t", "").Glyph);

    // ---- history search used by the list is per profile ----

    [Fact]
    public void Another_profiles_history_never_appears_in_a_profiles_suggestions()
    {
        var store = new HistoryStore();
        store.Record("clientA", "https://secret-a.example/", "Client A bookings");
        store.Record("clientB", "https://b.example/", "Client B");

        var forB = AddressSuggestions.Build("example", "duckduckgo", store.Search("clientB", "example", 12), Array.Empty<string>(), Array.Empty<OtaPortal>());

        Assert.DoesNotContain(forB, r => r.Url.Contains("secret-a") || r.Title.Contains("Client A"));
        Assert.Contains(forB, r => r.Url == "https://b.example/");
    }
}
