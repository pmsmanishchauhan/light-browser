using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class SearchTests : DataFolderTest
{
    [Theory]
    [InlineData("google", "hotel rates", "https://www.google.com/search?q=hotel%20rates")]
    [InlineData("duckduckgo", "hotel rates", "https://duckduckgo.com/?q=hotel%20rates")]
    [InlineData("bing", "hotel rates", "https://www.bing.com/search?q=hotel%20rates")]
    [InlineData("brave", "hotel rates", "https://search.brave.com/search?q=hotel%20rates")]
    [InlineData("yahoo", "hotel rates", "https://search.yahoo.com/search?p=hotel%20rates")]
    public void Text_is_searched_with_the_chosen_engine(string engine, string text, string url) =>
        Assert.Equal(url, SearchEngines.ToUrl(text, engine));

    [Theory]
    [InlineData("ddg cats", "https://duckduckgo.com/?q=cats")]
    [InlineData("DDG cats and dogs", "https://duckduckgo.com/?q=cats%20and%20dogs")]
    [InlineData("b cats", "https://www.bing.com/search?q=cats")]
    [InlineData("br cats", "https://search.brave.com/search?q=cats")]
    [InlineData("g  cats", "https://www.google.com/search?q=cats")]
    public void A_keyword_picks_the_engine_for_that_search_only(string typed, string url) =>
        Assert.Equal(url, SearchEngines.ToUrl(typed, "yahoo"));

    [Theory]
    [InlineData("b")]            // a lone keyword is just a word to search for
    [InlineData("ddg")]
    [InlineData("b ")]
    public void A_keyword_without_a_query_is_an_ordinary_search(string typed) =>
        Assert.StartsWith("https://search.yahoo.com/search?p=", SearchEngines.ToUrl(typed, "yahoo"));

    [Theory]
    [InlineData("booking.com", "https://booking.com")]
    [InlineData("admin.booking.com/hotel", "https://admin.booking.com/hotel")]
    [InlineData("localhost:5000", "https://localhost:5000")]
    [InlineData("http://example.com/a b", "http://example.com/a b")]
    [InlineData("https://x.test/?q=1", "https://x.test/?q=1")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("  example.org  ", "https://example.org")]
    public void Addresses_are_opened_not_searched(string typed, string url) =>
        Assert.Equal(url, SearchEngines.ToUrl(typed, "duckduckgo"));

    [Fact]
    public void An_unknown_or_missing_engine_falls_back_to_the_default()
    {
        Assert.Equal("https://www.google.com/search?q=x", SearchEngines.ToUrl("x", "nonsense"));
        Assert.Equal("https://www.google.com/search?q=x", SearchEngines.ToUrl("x", null));
        Assert.Equal("Google", SearchEngines.Get("nonsense").Name);
    }

    [Fact]
    public void Special_characters_in_a_search_are_escaped()
    {
        var url = SearchEngines.ToUrl("a&b=c #1 100%", "duckduckgo");

        Assert.Equal("https://duckduckgo.com/?q=a%26b%3Dc%20%231%20100%25", url);
    }

    [Fact]
    public void Engines_are_well_formed_and_keywords_do_not_clash()
    {
        Assert.Equal(SearchEngines.All.Length, SearchEngines.All.Select(e => e.Id).Distinct().Count());
        var keywords = SearchEngines.All.SelectMany(e => e.Keywords).ToList();
        Assert.Equal(keywords.Count, keywords.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var e in SearchEngines.All)
        {
            Assert.Contains("{q}", e.Template);
            Assert.StartsWith("https://", e.Template);
        }
    }

    [Fact]
    public void The_chosen_engine_is_remembered_and_a_bad_saved_value_is_replaced()
    {
        var s = AppSettings.Load();
        Assert.Equal("google", s.SearchEngine);
        s.SearchEngine = "brave";
        s.Save();
        Assert.Equal("brave", AppSettings.Load().SearchEngine);

        System.IO.File.WriteAllText(System.IO.Path.Combine(Dir, "settings.json"), "{ \"SearchEngine\": \"nope\" }");
        Assert.Equal("google", AppSettings.Load().SearchEngine);
    }
}
