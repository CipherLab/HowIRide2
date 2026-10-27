using Transit.Crossings;
using Transit.Geo;
using Transit.Gtfs;

namespace Transit.Tests;

public sealed class GeometryTests
{
    // East along y=0 to x=1000, a 20 m jog north, then back west along y=20: an out-and-back on parallel streets.
    static readonly Polyline OutAndBack = new([0, 1000, 1000, 0], [0, 0, 20, 20], [0, 1000, 1020, 2020]);

    [Fact]
    public void MatchesPerpendicularFoot()
    {
        var line = new Polyline([0, 1000], [0, 0], [0, 1000]);
        var m = line.Match(250, 10)!.Value;
        Assert.Equal(250, m.Distance, 6);
        Assert.Equal(10, m.Offset, 6);
    }

    [Fact]
    public void UnrestrictedMatchPicksNearestLeg()
    {
        // 15 m from the outbound leg, 5 m from the return leg.
        Assert.Equal(1720, OutAndBack.Match(300, 15)!.Value.Distance, 6);
    }

    [Fact]
    public void WindowKeepsMatchOnExpectedLeg()
    {
        var m = OutAndBack.Match(300, 15, minDist: 0, maxDist: 900)!.Value;
        Assert.Equal(300, m.Distance, 6);
        Assert.Equal(15, m.Offset, 6);
    }

    [Fact]
    public void WindowClampsWithinASegment()
    {
        var line = new Polyline([0, 1000], [0, 0], [0, 1000]);
        Assert.Equal(400, line.Match(800, 0, minDist: 200, maxDist: 400)!.Value.Distance, 6);
    }

    [Fact]
    public void AddsSyntheticCheckpointsOnlyOnLongGaps()
    {
        var cps = CheckpointBuilder.Build([new("A", 1, 0), new("B", 2, 400), new("C", 3, 1400)]);

        Assert.Equal(["A", "B", "B>C#1", "B>C#2", "C"], cps.Select(c => c.Id));
        Assert.Equal([0, 400, 400 + 1000.0 / 3, 400 + 2000.0 / 3, 1400], cps.Select(c => c.Dist));
        Assert.All(cps.Where(c => c.Kind == CheckpointKind.Synthetic), c => Assert.Equal(c.Entry, c.Exit));
        Assert.Equal([0, 1, 2, 3, 4], cps.Select(c => c.Index));
    }

    [Fact]
    public void StopZoneShrinksWhenStopsAreClose()
    {
        var cps = CheckpointBuilder.Build([new("A", 1, 0), new("B", 2, 40), new("C", 3, 500)]);

        Assert.Equal((20.0, 60.0), (cps[1].Entry, cps[1].Exit));
        Assert.Equal((470.0, 530.0), (cps[2].Entry, cps[2].Exit));
    }

    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData("1,\"Chicago Ave S & 8th St S, Farside\",x", new[] { "1", "Chicago Ave S & 8th St S, Farside", "x" })]
    [InlineData("\"say \"\"hi\"\"\",,", new[] { "say \"hi\"", "", "" })]
    public void SplitsCsvFields(string line, string[] expected) => Assert.Equal(expected, Csv.Split(line));
}
