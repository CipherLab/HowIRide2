using Microsoft.Data.Sqlite;

namespace Transit.Backtest;

/// <summary>
/// Caches each completed service day's replayed predictions, so a nightly run only parses the newest day of
/// the TripUpdates archive, and keeps a history of every score produced.
/// </summary>
public sealed class BacktestStore : IDisposable
{
    const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS cached_days (
            predictor    TEXT NOT NULL,
            service_date TEXT NOT NULL,
            computed_at  TEXT NOT NULL,
            PRIMARY KEY (predictor, service_date)
        );
        CREATE TABLE IF NOT EXISTS predictions (
            predictor     TEXT    NOT NULL,
            service_date  TEXT    NOT NULL,
            trip_id       TEXT    NOT NULL,
            stop_sequence INTEGER NOT NULL,
            horizon_min   INTEGER NOT NULL,
            predicted     REAL    NOT NULL,   -- unix seconds
            PRIMARY KEY (predictor, service_date, trip_id, stop_sequence, horizon_min)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS scores (
            run_at       TEXT    NOT NULL,
            test_from    TEXT    NOT NULL,
            test_to      TEXT    NOT NULL,
            predictor    TEXT    NOT NULL,
            grp          TEXT    NOT NULL,
            horizon_min  INTEGER NOT NULL,
            targets      INTEGER NOT NULL,
            predicted    INTEGER NOT NULL,
            coverage     REAL, median_abs_s REAL, p90_abs_s REAL, bias_s REAL,
            within_1m    REAL, within_2m REAL, early_rate REAL
        );
        """;

    readonly SqliteConnection _db;

    public BacktestStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Loads a cached day into <paramref name="table"/> at the given target indices. False if the day was never cached.</summary>
    public bool TryLoad(string predictor, string serviceDate, IReadOnlyList<BacktestTarget> targets, IReadOnlyList<int> indices, PredictionTable table)
    {
        using (var check = _db.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM cached_days WHERE predictor = $p AND service_date = $d";
            check.Parameters.AddWithValue("$p", predictor);
            check.Parameters.AddWithValue("$d", serviceDate);
            if (check.ExecuteScalar() is null) return false;
        }

        var lookup = indices.ToDictionary(i => (targets[i].TripId, targets[i].StopSequence));
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT trip_id, stop_sequence, horizon_min, predicted FROM predictions WHERE predictor = $p AND service_date = $d";
        cmd.Parameters.AddWithValue("$p", predictor);
        cmd.Parameters.AddWithValue("$d", serviceDate);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var h = Array.IndexOf(Horizons.Minutes, r.GetInt32(2));
            if (h >= 0 && lookup.TryGetValue((r.GetString(0), r.GetInt32(1)), out var i))
                table[i, h] = r.GetDouble(3);
        }
        return true;
    }

    public void Save(string predictor, string serviceDate, IReadOnlyList<BacktestTarget> targets, IReadOnlyList<int> indices, PredictionTable table)
    {
        using var tx = _db.BeginTransaction();
        using var insert = _db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT OR REPLACE INTO predictions VALUES ($p, $d, $trip, $seq, $h, $v)";
        var (pTrip, pSeq, pH, pV) = (insert.Parameters.Add("$trip", SqliteType.Text), insert.Parameters.Add("$seq", SqliteType.Integer),
            insert.Parameters.Add("$h", SqliteType.Integer), insert.Parameters.Add("$v", SqliteType.Real));
        insert.Parameters.AddWithValue("$p", predictor);
        insert.Parameters.AddWithValue("$d", serviceDate);
        foreach (var i in indices)
        {
            for (var h = 0; h < Horizons.Minutes.Length; h++)
            {
                if (double.IsNaN(table[i, h])) continue;
                (pTrip.Value, pSeq.Value, pH.Value, pV.Value) = (targets[i].TripId, targets[i].StopSequence, Horizons.Minutes[h], table[i, h]);
                insert.ExecuteNonQuery();
            }
        }
        using var mark = _db.CreateCommand();
        mark.Transaction = tx;
        mark.CommandText = "INSERT OR REPLACE INTO cached_days VALUES ($p, $d, $at)";
        mark.Parameters.AddWithValue("$p", predictor);
        mark.Parameters.AddWithValue("$d", serviceDate);
        mark.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        mark.ExecuteNonQuery();
        tx.Commit();
    }

    public void SaveScores(DateTimeOffset runAt, string testFrom, string testTo, IEnumerable<Score> scores)
    {
        using var tx = _db.BeginTransaction();
        using var insert = _db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO scores VALUES ($run, $from, $to, $p, $g, $h, $n, $k, $cov, $med, $p90, $bias, $w1, $w2, $early)";
        foreach (var s in scores)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$run", runAt.ToString("O"));
            insert.Parameters.AddWithValue("$from", testFrom);
            insert.Parameters.AddWithValue("$to", testTo);
            insert.Parameters.AddWithValue("$p", s.Predictor);
            insert.Parameters.AddWithValue("$g", s.Group);
            insert.Parameters.AddWithValue("$h", s.HorizonMinutes);
            insert.Parameters.AddWithValue("$n", s.Targets);
            insert.Parameters.AddWithValue("$k", s.Predicted);
            foreach (var (name, v) in new[] { ("$cov", s.Coverage), ("$med", s.MedianAbsError), ("$p90", s.P90AbsError), ("$bias", s.Bias),
                         ("$w1", s.Within1Min), ("$w2", s.Within2Min), ("$early", s.EarlyRate) })
                insert.Parameters.AddWithValue(name, double.IsNaN(v) ? DBNull.Value : v);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Dispose() => _db.Dispose();
}
