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

    /// <summary>How long past its usual start a trip can go unseen before we assume it left and tracking is what's late.</summary>
    public const double AssumedLeftAfterSeconds = 300;

    /// <summary>How long past its start a trip can go unseen before the next trip in its block stops waiting for it.</summary>
    public const double UnseenTripGraceSeconds = 900;

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
                if (Predict(trip, target, rows, dayType, origin, actual - Horizons.Minutes[h] * 60, id => history.Trip(t.ServiceDate, id)) is { } p)
                    table[i, h] = p;
        }
    }

    /// <summary>
    /// Predicted departure (unix seconds) from checkpoint <paramref name="target"/> as of <paramref name="now"/>,
    /// using only the trip's crossings in <paramref name="rows"/> that were knowable by then. With
    /// <paramref name="rowsOf"/> (crossings of another trip on the same service date) and <see cref="UseBlocks"/>,
    /// a trip that hasn't started waits for its bus to finish the trip before it.
    /// </summary>
    public double? Predict(TripInfo trip, int target, IReadOnlyList<CrossingRow> rows, DayType dayType, double serviceOrigin, double now,
        Func<string, IReadOnlyList<CrossingRow>>? rowsOf = null) =>
        Walk(trip, target, rows, dayType, serviceOrigin, now, UseBlocks ? rowsOf : null, MaxBlockDepth)?.Depart;

    /// <summary>
    /// What <see cref="Predict"/> would say for every stop still ahead of the bus, up to <paramref name="notAfter"/>,
    /// from one walk along the trip instead of one per stop.
    /// </summary>
    public IReadOnlyList<(int StopSequence, double Depart)> PredictAlong(TripInfo trip, IReadOnlyList<CrossingRow> rows, DayType dayType, double serviceOrigin, double now,
        Func<string, IReadOnlyList<CrossingRow>>? rowsOf = null, double notAfter = double.PositiveInfinity)
    {
        var stops = new List<(int, double)>();
        Walk(trip, trip.Pattern.Checkpoints.Length - 1, rows, dayType, serviceOrigin, now, UseBlocks ? rowsOf : null, MaxBlockDepth, notAfter,
            (cp, depart) => { if (cp.Kind == CheckpointKind.Stop) stops.Add((cp.StopSequence, depart)); });
        return stops;
    }

    /// <summary>Whether a trip that hasn't started is held back by the predicted end of the trip before it in its block.</summary>
    public bool UseBlocks { get; init; }

    /// <summary>Shortest turnaround between a bus finishing one trip and starting its next, when it's running late.</summary>
    public double MinLayoverSeconds { get; init; } = 60;

    /// <summary>How many trips back a block chain is followed before falling back to the timetable.</summary>
    public const int MaxBlockDepth = 3;

    (double Arrive, double Depart)? Walk(TripInfo trip, int target, IReadOnlyList<CrossingRow> rows, DayType dayType, double serviceOrigin,
        double now, Func<string, IReadOnlyList<CrossingRow>>? rowsOf, int depth, double notAfter = double.PositiveInfinity, Action<Checkpoint, double>? onDepart = null)
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
            // Not started (or not seen yet): leave the origin when trips from there usually do...
            var scheduled = trip.ScheduledDepartures.Length > 0 ? trip.ScheduledDepartures[0] : -1;
            if (scheduled < 0) return null;
            var earliest = serviceOrigin + scheduled + stats.StartDelay(checkpoints[0].Id, dayType, serviceOrigin + scheduled);
            // ...but not before the bus is back from the trip it's on, unless that trip seems not to have run.
            if (rowsOf is not null && depth > 0 && trip.PreviousInBlock is { } previousId && resolveTrip(previousId) is { } previous
                && !NeverAppeared(previous, rowsOf(previousId), serviceOrigin, now)
                && Finish(previous, rowsOf(previousId), dayType, serviceOrigin, now, rowsOf, depth - 1) is { } finish)
                earliest = Math.Max(earliest, finish + MinLayoverSeconds);
            // Long past that with no sighting, the likelier story is that tracking started late, not that the bus is still waiting.
            (from, t) = (0, now - earliest > AssumedLeftAfterSeconds ? earliest : Math.Max(now, earliest));
        }

        var position = checkpoints[from].Dist;
        var arrive = t;
        for (var s = from + 1; s <= target; s++)
        {
            var (prev, cp) = (checkpoints[s - 1], checkpoints[s]);
            var travel = stats.Travel(SegmentObservation.Key(prev.Id, cp.Id), dayType, t)
                ?? Math.Max(0, cp.Entry - prev.Exit) / DefaultSpeedMetersPerSecond;
            if (pace is not null)
                travel *= pace.Factor(checkpoints, s, now, cp.Dist - position);
            arrive = t + travel;
            // The bus hasn't been seen arriving, so it can't have arrived before the last moment we'd have seen it.
            if (s == from + 1) arrive = Math.Max(arrive, now - KnowledgeLagSeconds);
            var dwell = cp.Kind == CheckpointKind.Stop ? stats.Dwell(cp.Id, dayType, arrive) ?? DefaultDwellSeconds : 0;
            t = arrive + dwell;
            if (t > notAfter) break;
            onDepart?.Invoke(cp, Math.Max(t, now));
        }
        return (arrive, Math.Max(t, now));
    }

    /// <summary>A trip this overdue, with nothing at all known about it, was cancelled or run by another bus.</summary>
    bool NeverAppeared(TripInfo trip, IReadOnlyList<CrossingRow> rows, double serviceOrigin, double now) =>
        trip.ScheduledDepartures is [>= 0 and var first, ..] && now - (serviceOrigin + first) > UnseenTripGraceSeconds
        && !rows.Any(r => r.DepartKnownAt <= now);

    /// <summary>When the bus reaches the last checkpoint of <paramref name="trip"/>: seen, or predicted.</summary>
    double? Finish(TripInfo trip, IReadOnlyList<CrossingRow> rows, DayType dayType, double serviceOrigin, double now,
        Func<string, IReadOnlyList<CrossingRow>> rowsOf, int depth)
    {
        var end = trip.Pattern.Checkpoints.Length - 1;
        foreach (var r in rows)
            if (r.Index == end && r.Arrive is { } seen && seen + (r.ArriveGap ?? 0) + SegmentObservation.FeedLagSeconds <= now)
                return seen;
        return end > 0 ? Walk(trip, end, rows, dayType, serviceOrigin, now, rowsOf, depth)?.Arrive : null;
    }
}
