using Transit.Backtest;
using Transit.Crossings;
using Transit.Gtfs;

namespace Transit.Eta;

/// <summary>
/// v0 and v1. Predicts a departure by starting from the bus's last known departure and adding, for each segment
/// still ahead, its historical median travel time (scaled by today's pace in v1) plus the median dwell at each
/// stop, including the target's own dwell since the target is its departure.
/// </summary>
public sealed class SegmentEtaPredictor(
    string name, SegmentStats stats, RecentPace? pace, CrossingHistory history, Func<string, TripInfo?> resolveTrip, TimeZoneInfo zone) : IPredictor
{
    /// <summary>Used when a segment has no history at all.</summary>
    public const double DefaultSpeedMetersPerSecond = 7, DefaultDwellSeconds = 8;

    /// <summary>
    /// A crossing becomes known about one ping interval plus the feed lag after it happens, so not having seen the
    /// bus reach the next checkpoint only proves it hadn't by this long ago. Clamping to "now" biased v0 ~40 s late.
    /// </summary>
    public const double KnowledgeLagSeconds = SegmentObservation.FeedLagSeconds + 15;

    public string Name => name;

    public void Fill(IReadOnlyList<BacktestTarget> targets, PredictionTable table)
    {
        var origins = new Dictionary<string, double>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (resolveTrip(t.TripId) is not { } trip) continue;
            var checkpoints = trip.Pattern.Checkpoints;
            var target = Array.FindIndex(checkpoints, c => c.Kind == CheckpointKind.Stop && c.StopSequence == t.StopSequence);
            if (target <= 0) continue;

            if (!origins.TryGetValue(t.ServiceDate, out var origin))
                origins[t.ServiceDate] = origin = StaticFeed.ServiceDayOrigin(t.ServiceDate, zone).ToUnixTimeSeconds();
            var rows = history.Trip(t.ServiceDate, t.TripId);
            var dayType = ServiceCalendar.DayTypeOf(t.ServiceDate);
            var actual = t.Actual.ToUnixTimeMilliseconds() / 1000.0;
            for (var h = 0; h < Horizons.Minutes.Length; h++)
                if (Predict(trip, target, rows, dayType, origin, actual - Horizons.Minutes[h] * 60) is { } p)
                    table[i, h] = p;
        }
    }

    double? Predict(TripInfo trip, int target, CrossingRow[] rows, DayType dayType, double serviceOrigin, double now)
    {
        var checkpoints = trip.Pattern.Checkpoints;

        // Where the bus is, as far as anyone could know at `now`.
        CrossingRow? last = null;
        foreach (var r in rows)
            if (r.Index < target && r.Depart is not null && r.DepartKnownAt <= now && !r.Detour)
                last = r;

        int from;
        double t;
        if (last is not null)
            (from, t) = (last.Index, last.Depart!.Value);
        else
        {
            // Not started (or not seen yet): leave the origin at the scheduled time, or now if that's passed.
            var scheduled = trip.ScheduledDepartures.Length > 0 ? trip.ScheduledDepartures[0] : -1;
            if (scheduled < 0) return null;
            (from, t) = (0, Math.Max(now, serviceOrigin + scheduled));
        }

        var position = checkpoints[from].Dist;
        for (var s = from + 1; s <= target; s++)
        {
            var (prev, cp) = (checkpoints[s - 1], checkpoints[s]);
            var travel = stats.Travel(SegmentObservation.Key(prev.Id, cp.Id), dayType, t)
                ?? Math.Max(0, cp.Entry - prev.Exit) / DefaultSpeedMetersPerSecond;
            if (pace is not null)
                travel *= pace.Factor(checkpoints, s, now, cp.Dist - position);
            var arrive = t + travel;
            // The bus hasn't been seen arriving, so it can't have arrived before the last moment we'd have seen it.
            if (s == from + 1) arrive = Math.Max(arrive, now - KnowledgeLagSeconds);
            var dwell = cp.Kind == CheckpointKind.Stop ? stats.Dwell(cp.Id, dayType, arrive) ?? DefaultDwellSeconds : 0;
            t = arrive + dwell;
        }
        return t;
    }
}
