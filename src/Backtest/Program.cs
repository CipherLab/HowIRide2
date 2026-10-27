using System.Diagnostics;
using System.Globalization;
using Transit.Backtest;
using Transit.Gtfs;

// Scores each predictor against observed departures, by how far ahead it was predicting.
if (args.Length is < 4 or > 5)
{
    Console.Error.WriteLine("""
        Usage: backtest <data-dir> <crossings.db> <from-service-date> <to-service-date> [per-route.csv]
          data-dir     ingest DataDir (raw/ and static/)
          dates        yyyyMMdd, inclusive
          per-route    optional CSV of every score broken down by route
        """);
    return 2;
}
var (dataDir, dbPath, from, to) = (args[0], args[1], args[2], args[3]);
var csvPath = args.Length == 5 ? args[4] : null;

var clock = Stopwatch.StartNew();
var feeds = new FeedSet(Path.Combine(dataDir, "static", "metro-gtfs"), Path.Combine(dataDir, "static", "metro-gtfs-next"));
feeds.Refresh();
var zone = feeds.Feeds[0].TimeZone;
var routeTypes = feeds.Feeds.SelectMany(f => f.RouteTypes).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
string Mode(BacktestTarget t) => routeTypes.GetValueOrDefault(t.RouteId, 3) is 0 or 1 or 2 ? "rail" : "bus";

var targets = BacktestTargets.Load(dbPath, from, to);
Console.WriteLine($"{targets.Count:N0} observed departures, service dates {from}-{to} ({clock.Elapsed.TotalSeconds:F0}s)");
if (targets.Count == 0) return 1;

var tripUpdates = new TripUpdatesPredictor(Path.Combine(dataDir, "raw", "metro-tripupdates"));
IPredictor[] predictors = [new SchedulePredictor(id => feeds.Resolve(id)?.Trip, zone), tripUpdates];
var scores = new List<Score>();
foreach (var predictor in predictors)
{
    var table = new PredictionTable(targets.Count);
    predictor.Fill(targets, table);
    scores.AddRange(Scorer.Compute(predictor.Name, targets, table, _ => "all"));
    scores.AddRange(Scorer.Compute(predictor.Name, targets, table, Mode));
    if (csvPath is not null)
        scores.AddRange(Scorer.Compute(predictor.Name, targets, table, t => "route " + t.RouteId));
    Console.WriteLine($"  scored {predictor.Name} ({clock.Elapsed.TotalSeconds:F0}s)");
}
Console.WriteLine($"  {tripUpdates.Snapshots:N0} TripUpdates snapshots read");

foreach (var group in new[] { "all", "rail", "bus" })
{
    Console.WriteLine($"""

        {group.ToUpperInvariant()}  (errors are predicted minus actual departure; early = bus left >60 s before predicted)
        predictor          ahead   coverage   median|err|  p90|err|   bias   within1m  within2m   early
        """);
    foreach (var s in scores.Where(s => s.Group == group).OrderBy(s => s.HorizonMinutes).ThenBy(s => s.Predictor))
        Console.WriteLine(s.Predicted == 0
            ? $"{s.Predictor,-18} {s.HorizonMinutes,3} min   {"-",8}"
            : $"{s.Predictor,-18} {s.HorizonMinutes,3} min   {s.Coverage,8:P0}   {s.MedianAbsError,8:F0} s  {s.P90AbsError,6:F0} s  {s.Bias,5:F0} s  {s.Within1Min,8:P0}  {s.Within2Min,8:P0}  {s.EarlyRate,6:P1}");
}

if (csvPath is not null)
{
    using var csv = new StreamWriter(csvPath);
    csv.WriteLine("predictor,group,horizon_min,targets,predicted,coverage,median_abs_s,p90_abs_s,bias_s,within_1m,within_2m,early_rate");
    foreach (var s in scores)
        csv.WriteLine(string.Join(',', s.Predictor, s.Group, s.HorizonMinutes, s.Targets, s.Predicted,
            F(s.Coverage), F(s.MedianAbsError), F(s.P90AbsError), F(s.Bias), F(s.Within1Min), F(s.Within2Min), F(s.EarlyRate)));
    Console.WriteLine($"\nwrote {csvPath}");
}
return 0;

static string F(double v) => double.IsNaN(v) ? "" : v.ToString("G4", CultureInfo.InvariantCulture);
