using Transit.Gtfs;

namespace Transit.Tests;

public sealed class StopSearchTests
{
    static StopInfo Stop(string id, string name) => new(id, name, 45, -93);

    static readonly StopInfo[] Stops =
    [
        Stop("1001", "Lake St & Hennepin Ave"),
        Stop("B", "Bryant Ave & 26th St"),
        Stop("100", "Hennepin Ave & 1st St"),
        Stop("2000", "Lake St & 100th Ave"),
    ];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("& ( )")]
    public void Empty_or_blank_query_finds_nothing(string query) => Assert.Empty(StopSearch.Find(Stops, query));

    [Fact]
    public void Exact_stop_id_comes_first()
    {
        Assert.Equal("B", StopSearch.Find(Stops, "B")[0].Id);
        Assert.Equal(["100", "1001", "2000"], StopSearch.Find(Stops, "100").Select(s => s.Id));
    }

    [Fact]
    public void All_words_must_match_in_any_order_ignoring_case()
    {
        Assert.Equal(["1001"], StopSearch.Find(Stops, "hennepin LAKE").Select(s => s.Id));
    }

    [Fact]
    public void Punctuation_in_the_query_is_harmless()
    {
        Assert.Equal(["1001"], StopSearch.Find(Stops, "lake & (hennepin").Select(s => s.Id));
    }

    [Fact]
    public void Results_stop_at_the_limit()
    {
        Assert.Single(StopSearch.Find(Stops, "st", limit: 1));
    }
}
