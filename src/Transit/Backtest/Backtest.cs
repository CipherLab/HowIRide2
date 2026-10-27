using Microsoft.Data.Sqlite;

namespace Transit.Backtest;

/// <summary>
/// One observed departure to score predictions against: the moment the bus left the stop's zone.
/// Departure, not arrival, because it's what Metro Transit's TripUpdates predict and what a rider boards on.
/// </summary>
public readonly record struct BacktestTarget(string ServiceDate, string TripId, int StopSequence, string StopId, string RouteId, DateTimeOffset Actual);

public static class Horizons
{
    /// <summary>Minutes before the actual departure at which each predictor's answer is looked up.</summary>
    public static readonly int[] Minutes = [2, 5, 10, 15, 20, 30];

    public static TimeSpan Max => TimeSpan.FromMinutes(Minutes[^1]);
}

/// <summary>Predicted departure (unix seconds) per target and horizon; NaN where the predictor had nothing to say.</summary>
public sealed class PredictionTable(int targets)
{
    readonly double[] _values = Enumerable.Repeat(double.NaN, targets * Horizons.Minutes.Length).ToArray();

    public int Targets { get; } = targets;

    public double this[int target, int horizon]
    {
        get => _values[target * Horizons.Minutes.Length + horizon];
        set => _values[target * Horizons.Minutes.Length + horizon] = value;
    }
}

public interface IPredictor
{
    string Name { get; }

    /// <summary>Fills in what this predictor would have said at each horizon before each target, using only information available then.</summary>
    void Fill(IReadOnlyList<BacktestTarget> targets, PredictionTable table);
}

public static class BacktestTargets
{
    /// <summary>
    /// Departures from the crossings store for service dates in [from, to]. Excludes a trip's first and last stops
    /// (layovers), stops where tracking began inside the zone, and detoured crossings.
    /// </summary>
    public static List<BacktestTarget> Load(string dbPath, string fromServiceDate, string toServiceDate)
    {
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT service_date, trip_id, stop_sequence, checkpoint_id, route_id, depart
            FROM crossings
            WHERE kind = 'stop' AND detour = 0 AND arrive IS NOT NULL AND depart IS NOT NULL
              AND service_date BETWEEN $from AND $to
            ORDER BY depart
            """;
        cmd.Parameters.AddWithValue("$from", fromServiceDate);
        cmd.Parameters.AddWithValue("$to", toServiceDate);
        using var r = cmd.ExecuteReader();
        var targets = new List<BacktestTarget>();
        while (r.Read())
            targets.Add(new BacktestTarget(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(r.GetDouble(5) * 1000))));
        return targets;
    }
}

/// <summary>Accuracy of one predictor at one horizon over a group of targets. Errors are predicted minus actual, in seconds.</summary>
/// <param name="Coverage">Share of targets the predictor had an answer for.</param>
/// <param name="Bias">Mean error; positive means buses tend to leave earlier than predicted.</param>
/// <param name="EarlyRate">Bus left more than 60 s before the predicted time: the miss riders hate most.</param>
public sealed record Score(
    string Predictor, string Group, int HorizonMinutes, int Targets, int Predicted, double Coverage,
    double MedianAbsError, double P90AbsError, double Bias, double Within1Min, double Within2Min, double EarlyRate);

public static class Scorer
{
    public static IEnumerable<Score> Compute(string predictor, IReadOnlyList<BacktestTarget> targets, PredictionTable table, Func<BacktestTarget, string> group)
    {
        var groups = Enumerable.Range(0, targets.Count).GroupBy(i => group(targets[i]));
        foreach (var g in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            for (var h = 0; h < Horizons.Minutes.Length; h++)
            {
                var errors = g.Select(i => table[i, h] - targets[i].Actual.ToUnixTimeMilliseconds() / 1000.0)
                    .Where(e => !double.IsNaN(e))
                    .ToArray();
                var n = g.Count();
                if (errors.Length == 0)
                {
                    yield return new Score(predictor, g.Key, Horizons.Minutes[h], n, 0, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
                    continue;
                }
                var abs = errors.Select(Math.Abs).Order().ToArray();
                yield return new Score(predictor, g.Key, Horizons.Minutes[h], n, errors.Length, (double)errors.Length / n,
                    Quantile(abs, 0.5), Quantile(abs, 0.9), errors.Average(),
                    Share(abs, a => a <= 60), Share(abs, a => a <= 120), Share(errors, e => e > 60));
            }
        }
    }

    static double Quantile(double[] sorted, double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(p * sorted.Length))];

    static double Share(double[] values, Func<double, bool> predicate) => (double)values.Count(predicate) / values.Length;
}
