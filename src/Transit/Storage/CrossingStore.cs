using Microsoft.Data.Sqlite;
using Transit.Crossings;

namespace Transit.Storage;

/// <summary>
/// SQLite store for crossings plus the detector's resume cursor. WAL mode lets analysis read while
/// the detector writes. Rows are keyed by (service_date, trip_id, checkpoint_index), so replaying a
/// stretch of archive rewrites the same rows instead of duplicating them.
/// </summary>
public sealed class CrossingStore : IDisposable
{
    const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        CREATE TABLE IF NOT EXISTS crossings (
            service_date      TEXT    NOT NULL,
            trip_id           TEXT    NOT NULL,
            checkpoint_index  INTEGER NOT NULL,
            route_id          TEXT    NOT NULL,
            direction_id      INTEGER NOT NULL,
            vehicle_id        TEXT    NOT NULL,
            shape_id          TEXT    NOT NULL,
            checkpoint_id     TEXT    NOT NULL,
            prev_checkpoint_id TEXT,
            kind              TEXT    NOT NULL,   -- 'stop' or 'synthetic'
            stop_sequence     INTEGER NOT NULL,
            dist_m            REAL    NOT NULL,
            arrive            REAL,               -- unix seconds; null at a trip's first stop
            depart            REAL,               -- unix seconds; null at a trip's last stop
            arrive_gap_s      REAL,
            depart_gap_s      REAL,
            detour            INTEGER NOT NULL,
            feed_version      TEXT    NOT NULL,
            PRIMARY KEY (service_date, trip_id, checkpoint_index)
        ) WITHOUT ROWID;
        -- Segment history: travel time is this row's arrive minus the previous checkpoint's depart.
        CREATE INDEX IF NOT EXISTS crossings_segment ON crossings (prev_checkpoint_id, checkpoint_id, arrive);
        CREATE INDEX IF NOT EXISTS crossings_arrive ON crossings (arrive);
        CREATE TABLE IF NOT EXISTS cursors (
            feed       TEXT PRIMARY KEY,
            fetched_ms INTEGER NOT NULL
        );
        """;

    readonly SqliteConnection _db;
    readonly SqliteCommand _insert, _setCursor;

    public CrossingStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Execute(Schema);
        Execute("PRAGMA busy_timeout = 5000;");

        _insert = _db.CreateCommand();
        _insert.CommandText = """
            INSERT OR REPLACE INTO crossings VALUES (
                $service_date, $trip_id, $checkpoint_index, $route_id, $direction_id, $vehicle_id, $shape_id,
                $checkpoint_id, $prev_checkpoint_id, $kind, $stop_sequence, $dist_m, $arrive, $depart,
                $arrive_gap_s, $depart_gap_s, $detour, $feed_version)
            """;
        foreach (var name in new[] { "service_date", "trip_id", "checkpoint_index", "route_id", "direction_id", "vehicle_id", "shape_id",
                     "checkpoint_id", "prev_checkpoint_id", "kind", "stop_sequence", "dist_m", "arrive", "depart",
                     "arrive_gap_s", "depart_gap_s", "detour", "feed_version" })
            _insert.Parameters.Add(new SqliteParameter("$" + name, null));

        _setCursor = _db.CreateCommand();
        _setCursor.CommandText = "INSERT OR REPLACE INTO cursors VALUES ($feed, $fetched_ms)";
        _setCursor.Parameters.Add(new SqliteParameter("$feed", null));
        _setCursor.Parameters.Add(new SqliteParameter("$fetched_ms", null));
    }

    public DateTimeOffset? GetCursor(string feed)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT fetched_ms FROM cursors WHERE feed = $feed";
        cmd.Parameters.AddWithValue("$feed", feed);
        return cmd.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    /// <summary>Writes a snapshot's crossings and advances the cursor in one transaction, so a crash never loses or half-writes a snapshot.</summary>
    public void Write(IReadOnlyCollection<StopCrossing> crossings, string feed, DateTimeOffset cursor)
    {
        using var tx = _db.BeginTransaction();
        _insert.Transaction = tx;
        _setCursor.Transaction = tx;
        foreach (var c in crossings)
        {
            var cp = c.Checkpoint;
            var p = _insert.Parameters;
            p["$service_date"].Value = c.ServiceDate;
            p["$trip_id"].Value = c.TripId;
            p["$checkpoint_index"].Value = cp.Index;
            p["$route_id"].Value = c.RouteId;
            p["$direction_id"].Value = c.DirectionId;
            p["$vehicle_id"].Value = c.VehicleId;
            p["$shape_id"].Value = c.Trip.Pattern.ShapeId;
            p["$checkpoint_id"].Value = cp.Id;
            p["$prev_checkpoint_id"].Value = (object?)cp.PreviousId ?? DBNull.Value;
            p["$kind"].Value = cp.Kind == CheckpointKind.Stop ? "stop" : "synthetic";
            p["$stop_sequence"].Value = cp.StopSequence;
            p["$dist_m"].Value = cp.Dist;
            p["$arrive"].Value = Unix(c.Arrive);
            p["$depart"].Value = Unix(c.Depart);
            p["$arrive_gap_s"].Value = (object?)c.ArriveGapSeconds ?? DBNull.Value;
            p["$depart_gap_s"].Value = (object?)c.DepartGapSeconds ?? DBNull.Value;
            p["$detour"].Value = c.Detour ? 1 : 0;
            p["$feed_version"].Value = c.Trip.FeedVersion;
            _insert.ExecuteNonQuery();
        }
        _setCursor.Parameters["$feed"].Value = feed;
        _setCursor.Parameters["$fetched_ms"].Value = cursor.ToUnixTimeMilliseconds();
        _setCursor.ExecuteNonQuery();
        tx.Commit();
    }

    public long Count()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM crossings";
        return (long)cmd.ExecuteScalar()!;
    }

    static object Unix(DateTimeOffset? t) => t is { } v ? v.ToUnixTimeMilliseconds() / 1000.0 : DBNull.Value;

    void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _insert.Dispose();
        _setCursor.Dispose();
        _db.Dispose();
    }
}
