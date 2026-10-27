using Transit.Gtfs;

namespace Transit.Crossings;

public enum CheckpointKind { Stop, Synthetic }

/// <summary>
/// A point along a pattern where crossing times are recorded. Stops have a zone [Entry, Exit]
/// around them: entering it is arrival, leaving it is departure, the time inside is dwell.
/// Synthetic checkpoints have Entry == Exit and just split long stop-to-stop runs.
/// </summary>
/// <param name="Id">Stop id, or "{fromStop}>{toStop}#{k}" for synthetics, so the same physical point gets the same id on every route.</param>
/// <param name="StopSequence">The stop's sequence, or for a synthetic the sequence of the stop before it.</param>
/// <param name="PreviousId">The checkpoint before this one on the pattern; (PreviousId, Id) names the physical segment just travelled.</param>
public sealed record Checkpoint(int Index, string Id, CheckpointKind Kind, int StopSequence, double Dist, double Entry, double Exit, string? PreviousId);

public static class CheckpointBuilder
{
    /// <summary>Half-width of a stop zone. Shrinks to half the gap when stops are closer than 60 m.</summary>
    public const double StopRadius = 30;

    /// <summary>Target spacing for synthetic checkpoints, added only where stops are more than twice this far apart.</summary>
    public const double SyntheticSpacing = 300;

    public static Checkpoint[] Build(IReadOnlyList<StopPoint> stops)
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
}
