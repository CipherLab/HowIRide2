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

    const double MetersPerDegLat = 111_195;
    static readonly double MetersPerDegLon = MetersPerDegLat * Math.Cos(45 * Math.PI / 180);

    /// <summary>An east-west street 2 km long, a point every 10 m, with an optional north-south wobble.</summary>
    static ShapeGeometry Street(double lat, Func<int, double>? wobbleMeters = null)
    {
        var n = 201;
        var lats = Enumerable.Range(0, n).Select(i => lat + (wobbleMeters?.Invoke(i) ?? 0) / MetersPerDegLat).ToArray();
        var lons = Enumerable.Range(0, n).Select(i => -93.30 + i * 10 / MetersPerDegLon).ToArray();
        return new ShapeGeometry("s", lats, lons, Enumerable.Range(0, n).Select(i => i * 10.0).ToArray());
    }

    [Fact]
    public void GridCheckpointsMatchAcrossRoutesWithDifferentStops()
    {
        var street = Street(44.9773);
        var local = CheckpointBuilder.BuildGrid([new("A", 1, 0), new("B", 2, 450), new("C", 3, 1270), new("D", 4, 2000)], street);
        var limited = CheckpointBuilder.BuildGrid([new("A", 1, 0), new("X", 2, 1010), new("D", 3, 2000)], street);

        var edges = local.Where(c => c.Kind == CheckpointKind.Synthetic).ToList();
        Assert.InRange(edges.Count, 15, 20); // ~20 cells crossed, a few dropped inside stop zones
        // Every edge clear of the limited route's stops is the same checkpoint on both routes.
        foreach (var e in edges.Where(e => limited.Where(c => c.Kind == CheckpointKind.Stop).All(s => Math.Abs(s.Dist - e.Dist) > 40)))
            Assert.Contains(limited, c => c.Id == e.Id && Math.Abs(c.Dist - e.Dist) < 1e-6);
        Assert.Equal(Enumerable.Range(0, local.Length), local.Select(c => c.Index));
        Assert.True(local.Zip(local.Skip(1)).All(p => p.First.Dist < p.Second.Dist));
    }

    [Fact]
    public void GridIgnoresAShapeWobblingAlongACellEdge()
    {
        // Exactly on a grid line, zigzagging 3 m either side of it every 10 m.
        var onEdge = Math.Round(44.9773 * MetersPerDegLat / CheckpointBuilder.GridMeters) * CheckpointBuilder.GridMeters / MetersPerDegLat;
        var cps = CheckpointBuilder.BuildGrid([new("A", 1, 0), new("D", 2, 2000)], Street(onEdge, i => i % 2 == 0 ? 3 : -3));

        Assert.InRange(cps.Count(c => c.Kind == CheckpointKind.Synthetic), 15, 22);
    }

    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData("1,\"Chicago Ave S & 8th St S, Farside\",x", new[] { "1", "Chicago Ave S & 8th St S, Farside", "x" })]
    [InlineData("\"say \"\"hi\"\"\",,", new[] { "say \"hi\"", "", "" })]
    public void SplitsCsvFields(string line, string[] expected) => Assert.Equal(expected, Csv.Split(line));
}
