using System.Globalization;
using Transit.Archive;
using Transit.Crossings;
using Transit.Gtfs;
using Transit.Realtime;
using TransitRealtime;

// Replays recorded VehiclePositions through crossing detection and writes one CSV row per checkpoint crossing.
if (args.Length != 4)
{
    Console.Error.WriteLine("""
        Usage: replay <data-dir> <from-hour> <to-hour> <out.csv>
          data-dir   ingest DataDir (contains raw/ and static/)
          from/to    UTC hours, inclusive, e.g. 2026-10-27T18
        """);
    return 2;
}

var dataDir = args[0];
var from = ParseHour(args[1]);
var to = ParseHour(args[2]);
var outPath = args[3];

var staticZip = PickStaticVersion(Path.Combine(dataDir, "static", "metro-gtfs"), from);
Console.WriteLine($"static GTFS: {staticZip}");
var feed = StaticFeed.Load(staticZip);
Console.WriteLine($"  {feed.Trips.Count:N0} trips, {feed.PatternCount:N0} patterns");

var detector = new CrossingDetector(feed);
// REPLAY_TRACE=10,902 writes every ping on those routes to <out>.trace.csv for debugging the matcher.
using var trace = Environment.GetEnvironmentVariable("REPLAY_TRACE") is { Length: > 0 } traceRoutes ? new StreamWriter(outPath + ".trace.csv") : null;
if (trace is not null)
{
    var routes = Environment.GetEnvironmentVariable("REPLAY_TRACE")!.Split(',').ToHashSet();
    trace.WriteLine("vehicle,trip,unix,last_dist,win_min,win_max,match_dist,match_off,nearest_dist,nearest_off");
    detector.TraceRoute = routes.Contains;
    detector.Trace = t => trace.WriteLine(string.Join(',', t.Tracker.VehicleId, t.Tracker.TripId, t.Time.ToUnixTimeSeconds(),
        F(t.LastDist), F(t.WindowMin), F(t.WindowMax), F(t.Windowed?.Distance), F(t.Windowed?.Offset), F(t.Nearest?.Distance), F(t.Nearest?.Offset)));
}
static string F(double? v) => v is { } d && double.IsFinite(d) ? d.ToString("F0", CultureInfo.InvariantCulture) : "";
var sanity = new SegmentSanity();
using var csv = new StreamWriter(outPath);
csv.WriteLine("service_date,trip_id,route_id,direction_id,vehicle_id,checkpoint_index,checkpoint_id,kind,stop_sequence,dist_m,arrive_unix,depart_unix,arrive_gap_s,depart_gap_s,detour");

var snapshots = 0;
for (var hour = from; hour <= to; hour = hour.AddHours(1))
{
    var path = FindHourFile(Path.Combine(dataDir, "raw", "metro-vehicles"), hour);
    if (path is null)
    {
        Console.WriteLine($"{hour:yyyy-MM-ddTHH}: no file");
        continue;
    }
    var before = detector.Stats.Crossings;
    DateTimeOffset lastFetch = default;
    foreach (var record in RawArchiveReader.Read(path))
    {
        snapshots++;
        lastFetch = record.FetchedAt;
        foreach (var ping in VehiclePings.From(FeedMessage.Parser.ParseFrom(record.Payload)))
        {
            foreach (var c in detector.OnPing(ping, record.FetchedAt))
            {
                WriteRow(csv, c);
                sanity.Add(c);
            }
        }
    }
    detector.PruneIdle(lastFetch);
    Console.WriteLine($"{hour:yyyy-MM-ddTHH}: {detector.Stats.Crossings - before:N0} crossings, {detector.ActiveTrackers} active trips");
}

var s = detector.Stats;
Console.WriteLine($"""

    {snapshots:N0} snapshots, {s.Pings:N0} pings
      used {s.Used:N0}, duplicate {s.Duplicate:N0}, stale {s.Stale:N0}, no trip {s.NoTrip:N0}, unknown trip {s.UnknownTrip:N0}
      off-route {s.OffRoute:N0}, ambiguous {s.Ambiguous:N0}, lost {s.Lost:N0}
      trips started {s.TripsStarted:N0}, crossings {s.Crossings:N0}
    """);
Console.WriteLine($"  off-route before trip start {s.PreStartOffRoute:N0} of {s.OffRoute:N0}; worst routes (used / off-route / pre-start):");
foreach (var (route, r) in s.ByRoute.OrderByDescending(kv => kv.Value.OffRoute - kv.Value.PreStart).Take(8))
    Console.WriteLine($"    {route,-6} {r.Used,7:N0} {r.OffRoute,6:N0} {r.PreStart,6:N0}  ({100.0 * (r.OffRoute - r.PreStart) / r.Used:F1}% mid-trip)");
sanity.Print();
Console.WriteLine($"wrote {outPath}");
return 0;

static DateTime ParseHour(string s) =>
    DateTime.ParseExact(s, "yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

static string? FindHourFile(string feedDir, DateTime hour) =>
    new[] { RawArchiveWriter.ClosedExtension, RawArchiveWriter.OpenExtension }
        .Select(ext => Path.Combine(feedDir, hour.ToString("yyyy-MM-dd"), hour.ToString("HH") + ext))
        .FirstOrDefault(File.Exists);

// Newest version published at or before the replay start; files are named by Last-Modified (yyyyMMddTHHmmssZ).
static string PickStaticVersion(string dir, DateTime at)
{
    var versions = Directory.GetFiles(dir, "*.zip").Order(StringComparer.Ordinal).ToList();
    if (versions.Count == 0) throw new FileNotFoundException($"no static GTFS in {dir}");
    var cutoff = at.ToString("yyyyMMdd'T'HHmmss'Z'") + ".zip";
    return versions.LastOrDefault(v => string.CompareOrdinal(Path.GetFileName(v), cutoff) <= 0) ?? versions[0];
}

static void WriteRow(TextWriter w, StopCrossing c)
{
    var cp = c.Checkpoint;
    w.WriteLine(string.Join(',',
        c.ServiceDate, c.TripId, c.RouteId, c.DirectionId.ToString(CultureInfo.InvariantCulture), c.VehicleId,
        cp.Index.ToString(CultureInfo.InvariantCulture), cp.Id, cp.Kind == CheckpointKind.Stop ? "stop" : "synthetic",
        cp.StopSequence.ToString(CultureInfo.InvariantCulture), cp.Dist.ToString("F1", CultureInfo.InvariantCulture),
        Unix(c.Arrive), Unix(c.Depart), Num(c.ArriveGapSeconds), Num(c.DepartGapSeconds), c.Detour ? "1" : "0"));

    static string Unix(DateTimeOffset? t) => t is { } v ? (v.ToUnixTimeMilliseconds() / 1000.0).ToString("F1", CultureInfo.InvariantCulture) : "";
    static string Num(double? v) => v?.ToString("F0", CultureInfo.InvariantCulture) ?? "";
}

/// <summary>Implied speeds between consecutive crossings on the same trip; nonsense here means a matching bug.</summary>
sealed class SegmentSanity
{
    readonly Dictionary<(string Trip, string Date), StopCrossing> _last = [];
    readonly List<double> _speeds = [], _dwells = [], _gaps = [];
    int _detours, _rows, _nonPositive;

    public void Add(StopCrossing c)
    {
        _rows++;
        if (c.Detour) _detours++;
        if (c.ArriveGapSeconds is { } g) _gaps.Add(g);
        if (c.Checkpoint.Kind == CheckpointKind.Stop && c.Arrive is { } a && c.Depart is { } d) _dwells.Add((d - a).TotalSeconds);

        var key = (c.TripId, c.ServiceDate);
        if (_last.TryGetValue(key, out var prev) && prev.Checkpoint.Index == c.Checkpoint.Index - 1
            && prev.Depart is { } leave && c.Arrive is { } reach)
        {
            // Stops under 60 m apart have touching zones, so their travel segment is legitimately 0 m and 0 s.
            var seconds = (reach - leave).TotalSeconds;
            var meters = c.Checkpoint.Entry - prev.Checkpoint.Exit;
            if (meters > 0 && seconds <= 0) _nonPositive++;
            else if (meters > 0) _speeds.Add(meters / seconds);
        }
        _last[key] = c;
    }

    public void Print()
    {
        Console.WriteLine($"rows {_rows:N0}, detour-flagged {Pct(_detours, _rows)}");
        Console.WriteLine($"segment speed m/s   {Quantiles(_speeds)}   (>30 m/s: {Pct(_speeds.Count(v => v > 30), _speeds.Count)}, zero/negative time: {_nonPositive:N0})");
        Console.WriteLine($"stop dwell s        {Quantiles(_dwells)}");
        Console.WriteLine($"ping gap s          {Quantiles(_gaps)}");
    }

    static string Pct(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:F1}%";

    static string Quantiles(List<double> values)
    {
        if (values.Count == 0) return "-";
        values.Sort();
        double Q(double p) => values[(int)Math.Min(values.Count - 1, p * values.Count)];
        return $"p5 {Q(.05):F1}  p25 {Q(.25):F1}  median {Q(.5):F1}  p75 {Q(.75):F1}  p95 {Q(.95):F1}  max {values[^1]:F1}  (n={values.Count:N0})";
    }
}
