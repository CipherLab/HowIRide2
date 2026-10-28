using Transit.Gtfs;

namespace Transit.Crossings;

public enum CheckpointKind { Stop, Synthetic }

/// <summary>
/// A point along a pattern where crossing times are recorded. Stops have a zone [Entry, Exit]
/// around them: entering it is arrival, leaving it is departure, the time inside is dwell.
/// Synthetic checkpoints have Entry == Exit and just split long stop-to-stop runs.
/// </summary>
/// <param name="Id">
/// Stop id, or for a synthetic either "{fromStop}>{toStop}#{k}" (spaced) or "g{x},{y}>{x},{y}" (grid: the edge between two
/// map cells, crossed in that direction), so the same physical point gets the same id on every route.
/// </param>
/// <param name="StopSequence">The stop's sequence, or for a synthetic the sequence of the stop before it.</param>
/// <param name="PreviousId">The checkpoint before this one on the pattern; (PreviousId, Id) names the physical segment just travelled.</param>
public sealed record Checkpoint(int Index, string Id, CheckpointKind Kind, int StopSequence, double Dist, double Entry, double Exit, string? PreviousId);

public static class CheckpointBuilder
{
    /// <summary>Half-width of a stop zone. Shrinks to half the gap when stops are closer than 60 m.</summary>
    public const double StopRadius = 30;

    /// <summary>Target spacing for synthetic checkpoints, added only where stops are more than twice this far apart.</summary>
    public const double SyntheticSpacing = 300;

    /// <summary>
    /// "grid" times buses where their shape crosses the edges of a fixed map grid, so every route on a street shares
    /// segments with the others; anything else spaces synthetics evenly between stops, unique to each stop pair.
    /// Every process reading a crossings database must use the mode that wrote it.
    /// </summary>
    public static readonly string Mode = Environment.GetEnvironmentVariable("TRANSIT_CHECKPOINTS") ?? "spaced";

    /// <summary>Grid cell size. About the distance a bus covers between GPS fixes, so finer would mostly be interpolation.</summary>
    public const double GridMeters = 100;

    /// <summary>How far past a cell edge the shape must go before it counts as in the next cell.</summary>
    public const double EdgeMargin = 10;

    const double SampleMeters = 5;
    const double MetersPerDegLat = 111_195;

    /// <summary>One east-west scale for the whole region (45°N, the Twin Cities), so every shape lands on the same grid.</summary>
    static readonly double MetersPerDegLon = MetersPerDegLat * Math.Cos(45 * Math.PI / 180);

    public static Checkpoint[] Build(IReadOnlyList<StopPoint> stops, ShapeGeometry? geometry = null) =>
        Mode == "grid" && geometry is not null && stops.Count > 1 ? BuildGrid(stops, geometry) : BuildSpaced(stops);

    public static Checkpoint[] BuildSpaced(IReadOnlyList<StopPoint> stops)
    {
        var result = new List<Checkpoint>();
        for (var i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            var before = i > 0 ? stop.Dist - stops[i - 1].Dist : double.PositiveInfinity;
            var after = i < stops.Count - 1 ? stops[i + 1].Dist - stop.Dist : double.PositiveInfinity;
            var radius = Math.Min(StopRadius, Math.Min(before, after) / 2);
            result.Add(new Checkpoint(result.Count, stop.StopId, CheckpointKind.Stop, stop.Sequence, stop.Dist, stop.Dist - radius, stop.Dist + radius, Previous()));

            if (i == stops.Count - 1 || after <= 2 * SyntheticSpacing)
                continue;
            var next = stops[i + 1];
            var count = (int)Math.Round(after / SyntheticSpacing) - 1;
            for (var k = 1; k <= count; k++)
            {
                var dist = stop.Dist + after * k / (count + 1);
                result.Add(new Checkpoint(result.Count, $"{stop.StopId}>{next.StopId}#{k}", CheckpointKind.Synthetic, stop.Sequence, dist, dist, dist, Previous()));
            }
        }
        return [.. result];

        string? Previous() => result.Count > 0 ? result[^1].Id : null;
    }

    public static Checkpoint[] BuildGrid(IReadOnlyList<StopPoint> stops, ShapeGeometry geometry)
    {
        // Stop zones, as in the spaced layout.
        var zones = new (double Entry, double Exit)[stops.Count];
        for (var i = 0; i < stops.Count; i++)
        {
            var before = i > 0 ? stops[i].Dist - stops[i - 1].Dist : double.PositiveInfinity;
            var after = i < stops.Count - 1 ? stops[i + 1].Dist - stops[i].Dist : double.PositiveInfinity;
            var radius = Math.Min(StopRadius, Math.Min(before, after) / 2);
            zones[i] = (stops[i].Dist - radius, stops[i].Dist + radius);
        }

        // Walk the shape from first stop to last, noting which cell each sample is in. An axis only moves to the next
        // cell once the shape is EdgeMargin past the edge, so a line wobbling along an edge stays put.
        var clean = new List<((int X, int Y) Cell, double From)>();
        int Step(int current, double coordinate)
        {
            var margin = EdgeMargin / GridMeters;
            return coordinate > current + 1 + margin || coordinate < current - margin ? (int)Math.Floor(coordinate) : current;
        }
        for (var d = stops[0].Dist; d <= stops[^1].Dist; d += SampleMeters)
        {
            var (lat, lon) = At(geometry, d);
            var (x, y) = (lon * MetersPerDegLon / GridMeters, lat * MetersPerDegLat / GridMeters);
            if (clean.Count == 0)
                clean.Add((((int)Math.Floor(x), (int)Math.Floor(y)), d));
            else if ((Step(clean[^1].Cell.X, x), Step(clean[^1].Cell.Y, y)) is var cell && cell != clean[^1].Cell)
                clean.Add((cell, d));
        }

        // Edges between cells, kept only outside stop zones (a stop's own zone already times that stretch).
        var edges = new List<(double Dist, string Id)>();
        for (var i = 1; i < clean.Count; i++)
        {
            var dist = clean[i].From;
            if (zones.Any(z => dist >= z.Entry - SampleMeters && dist <= z.Exit + SampleMeters)) continue;
            var (a, b) = (clean[i - 1].Cell, clean[i].Cell);
            edges.Add((dist, $"g{a.X},{a.Y}>{b.X},{b.Y}"));
        }

        var result = new List<Checkpoint>();
        var e = 0;
        for (var i = 0; i < stops.Count; i++)
        {
            result.Add(new Checkpoint(result.Count, stops[i].StopId, CheckpointKind.Stop, stops[i].Sequence, stops[i].Dist, zones[i].Entry, zones[i].Exit, Previous()));
            for (; e < edges.Count && (i == stops.Count - 1 || edges[e].Dist < stops[i + 1].Dist); e++)
                if (edges[e].Dist > zones[i].Exit)
                    result.Add(new Checkpoint(result.Count, edges[e].Id, CheckpointKind.Synthetic, stops[i].Sequence, edges[e].Dist, edges[e].Dist, edges[e].Dist, Previous()));
        }
        return [.. result];

        string? Previous() => result.Count > 0 ? result[^1].Id : null;
    }

    /// <summary>The shape's position at distance <paramref name="d"/> along it.</summary>
    static (double Lat, double Lon) At(ShapeGeometry g, double d)
    {
        var i = Array.BinarySearch(g.Dist, d);
        if (i >= 0) return (g.Lat[i], g.Lon[i]);
        i = ~i;
        if (i == 0) return (g.Lat[0], g.Lon[0]);
        if (i >= g.Dist.Length) return (g.Lat[^1], g.Lon[^1]);
        var f = g.Dist[i] > g.Dist[i - 1] ? (d - g.Dist[i - 1]) / (g.Dist[i] - g.Dist[i - 1]) : 0;
        return (g.Lat[i - 1] + f * (g.Lat[i] - g.Lat[i - 1]), g.Lon[i - 1] + f * (g.Lon[i] - g.Lon[i - 1]));
    }
}
