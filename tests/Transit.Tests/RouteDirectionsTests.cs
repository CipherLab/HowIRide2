using Transit.Gtfs;

namespace Transit.Tests;

public sealed class RouteDirectionsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("routedirs").FullName;
    readonly StaticFeed[] _feeds;

    public RouteDirectionsTests() => _feeds = [StaticFeed.Load(TestGtfs.WriteBranches(Path.Combine(_dir, "gtfs.zip")))];

    public void Dispose() => Directory.Delete(_dir, true);

    RouteDirection Direction(int id) => RouteDirections.Build(_feeds)["R"].Single(d => d.DirectionId == id);

    [Fact]
    public void Directions_are_labelled_with_the_most_common_headsign()
    {
        Assert.Equal("East", Direction(0).Label);
        Assert.Equal("West", Direction(1).Label);
    }

    [Fact]
    public void Directions_are_in_order()
    {
        Assert.Equal([0, 1], RouteDirections.Build(_feeds)["R"].Select(d => d.DirectionId));
    }

    [Fact]
    public void Direction_with_no_headsign_uses_its_last_stop()
    {
        Assert.Equal("East End", RouteDirections.Build(_feeds)["N"].Single().Label);
    }

    [Fact]
    public void Branch_stops_are_merged_in_route_order()
    {
        Assert.Equal(["B", "D", "C"], Direction(0).Stops.Select(s => s.Id));
    }

    [Fact]
    public void Stop_that_is_only_ever_first_is_left_out()
    {
        Assert.DoesNotContain(Direction(0).Stops, s => s.Id == "A");
    }

    [Fact]
    public void Stop_first_in_one_pattern_but_not_another_is_kept()
    {
        // B starts the short trip WS, but W1 and W2 reach it later.
        Assert.Equal(["B", "A"], Direction(1).Stops.Select(s => s.Id));
    }

    [Fact]
    public void Lines_by_stop_give_each_direction_serving_it()
    {
        var lines = RouteDirections.LinesByStop(_feeds)["B"].Where(l => l.RouteId == "R").OrderBy(l => l.DirectionId).ToArray();
        Assert.Equal([(0, "East"), (1, "West")], lines.Select(l => (l.DirectionId, l.Label)));
    }

    [Fact]
    public void Line_shape_is_the_most_common_pattern_through_that_stop()
    {
        var lines = RouteDirections.LinesByStop(_feeds);
        Assert.Equal("BRANCH", lines["D"].Single().ShapeId);
        Assert.Equal("EAST", lines["B"].First(l => l.RouteId == "R" && l.DirectionId == 0).ShapeId);
    }

    [Fact]
    public void No_line_for_a_stop_that_only_ever_starts_trips()
    {
        Assert.DoesNotContain(RouteDirections.LinesByStop(_feeds).GetValueOrDefault("A") ?? [], l => l.DirectionId == 0);
    }
}
