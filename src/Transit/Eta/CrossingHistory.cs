using Microsoft.Data.Sqlite;

namespace Transit.Eta;

/// <summary>A stored crossing; times are unix seconds.</summary>
public sealed record CrossingRow(
    string ServiceDate, string TripId, string RouteId, int Index, string CheckpointId, string? PreviousId, bool IsStop,
    double Dist, double? Arrive, double? Depart, double? ArriveGap, double? DepartGap, bool Detour)
{
    /// <summary>
    /// When an app could first have known this departure: it's interpolated toward the next ping, which
    /// reaches us after the feed's own lag. Using anything earlier would leak the future into a backtest.
    /// </summary>
    public double? DepartKnownAt => Depart + (DepartGap ?? 0) + SegmentObservation.FeedLagSeconds;

    public static CrossingRow From(Crossings.StopCrossing c) => new(
        c.ServiceDate, c.TripId, c.RouteId, c.Checkpoint.Index, c.Checkpoint.Id, c.Checkpoint.PreviousId,
        c.Checkpoint.Kind == Crossings.CheckpointKind.Stop, c.Checkpoint.Dist,
        c.Arrive?.ToUnixTimeMilliseconds() / 1000.0, c.Depart?.ToUnixTimeMilliseconds() / 1000.0,
        c.ArriveGapSeconds, c.DepartGapSeconds, c.Detour);
}

/// <summary>Travel over one segment by one trip: departure from the previous checkpoint to arrival at this one.</summary>
public readonly record struct SegmentObservation(string Segment, string ServiceDate, double Start, double End, double KnownAt)
{
    /// <summary>Typical delay between a vehicle's GPS fix and the feed serving it (median 38 s measured).</summary>
    public const double FeedLagSeconds = 40;

    public double Travel => End - Start;

    public static string Key(string fromCheckpoint, string toCheckpoint) => fromCheckpoint + "|" + toCheckpoint;
}

/// <summary>Crossings loaded from the detect store, indexed by trip.</summary>
public sealed class CrossingHistory
{
    readonly Dictionary<(string Date, string Trip), CrossingRow[]> _byTrip;

    CrossingHistory(Dictionary<(string, string), CrossingRow[]> byTrip) => _byTrip = byTrip;

    public int Trips => _byTrip.Count;

    /// <summary>This trip's crossings ordered by checkpoint index, or empty.</summary>
    public CrossingRow[] Trip(string serviceDate, string tripId) => _byTrip.GetValueOrDefault((serviceDate, tripId)) ?? [];

    /// <summary>The trips <paramref name="keep"/> (service date, trip id) accepts.</summary>
    public CrossingHistory Where(Func<string, string, bool> keep) =>
        new(_byTrip.Where(kv => keep(kv.Key.Date, kv.Key.Trip)).ToDictionary(kv => kv.Key, kv => kv.Value));

    public static CrossingHistory From(IEnumerable<CrossingRow> rows) =>
        new(rows.OrderBy(r => r.Index).GroupBy(x => (x.ServiceDate, x.TripId)).ToDictionary(g => g.Key, g => g.ToArray()));

    public static CrossingHistory Load(string dbPath, string fromServiceDate, string toServiceDate)
    {
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT service_date, trip_id, route_id, checkpoint_index, checkpoint_id, prev_checkpoint_id, kind = 'stop',
                   dist_m, arrive, depart, arrive_gap_s, depart_gap_s, detour
            FROM crossings WHERE service_date BETWEEN $from AND $to
            ORDER BY service_date, trip_id, checkpoint_index
            """;
        cmd.Parameters.AddWithValue("$from", fromServiceDate);
        cmd.Parameters.AddWithValue("$to", toServiceDate);
        using var r = cmd.ExecuteReader();
        var rows = new List<CrossingRow>();
        while (r.Read())
            rows.Add(new CrossingRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6), r.GetDouble(7), N(r, 8), N(r, 9), N(r, 10), N(r, 11), r.GetBoolean(12)));
        return new CrossingHistory(rows.GroupBy(x => (x.ServiceDate, x.TripId)).ToDictionary(g => g.Key, g => g.ToArray()));

        static double? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    /// <summary>Every clean segment traversal: consecutive checkpoints on one trip, both times known, no detour, under 30 minutes.</summary>
    public IEnumerable<SegmentObservation> Segments()
    {
        foreach (var rows in _byTrip.Values)
        {
            for (var i = 1; i < rows.Length; i++)
            {
                var (p, c) = (rows[i - 1], rows[i]);
                if (c.Index != p.Index + 1 || p.Detour || c.Detour || p.Depart is not { } start || c.Arrive is not { } end)
                    continue;
                if (end < start || end - start > 1800) continue;
                yield return new SegmentObservation(SegmentObservation.Key(p.CheckpointId, c.CheckpointId), c.ServiceDate, start, end,
                    end + (c.ArriveGap ?? 0) + SegmentObservation.FeedLagSeconds);
            }
        }
    }

    /// <summary>Every clean dwell: time inside a stop zone, excluding trip ends and holds over 15 minutes.</summary>
    public IEnumerable<(string CheckpointId, string ServiceDate, double Arrive, double Dwell)> Dwells()
    {
        foreach (var rows in _byTrip.Values)
            foreach (var r in rows)
                if (r.IsStop && !r.Detour && r.Arrive is { } a && r.Depart is { } d && d >= a && d - a <= 900)
                    yield return (r.CheckpointId, r.ServiceDate, a, d - a);
    }
}
