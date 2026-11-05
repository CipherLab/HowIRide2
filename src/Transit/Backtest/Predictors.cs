using Transit.Archive;
using Transit.Gtfs;
using TransitRealtime;
using StopTimeRelationship = TransitRealtime.TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship;
using TripRelationship = TransitRealtime.TripDescriptor.Types.ScheduleRelationship;

namespace Transit.Backtest;

/// <summary>The printed timetable: the same answer at every horizon.</summary>
public sealed class SchedulePredictor(Func<string, TripInfo?> resolveTrip, TimeZoneInfo zone) : IPredictor
{
    public string Name => "schedule";

    public void Fill(IReadOnlyList<BacktestTarget> targets, PredictionTable table)
    {
        var origins = new Dictionary<string, DateTimeOffset>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (resolveTrip(t.TripId) is not { } trip) continue;
            var stop = Array.FindIndex(trip.Pattern.Stops, s => s.Sequence == t.StopSequence);
            if (stop < 0 || stop >= trip.ScheduledDepartures.Length || trip.ScheduledDepartures[stop] < 0) continue;

            if (!origins.TryGetValue(t.ServiceDate, out var origin))
                origins[t.ServiceDate] = origin = StaticFeed.ServiceDayOrigin(t.ServiceDate, zone);
            var scheduled = origin.AddSeconds(trip.ScheduledDepartures[stop]).ToUnixTimeMilliseconds() / 1000.0;
            for (var h = 0; h < Horizons.Minutes.Length; h++)
                table[i, h] = scheduled;
        }
    }
}

/// <summary>
/// Metro Transit's own predictions, replayed from the TripUpdates archive. At each query time only the
/// latest snapshot counts: that is all a rider could see, so a trip that had dropped out of the feed has no prediction.
/// </summary>
public sealed class TripUpdatesPredictor(string feedDir) : IPredictor
{
    /// <summary>A latest snapshot older than this at the query time means our archive has a gap there, not that Metro was silent.</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromSeconds(60);

    public string Name => "metro-tripupdates";

    public long Snapshots { get; private set; }

    public void Fill(IReadOnlyList<BacktestTarget> targets, PredictionTable table) =>
        LatestSnapshot.Fill(targets, table, MaxSnapshotAge, (from, to) => ArchiveFollower.ReadRange(feedDir, from, to).Select(record =>
        {
            Snapshots++;
            return (record.FetchedAt.ToUnixTimeMilliseconds() / 1000.0, Rows(FeedMessage.Parser.ParseFrom(record.Payload)));
        }));

    static IEnumerable<(string Date, string Trip, int Sequence, double Predicted)> Rows(FeedMessage message)
    {
        foreach (var entity in message.Entity)
        {
            if (entity.TripUpdate is not { Trip: { } trip } update || trip.ScheduleRelationship == TripRelationship.Canceled)
                continue;
            foreach (var stu in update.StopTimeUpdate)
            {
                if (!stu.HasStopSequence || stu.ScheduleRelationship != StopTimeRelationship.Scheduled)
                    continue;
                // Metro publishes departure times; arrival is the rare fallback.
                var predicted = stu.Departure is { HasTime: true } d ? d.Time : stu.Arrival is { HasTime: true } a ? a.Time : (long?)null;
                if (predicted is not null)
                    yield return (trip.StartDate, trip.TripId, (int)stu.StopSequence, predicted.Value);
            }
        }
    }
}

/// <summary>
/// Replays recorded prediction snapshots against the targets. At each query time only the latest snapshot counts: that is
/// all a rider could see, so a stop missing from it has no prediction, and a snapshot older than the limit means a gap.
/// </summary>
static class LatestSnapshot
{
    public static void Fill(IReadOnlyList<BacktestTarget> targets, PredictionTable table, TimeSpan maxAge,
        Func<DateTimeOffset, DateTimeOffset, IEnumerable<(double Fetched, IEnumerable<(string Date, string Trip, int Sequence, double Predicted)> Rows)>> read)
    {
        if (targets.Count == 0) return;
        var horizons = Horizons.Minutes.Length;

        var index = new Dictionary<(string Date, string Trip), Dictionary<int, int>>();
        var query = new double[targets.Count * horizons];
        var issued = new double[targets.Count * horizons];
        Array.Fill(issued, double.NaN);
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (!index.TryGetValue((t.ServiceDate, t.TripId), out var stops))
                index[(t.ServiceDate, t.TripId)] = stops = [];
            stops[t.StopSequence] = i;
            var actual = t.Actual.ToUnixTimeMilliseconds() / 1000.0;
            for (var h = 0; h < horizons; h++)
                query[i * horizons + h] = actual - Horizons.Minutes[h] * 60;
        }

        var snapshotTimes = new List<double>();
        foreach (var (fetched, rows) in read(targets.Min(t => t.Actual) - Horizons.Max - maxAge, targets.Max(t => t.Actual)))
        {
            snapshotTimes.Add(fetched);
            foreach (var (date, trip, sequence, predicted) in rows)
            {
                if (!index.TryGetValue((date, trip), out var stops) || !stops.TryGetValue(sequence, out var i))
                    continue;
                for (var h = 0; h < horizons; h++)
                {
                    var k = i * horizons + h;
                    if (fetched > query[k]) continue;
                    table[i, h] = predicted;
                    issued[k] = fetched;
                }
            }
        }

        // Keep a value only if it came from the latest snapshot at the query time, and that snapshot is fresh.
        var times = snapshotTimes.ToArray();
        for (var i = 0; i < targets.Count; i++)
        {
            for (var h = 0; h < horizons; h++)
            {
                var k = i * horizons + h;
                if (double.IsNaN(issued[k])) continue;
                var at = Array.BinarySearch(times, query[k]);
                var latest = times[at >= 0 ? at : ~at - 1];
                if (issued[k] != latest || query[k] - latest > maxAge.TotalSeconds)
                    table[i, h] = double.NaN;
            }
        }
    }
}
