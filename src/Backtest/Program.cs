using System.Diagnostics;
using System.Globalization;
using Transit.Backtest;
using Transit.Eta;
using Transit.Gtfs;

// Scores each predictor against observed departures, by how far ahead it was predicting.
// Models that learn (metro+bias) are fit only on targets before the test window and scored only inside it.
const string Usage = """
    Usage: backtest --data <dir> --db <crossings.db> (--test <from>[..<to>] | --yesterday) [options]
      --data <dir>         ingest DataDir (raw/ and static/)
      --db <path>          crossings.db from the detect service
      --test <from>[..to]  service dates yyyyMMdd; or an ISO instant (2026-10-27T18:15Z) to test everything after it
      --yesterday          test yesterday's service date in the agency time zone (for the nightly run)
      --train-days <n>     days of history before the test window to learn from (default 14)
      --store <path>       cache of replayed predictions and score history (default: next to --db, backtest.db)
      --routes-csv <path>  also write every score broken down by route
      --learning-curve     also score v1 with history tables from 25% and 50% of the training trips
      --checkpoint-report  just print how many road segments each checkpoint layout shares between routes
    """;

var opts = ParseOptions(args);
if (opts is null || !opts.ContainsKey("data") || !opts.ContainsKey("db") || !(opts.ContainsKey("test") ^ opts.ContainsKey("yesterday") || opts.ContainsKey("checkpoint-report")))
{
    Console.Error.WriteLine(Usage);
    return 2;
}
var dataDir = opts["data"]!;
var dbPath = opts["db"]!;
var storePath = opts.GetValueOrDefault("store") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "backtest.db");
var trainDays = int.Parse(opts.GetValueOrDefault("train-days") ?? "14", CultureInfo.InvariantCulture);
var clock = Stopwatch.StartNew();
var now = DateTimeOffset.UtcNow;

var feeds = new FeedSet(Path.Combine(dataDir, "static", "metro-gtfs"), Path.Combine(dataDir, "static", "metro-gtfs-next"));
feeds.Refresh();
var zone = feeds.Feeds[0].TimeZone;
var routeTypes = feeds.Feeds.SelectMany(f => f.RouteTypes).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
string Mode(BacktestTarget t) => routeTypes.GetValueOrDefault(t.RouteId, 3) is 0 or 1 or 2 ? "rail" : "bus";

// --checkpoint-report: how many road segments each checkpoint layout lets routes share, then exit.
if (opts.ContainsKey("checkpoint-report"))
{
    var feed = feeds.Feeds[0];
    var patterns = feed.Trips.Values.GroupBy(t => t.Pattern).Select(g => (Pattern: g.Key, Routes: g.Select(t => t.RouteId).ToHashSet())).ToList();
    foreach (var (layout, build) in new (string, Func<Pattern, Transit.Crossings.Checkpoint[]>)[]
             {
                 ("spaced", p => Transit.Crossings.CheckpointBuilder.BuildSpaced(p.Stops)),
                 ("grid", p => Transit.Crossings.CheckpointBuilder.BuildGrid(p.Stops, feed.Shapes[p.ShapeId])),
             })
    {
        var routesBySegment = new Dictionary<string, HashSet<string>>();
        var checkpoints = 0L;
        var metres = new Dictionary<string, double>();
        foreach (var (pattern, routes) in patterns)
        {
            var cps = build(pattern);
            checkpoints += cps.Length;
            for (var i = 1; i < cps.Length; i++)
            {
                var key = cps[i - 1].Id + "|" + cps[i].Id;
                (routesBySegment.TryGetValue(key, out var set) ? set : routesBySegment[key] = []).UnionWith(routes);
                metres[key] = cps[i].Entry - cps[i - 1].Exit;
            }
        }
        var shared = routesBySegment.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
        // Weighted by route-metres: how much of the network's driving happens on a segment another route also drives.
        var routeMetres = routesBySegment.Sum(kv => kv.Value.Count * metres[kv.Key]);
        var sharedRouteMetres = shared.Sum(k => routesBySegment[k].Count * metres[k]);
        Console.WriteLine($"{layout,-7} {patterns.Count:N0} patterns, {checkpoints / (double)patterns.Count:F0} checkpoints each, {routesBySegment.Count:N0} segments; " +
            $"{100.0 * shared.Count / routesBySegment.Count:F0}% shared by 2+ routes, {100 * sharedRouteMetres / routeMetres:F0}% of route-metres on shared segments");
    }
    return 0;
}

// Test window: whole service dates, or everything after an instant (useful while history is short).
string testFrom, testTo;
DateTimeOffset? splitAt = null;
if (opts.ContainsKey("yesterday"))
    testFrom = testTo = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(-1).ToString("yyyyMMdd");
else if (opts["test"]!.Contains('T'))
{
    splitAt = DateTimeOffset.Parse(opts["test"]!, CultureInfo.InvariantCulture);
    testFrom = TimeZoneInfo.ConvertTime(splitAt.Value, zone).Date.AddDays(-1).ToString("yyyyMMdd"); // early-morning trips belong to the previous service date
    testTo = TimeZoneInfo.ConvertTime(now, zone).Date.ToString("yyyyMMdd");
}
else
{
    var parts = opts["test"]!.Split("..");
    (testFrom, testTo) = (parts[0], parts.Length > 1 ? parts[1] : parts[0]);
}
var trainFrom = DateTime.ParseExact(testFrom, "yyyyMMdd", CultureInfo.InvariantCulture).AddDays(-trainDays).ToString("yyyyMMdd");

var targets = BacktestTargets.Load(dbPath, trainFrom, testTo);
bool IsTest(BacktestTarget t) => splitAt is { } at ? t.Actual >= at : string.CompareOrdinal(t.ServiceDate, testFrom) >= 0;
var test = Enumerable.Range(0, targets.Count).Where(i => IsTest(targets[i])).ToList();
var train = Enumerable.Range(0, targets.Count).Where(i => !IsTest(targets[i])).ToList();
var testLabel = splitAt is { } s ? $"after {s:yyyy-MM-dd HH:mm}Z" : testFrom == testTo ? testFrom : $"{testFrom}-{testTo}";
Console.WriteLine($"test {testLabel}: {test.Count:N0} departures | train {trainFrom} onward: {train.Count:N0} departures ({clock.Elapsed.TotalSeconds:F0}s)");
if (test.Count == 0) return 1;

// Metro's predictions: replayed from the archive per service date, cached once a day is complete.
using var store = new BacktestStore(storePath);
var tripUpdates = new TripUpdatesPredictor(Path.Combine(dataDir, "raw", "metro-tripupdates"));
var metro = new PredictionTable(targets.Count);
foreach (var day in Enumerable.Range(0, targets.Count).GroupBy(i => targets[i].ServiceDate).OrderBy(g => g.Key, StringComparer.Ordinal))
{
    var indices = day.ToList();
    if (store.TryLoad(tripUpdates.Name, day.Key, targets, indices, metro))
    {
        Console.WriteLine($"  {day.Key}: cached predictions");
        continue;
    }
    var dayTargets = indices.Select(i => targets[i]).ToList();
    var dayTable = new PredictionTable(dayTargets.Count);
    tripUpdates.Fill(dayTargets, dayTable);
    for (var k = 0; k < indices.Count; k++)
        for (var h = 0; h < Horizons.Minutes.Length; h++)
            metro[indices[k], h] = dayTable[k, h];
    // A day is complete once its last departure is well past; partial days are recomputed next time.
    var complete = now - dayTargets.Max(t => t.Actual) > TimeSpan.FromHours(6);
    if (complete) store.Save(tripUpdates.Name, day.Key, targets, indices, metro);
    Console.WriteLine($"  {day.Key}: replayed {dayTargets.Count:N0} departures{(complete ? ", cached" : "")} ({clock.Elapsed.TotalSeconds:F0}s)");
}

var schedule = new PredictionTable(targets.Count);
new SchedulePredictor(id => feeds.Resolve(id)?.Trip, zone).Fill(targets, schedule);

var tables = new List<(string Name, PredictionTable Table)> { ("schedule", schedule), (tripUpdates.Name, metro) };
// Learned corrections for any base predictor, fit on its errors in the training window. "+cap" shifts toward the
// typical miss but never so far that more than 5% of training buses would leave early; metro+median is uncapped.
void AddCorrected(string baseName, PredictionTable baseTable)
{
    var samples = LeadTimeCorrection.Samples(targets, baseTable, train).ToList();
    if (samples.Count == 0)
    {
        Console.WriteLine($"  {baseName}+cap skipped: no training predictions");
        return;
    }
    tables.Add(($"{baseName}+cap", LeadTimeCorrection.Fit(samples, maxEarlyRate: 0.05).Apply(targets, baseTable)));
    if (baseName == tripUpdates.Name)
        tables.Add(("metro+median", LeadTimeCorrection.Fit(samples).Apply(targets, baseTable)));
}
AddCorrected(tripUpdates.Name, metro);

// v0/v1: medians learned only from crossings that ended before the test window; live state from anything knowable at query time.
var history = CrossingHistory.Load(dbPath, trainFrom, testTo);
var calendar = new ServiceCalendar(zone);
bool InTraining(string serviceDate, double endUnix) =>
    splitAt is { } cut ? endUnix < cut.ToUnixTimeSeconds() : string.CompareOrdinal(serviceDate, testFrom) < 0;
var stats = SegmentStats.Build(history, calendar, InTraining);
Console.WriteLine($"  v0 tables: {stats.TravelTable.Keys:N0} segments, {stats.DwellTable.Keys:N0} stops from {history.Trips:N0} trips ({clock.Elapsed.TotalSeconds:F0}s)");
if (stats.TravelTable.Keys > 0)
{
    var pace = RecentPace.Build(history, stats);
    TripInfo? Resolve(string id) => feeds.Resolve(id)?.Trip;
    foreach (var model in new[] { new SegmentEtaPredictor("v0-medians", stats, null, history, Resolve, zone),
                 new SegmentEtaPredictor("v1-pace", stats, pace, history, Resolve, zone) { UseBlocks = true } })
    {
        var table = new PredictionTable(targets.Count);
        model.Fill(targets, table);
        tables.Add((model.Name, table));
        AddCorrected(model.Name, table);
        Console.WriteLine($"  scored {model.Name} ({clock.Elapsed.TotalSeconds:F0}s)");
    }

    // Learning curve: the same v1 with its history tables built from a stable random share of the training trips.
    // Today's pace still sees every bus, as it would live; only how much history the medians rest on changes.
    if (opts.ContainsKey("learning-curve"))
        foreach (var share in new[] { 0.25, 0.5 })
        {
            var sampled = history.Where((date, trip) => !InTraining(date, double.MaxValue) || Bucket(trip) < share);
            var sampledStats = SegmentStats.Build(sampled, calendar, InTraining);
            var model = new SegmentEtaPredictor($"v1@{share:P0}".Replace(" ", ""), sampledStats, RecentPace.Build(history, sampledStats), history, Resolve, zone) { UseBlocks = true };
            var table = new PredictionTable(targets.Count);
            model.Fill(targets, table);
            tables.Add((model.Name, table));
            Console.WriteLine($"  scored {model.Name}: {sampledStats.TravelTable.Keys:N0} segments from {sampled.Trips:N0} trips ({clock.Elapsed.TotalSeconds:F0}s)");
        }
}

/// <summary>A trip's place in [0, 1), the same on every run, so a 25% sample sits inside the 50% one.</summary>
static double Bucket(string tripId)
{
    var h = 2166136261u;
    foreach (var ch in tripId) h = unchecked((h ^ ch) * 16777619);
    return (h % 10_000) / 10_000.0;
}

var testTargets = test.Select(i => targets[i]).ToList();
var scores = new List<Score>();
foreach (var (name, table) in tables)
{
    var sub = new PredictionTable(test.Count);
    for (var k = 0; k < test.Count; k++)
        for (var h = 0; h < Horizons.Minutes.Length; h++)
            sub[k, h] = table[test[k], h];
    scores.AddRange(Scorer.Compute(name, testTargets, sub, _ => "all"));
    scores.AddRange(Scorer.Compute(name, testTargets, sub, Mode));
    scores.AddRange(Scorer.Compute(name, testTargets, sub, t => "route " + t.RouteId));
}

// Where the error comes from: buses already on the trip, or trips whose bus hadn't started it when asked.
if (tables.FindIndex(t => t.Name == "v1-pace") is var oi and >= 0)
{
    var oursTable = tables[oi].Table;
    Console.WriteLine("\nBUS by trip state   (median |err| s: metro / v1-pace)");
    for (var h = 0; h < Horizons.Minutes.Length; h++)
    {
        var groups = test.Where(i => Mode(targets[i]) == "bus").GroupBy(i =>
        {
            var at = targets[i].Actual.ToUnixTimeSeconds() - Horizons.Minutes[h] * 60;
            return history.Trip(targets[i].ServiceDate, targets[i].TripId).Any(r => r.DepartKnownAt <= at) ? "on trip" : "not started";
        });
        foreach (var g in groups.OrderBy(g => g.Key))
        {
            double Med(PredictionTable table) => Median(g.Select(i => Math.Abs(table[i, h] - targets[i].Actual.ToUnixTimeSeconds())).Where(double.IsFinite));
            Console.WriteLine($"  {Horizons.Minutes[h],2} min  {g.Key,-12} {g.Count(),7:N0} ({100.0 * g.Count() / test.Count,3:F0}%)   {Med(metro),5:F0} / {Med(oursTable),5:F0}");
        }
    }
}
static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
}

foreach (var group in new[] { "all", "rail", "bus" })
{
    Console.WriteLine($"""

        {group.ToUpperInvariant()}  (errors are predicted minus actual departure; early = bus left >60 s before predicted)
        predictor          ahead   coverage   median|err|  p90|err|   bias   within1m  within2m   early
        """);
    foreach (var sc in scores.Where(sc => sc.Group == group).OrderBy(sc => sc.HorizonMinutes).ThenBy(sc => tables.FindIndex(t => t.Name == sc.Predictor)))
        Console.WriteLine(sc.Predicted == 0
            ? $"{sc.Predictor,-18} {sc.HorizonMinutes,3} min   {"-",8}"
            : $"{sc.Predictor,-18} {sc.HorizonMinutes,3} min   {sc.Coverage,8:P0}   {sc.MedianAbsError,8:F0} s  {sc.P90AbsError,6:F0} s  {sc.Bias,5:F0} s  {sc.Within1Min,8:P0}  {sc.Within2Min,8:P0}  {sc.EarlyRate,6:P1}");
}

store.SaveScores(now, splitAt?.ToString("O") ?? testFrom, testTo, scores);
Console.WriteLine($"\nscores saved to {storePath}");

if (opts.GetValueOrDefault("routes-csv") is { } csvPath)
{
    using var csv = new StreamWriter(csvPath);
    csv.WriteLine("predictor,group,horizon_min,targets,predicted,coverage,median_abs_s,p90_abs_s,bias_s,within_1m,within_2m,early_rate");
    foreach (var sc in scores)
        csv.WriteLine(string.Join(',', sc.Predictor, sc.Group, sc.HorizonMinutes, sc.Targets, sc.Predicted,
            F(sc.Coverage), F(sc.MedianAbsError), F(sc.P90AbsError), F(sc.Bias), F(sc.Within1Min), F(sc.Within2Min), F(sc.EarlyRate)));
    Console.WriteLine($"wrote {csvPath}");
}
return 0;

static string F(double v) => double.IsNaN(v) ? "" : v.ToString("G4", CultureInfo.InvariantCulture);

static Dictionary<string, string?>? ParseOptions(string[] args)
{
    var opts = new Dictionary<string, string?>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) return null;
        var name = args[i][2..];
        opts[name] = name is "yesterday" or "learning-curve" or "checkpoint-report" ? null : i + 1 < args.Length ? args[++i] : null;
    }
    return opts;
}
