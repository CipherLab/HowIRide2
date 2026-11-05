using System.Text.Json;
using Transit.Archive;
using Transit.Eta;

namespace Transit.Backtest;

/// <summary>
/// How far ahead of a predicted departure a rider should be at the stop. Learned from what the live API actually
/// showed: a prediction is a median, so about a third of buses leave before it, and this is the time they mostly don't.
/// </summary>
public sealed class BeByHistogram
{
    /// <summary>The share of riders following the time who may miss the bus, in a route's or the shared table.</summary>
    public const double Risk = 0.05;

    /// <summary>Samples a route's cell needs before it can raise that route's margin.</summary>
    public const int MinRouteSamples = 100;

    /// <summary>Samples a shared cell needs before it's trusted over its neighbours.</summary>
    const int MinSharedSamples = 100;

    const int BucketSeconds = 10, MaxSeconds = 1800, Buckets = 2 * MaxSeconds / BucketSeconds + 1;

    /// <summary>Upper edges of the bins of time shown to the rider, in seconds. Early misses grow with it.</summary>
    static readonly double[] LeadEdges = [120, 300, 600, 900, 1200, 1800, 2700];

    readonly long[][][] _shared = NewTable();
    readonly Dictionary<string, long[][][]> _byRoute = [];

    static long[][][] NewTable() => [.. Enumerable.Range(0, 2).Select(_ => Enumerable.Range(0, LeadEdges.Length + 1).Select(_ => new long[Buckets]).ToArray())];

    internal static int Bin(double lead)
    {
        for (var i = 0; i < LeadEdges.Length; i++)
            if (lead < LeadEdges[i]) return i;
        return LeadEdges.Length;
    }

    /// <param name="residual">Actual departure minus the prediction shown; negative means the bus left first.</param>
    public void Add(string route, bool onTrip, double lead, double residual)
    {
        var (state, bin) = (onTrip ? 1 : 0, Bin(lead));
        // Rounds toward the earlier bucket, so a margin read back from it never falls short of the sample.
        var bucket = (int)Math.Clamp(Math.Floor((residual + MaxSeconds) / BucketSeconds), 0, Buckets - 1);
        _shared[state][bin][bucket]++;
        if (!_byRoute.TryGetValue(route, out var table)) _byRoute[route] = table = NewTable();
        table[state][bin][bucket]++;
    }

    public BeByMargins Fit(string from = "", string to = "")
    {
        var shared = _shared.Select(FillGaps).ToArray();
        var routes = new Dictionary<string, int[][]>();
        foreach (var (route, table) in _byRoute)
        {
            var own = table.Select((bins, s) => bins.Select((h, b) => Math.Max(shared[s][b], h.Sum() >= MinRouteSamples ? MarginOf(h) : 0)).ToArray()).ToArray();
            if (own.Where((bins, s) => !bins.SequenceEqual(shared[s])).Any())
                routes[route] = own;
        }
        return new BeByMargins(new BeByMargins.Table(from, to, _shared.Sum(s => s.Sum(b => b.Sum())), shared, routes));
    }

    /// <summary>Each bin's margin; a bin without enough samples takes the nearest shorter one that has them, else the nearest longer.</summary>
    static int[] FillGaps(long[][] bins)
    {
        var margins = bins.Select(h => h.Sum() >= MinSharedSamples ? MarginOf(h) : -1).ToArray();
        for (var b = 1; b < margins.Length; b++)
            if (margins[b] < 0) margins[b] = margins[b - 1];
        for (var b = margins.Length - 2; b >= 0; b--)
            if (margins[b] < 0) margins[b] = margins[b + 1];
        return margins.Select(m => Math.Max(0, m)).ToArray();
    }

    static int MarginOf(long[] histogram)
    {
        var needed = Risk * histogram.Sum();
        long seen = 0;
        for (var i = 0; i < Buckets; i++)
        {
            seen += histogram[i];
            if (seen >= needed) return Math.Max(0, MaxSeconds - i * BucketSeconds);
        }
        return 0;
    }
}

/// <summary>Learned margins, by route, whether the trip had started, and the time shown.</summary>
public sealed class BeByMargins
{
    internal sealed record Table(string From, string To, long Samples, int[][] Shared, Dictionary<string, int[][]> Routes);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly Table _table;

    internal BeByMargins(Table table) => _table = table;

    public string From => _table.From;
    public string To => _table.To;
    public long Samples => _table.Samples;

    /// <summary>Seconds ahead of the prediction. A route can raise the shared margin but never lower it: one good day isn't evidence.</summary>
    public int Margin(string route, bool onTrip, double lead)
    {
        var table = _table.Routes.TryGetValue(route, out var own) ? own : _table.Shared;
        return table[onTrip ? 1 : 0][BeByHistogram.Bin(lead)];
    }

    /// <summary>When to be at the stop, as a unix time; never before now.</summary>
    public double BeBy(string route, bool onTrip, double predicted, double now) =>
        Math.Max(now, predicted - Margin(route, onTrip, predicted - now));

    /// <summary>Replaces the file in one step, because the API may be reading it.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_table, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Null when there's nothing usable: the app then simply doesn't offer the time.</summary>
    public static BeByMargins? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Table>(File.ReadAllText(path), Json) is { Shared: not null, Routes: not null } table
                ? new BeByMargins(table)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Turns the live prediction log and the departures that followed into samples for <see cref="BeByHistogram"/>.</summary>
public static class BeByFit
{
    /// <summary>Consecutive samples are near-identical, so one in this many teaches as much for a fifth of the work on the Pi.</summary>
    const int EverySample = 5;

    /// <summary>Calls <paramref name="onSample"/> with (route, on trip, lead, residual) for each stop a rider could still have caught.</summary>
    public static void Visit(double now, IReadOnlyList<LiveTripPrediction> trips, Func<string, int, (string Route, double Actual)?> departure,
        Func<string, double> firstKnown, Action<string, bool, double, double> onSample)
    {
        foreach (var trip in trips)
        {
            foreach (var (sequence, depart) in trip.Stops)
            {
                if (departure(trip.TripId, sequence) is not { } seen || seen.Actual <= now) continue;
                onSample(seen.Route, now >= firstKnown(trip.TripId), depart - now, seen.Actual - depart);
            }
        }
    }

    /// <summary>Visits one service date of the log against that date's observed departures.</summary>
    public static void VisitDay(string dbPath, string logDir, string serviceDate, Action<string, bool, double, double> onSample)
    {
        var targets = BacktestTargets.Load(dbPath, serviceDate, serviceDate);
        if (targets.Count == 0) return;
        var departures = targets.ToDictionary(t => (t.TripId, t.StopSequence), t => (t.RouteId, Actual: t.Actual.ToUnixTimeMilliseconds() / 1000.0));
        var history = CrossingHistory.Load(dbPath, serviceDate, serviceDate);
        var firstKnown = new Dictionary<string, double>();
        foreach (var trip in departures.Keys.Select(k => k.TripId).Distinct())
            firstKnown[trip] = history.Trip(serviceDate, trip).Select(r => r.DepartKnownAt ?? double.PositiveInfinity).DefaultIfEmpty(double.PositiveInfinity).Min();

        var count = 0;
        foreach (var record in ArchiveFollower.ReadRange(logDir, targets.Min(t => t.Actual) - TimeSpan.FromHours(1), targets.Max(t => t.Actual)))
        {
            if (count++ % EverySample != 0) continue;
            var trips = LivePredictionLog.Decode(record.Payload).Where(t => t.ServiceDate == serviceDate).ToList();
            Visit(record.FetchedAt.ToUnixTimeMilliseconds() / 1000.0, trips,
                (trip, sequence) => departures.TryGetValue((trip, sequence), out var d) ? d : null,
                trip => firstKnown.GetValueOrDefault(trip, double.PositiveInfinity), onSample);
        }
    }
}
