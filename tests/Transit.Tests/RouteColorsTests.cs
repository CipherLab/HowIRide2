using Transit.Gtfs;

namespace Transit.Tests;

public class RouteColorsTests
{
    static RouteInfo Route(string id, string longName = "") => new(id, id, longName, "771473", "FFFFFF", 3);

    static ShapeGeometry Line(double lat0, double lon0, double lat1, double lon1) =>
        new("s", [.. Enumerable.Range(0, 50).Select(i => lat0 + (lat1 - lat0) * i / 49)], [.. Enumerable.Range(0, 50).Select(i => lon0 + (lon1 - lon0) * i / 49)], new double[50]);

    [Fact]
    public void Named_lines_keep_the_feed_colour()
    {
        var routes = new Dictionary<string, RouteInfo> { ["901"] = Route("901", "METRO Blue Line") with { Color = "00539F" }, ["2"] = Route("2") };
        var result = RouteColors.Assign(routes, new Dictionary<string, IEnumerable<ShapeGeometry>>());
        Assert.Equal("00539F", result["901"].Color);
        Assert.Contains(result["2"].Color, RouteColors.Palette);
    }

    [Fact]
    public void Routes_sharing_a_street_get_different_colours()
    {
        var street = Line(44.95, -93.30, 44.95, -93.20);
        var ids = Enumerable.Range(1, RouteColors.Palette.Length).Select(i => i.ToString()).ToList();
        var result = RouteColors.Assign(ids.ToDictionary(id => id, id => Route(id)), ids.ToDictionary(id => id, _ => (IEnumerable<ShapeGeometry>)[street]));
        Assert.Equal(ids.Count, ids.Select(id => result[id].Color).Distinct().Count());
    }

    [Theory]
    [InlineData("FFDF52", "14212B")]
    [InlineData("7B2D8E", "FFFFFF")]
    [InlineData("2B3A4A", "FFFFFF")]
    public void Text_contrasts_with_the_colour(string fill, string text) => Assert.Equal(text, RouteColors.TextOn(fill));
}
