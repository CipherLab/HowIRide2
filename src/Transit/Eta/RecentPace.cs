using Transit.Crossings;

namespace Transit.Eta;

/// <summary>
/// Today's pace (v1): how recent traversals of a stretch of road compare with their historical medians.
/// Every bus on every route that just drove a segment is a probe for the bus behind it, so snow, a crash or a
/// game letting out show up here without being modeled.
/// </summary>
public sealed class RecentPace
{
    /// <summary>Only traversals that finished within this window before the query count.</summary>
    public static readonly double WindowSeconds = TimeSpan.FromMinutes(30).TotalSeconds;

    /// <summary>Neighbouring segments within this distance along the pattern also inform a segment's pace.</summary>
    public const double NeighbourhoodMeters = 1500;

    /// <summary>Distance scale for weighting neighbours and for fading the effect out with distance ahead of the bus.</summary>
    public const double NeighbourScaleMeters = 750, FadeScaleMeters = 5000;

    /// <summary>Pseudo-observations at ratio 1: with little evidence, assume a normal day.</summary>
    public const double PriorWeight = 2;

    const double MinRatio = 0.5, MaxRatio = 3;

    readonly Dictionary<string, (double KnownAt, double End, double Ratio)[]> _bySegment;

    RecentPace(Dictionary<string, (double, double, double)[]> bySegment) => _bySegment = bySegment;

    public static RecentPace Build(CrossingHistory history, SegmentStats stats) =>
        new(history.Segments()
            .Select(o => (o, expected: stats.Travel(o.Segment, ServiceCalendar.DayTypeOf(o.ServiceDate), o.Start)))
            .Where(x => x.expected is > 1)
            .GroupBy(x => x.o.Segment)
            .ToDictionary(g => g.Key, g => g
                .Select(x => (x.o.KnownAt, x.o.End, Math.Clamp(x.o.Travel / x.expected!.Value, MinRatio, MaxRatio)))
                .OrderBy(x => x.Item1)
                .ToArray()));

    /// <summary>Ratios of traversals of a segment known by <paramref name="now"/> and finished in the last half hour.</summary>
    IEnumerable<double> Recent(string segment, double now)
    {
        if (!_bySegment.TryGetValue(segment, out var obs)) yield break;
        // Sorted by KnownAt; walk back from the newest knowable one.
        var lo = 0;
        var hi = obs.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (obs[mid].KnownAt <= now) lo = mid + 1; else hi = mid;
        }
        for (var i = lo - 1; i >= 0 && obs[i].KnownAt >= now - WindowSeconds - 600; i--)
            if (obs[i].End >= now - WindowSeconds)
                yield return obs[i].Ratio;
    }

    /// <summary>
    /// Multiplier for the expected travel time of checkpoint <paramref name="to"/>'s incoming segment on a pattern:
    /// a distance-weighted blend of recent ratios on it and its neighbours, shrunk toward 1, faded with distance ahead.
    /// </summary>
    public double Factor(Checkpoint[] checkpoints, int to, double now, double distanceAhead)
    {
        var here = checkpoints[to].Dist;
        double weighted = PriorWeight, weight = PriorWeight;
        for (var m = Math.Max(1, to); m < checkpoints.Length && checkpoints[m].Dist - here <= NeighbourhoodMeters; m++)
            Accumulate(m);
        for (var m = to - 1; m >= 1 && here - checkpoints[m].Dist <= NeighbourhoodMeters; m--)
            Accumulate(m);
        var ratio = weighted / weight;
        return 1 + (ratio - 1) * Math.Exp(-Math.Max(0, distanceAhead) / FadeScaleMeters);

        void Accumulate(int m)
        {
            var w = Math.Exp(-Math.Abs(checkpoints[m].Dist - here) / NeighbourScaleMeters);
            foreach (var r in Recent(SegmentObservation.Key(checkpoints[m - 1].Id, checkpoints[m].Id), now))
            {
                weighted += w * r;
                weight += w;
            }
        }
    }
}
