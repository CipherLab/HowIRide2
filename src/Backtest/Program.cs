using System.Diagnostics;
using System.Globalization;
using Transit.Backtest;
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
    """;

var opts = ParseOptions(args);
if (opts is null || !opts.ContainsKey("data") || !opts.ContainsKey("db") || !(opts.ContainsKey("test") ^ opts.ContainsKey("yesterday")))
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
var samples = MetroBiasCorrection.Samples(targets, metro, train).ToList();
if (samples.Count > 0)
{
    // metro+bias is the candidate: shifted toward the typical miss, but never so far that more than 5% of buses leave early.
    // metro+median is the unconstrained shift, kept to show what the cap costs and buys.
    var safe = MetroBiasCorrection.Fit(samples, maxEarlyRate: 0.05);
    tables.Add(("metro+bias", safe.Apply(targets, metro)));
    tables.Add(("metro+median", MetroBiasCorrection.Fit(samples).Apply(targets, metro)));
    Console.WriteLine($"  corrections learned from {samples.Count:N0} training predictions; {safe.RoutesWithOwnCorrection} routes have their own");
}
else
    Console.WriteLine("  metro+bias skipped: no training data before the test window");

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
        opts[name] = name == "yesterday" ? null : i + 1 < args.Length ? args[++i] : null;
    }
    return opts;
}
