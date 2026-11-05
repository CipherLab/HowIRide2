using System.Globalization;
using Transit.Archive;
using Transit.Backtest;
using Transit.Crossings;
using Transit.Eta;
using Transit.Gtfs;
using TransitRealtime;
using StopTimeRelationship = TransitRealtime.TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship;
using TripRelationship = TransitRealtime.TripDescriptor.Types.ScheduleRelationship;

// One service day's predictions and what was knowable when they were made, for training a model on v1's errors.
//
// Predictions are sampled on a fixed clock (every GridSeconds), the way a rider would see them, NOT at set
// times before the actual departure as the backtest does. A model must not train on those: the true lead would take
// only six values, so it could learn to round the lead it's shown and look far better than any real rider would see.
//
// v0 and v1 use the same walk-forward history as the backtest: only service dates before this one.
if (args.Length != 4)
{
    Console.Error.WriteLine("usage: dump <data-dir> <crossings.db> <yyyyMMdd> <out.csv>");
    return 2;
}
var (dataDir, dbPath, date, outPath) = (args[0], args[1], args[2], args[3]);

const int GridSeconds = 300;
const double MaxLeadSeconds = 35 * 60;
// Well past the longest lead, so a bus far later than predicted is kept and not quietly dropped.
const double MaxLookBackSeconds = 90 * 60;
const double MaxSnapshotAge = 60;
const int TrainDays = 14;

var feeds = new FeedSet(Path.Combine(dataDir, "static", "metro-gtfs"), Path.Combine(dataDir, "static", "metro-gtfs-next"));
feeds.Refresh();
var zone = feeds.Feeds[0].TimeZone;
TripInfo? Resolve(string id) => feeds.Resolve(id)?.Trip;
var routeTypes = feeds.Feeds.SelectMany(f => f.RouteTypes).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);

var trainFrom = DateTime.ParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture).AddDays(-TrainDays).ToString("yyyyMMdd");
var history = CrossingHistory.Load(dbPath, trainFrom, date);
var calendar = new ServiceCalendar(zone);
bool InTraining(string serviceDate, double endUnix) => string.CompareOrdinal(serviceDate, date) < 0;
var stats = SegmentStats.Build(history, calendar, InTraining, scheduledStart: SegmentStats.ScheduledStarts(Resolve, zone));
var pace = RecentPace.Build(history, stats);
var v0 = new SegmentEtaPredictor("v0", stats, null, history, Resolve, zone);
var v1 = new SegmentEtaPredictor("v1", stats, pace, history, Resolve, zone) { UseBlocks = true };

var targets = BacktestTargets.Load(dbPath, date, date);
var origin = StaticFeed.ServiceDayOrigin(date, zone).ToUnixTimeSeconds();
var dayType = ServiceCalendar.DayTypeOf(date);
var localOffset = zone.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(origin + 12 * 3600)).TotalSeconds;
Console.WriteLine($"{date}: {targets.Count:N0} departures, {targets.Count(t => Resolve(t.TripId) is not null):N0} on trips in the current feed, {stats.TravelTable.Keys:N0} segments of history");
if (targets.Count == 0) return 1;

double? ScheduledAt(TripInfo trip, int stopSequence)
{
    var pos = Array.FindIndex(trip.Pattern.Stops, s => s.Sequence == stopSequence);
    return pos >= 0 && pos < trip.ScheduledDepartures.Length && trip.ScheduledDepartures[pos] >= 0 ? origin + trip.ScheduledDepartures[pos] : null;
}

// A trip whose pattern changed in a new timetable can have crossings recorded against the old checkpoints.
static bool Aligned(TripInfo trip, CrossingRow r) => r.Index < trip.Pattern.Checkpoints.Length && trip.Pattern.Checkpoints[r.Index].Id == r.CheckpointId;
var misaligned = 0L;
var dropped = 0L;

// Everything observed today by stop and by route, ordered by when an app could first have known it.
var byStop = new Dictionary<(string Route, int Direction, string Stop), List<Seen>>();
var byRoute = new Dictionary<(string Route, int Direction), List<Seen>>();
foreach (var tripId in targets.Select(t => t.TripId).Distinct())
{
    if (Resolve(tripId) is not { } trip) continue;
    foreach (var r in history.Trip(date, tripId))
    {
        if (!r.IsStop || r.Detour || r.Depart is not { } depart || r.DepartKnownAt is not { } known) continue;
        if (!Aligned(trip, r))
        {
            misaligned++;
            continue;
        }
        var scheduled = ScheduledAt(trip, trip.Pattern.Checkpoints[r.Index].StopSequence);
        var seen = new Seen(known, depart, scheduled ?? double.NaN, tripId);
        var stopKey = (trip.RouteId, trip.DirectionId, r.CheckpointId);
        (byStop.TryGetValue(stopKey, out var s) ? s : byStop[stopKey] = []).Add(seen);
        if (scheduled is not null)
            (byRoute.TryGetValue((trip.RouteId, trip.DirectionId), out var rt) ? rt : byRoute[(trip.RouteId, trip.DirectionId)] = []).Add(seen);
    }
}
foreach (var list in byStop.Values) list.Sort((a, b) => a.Known.CompareTo(b.Known));
foreach (var list in byRoute.Values) list.Sort((a, b) => a.Known.CompareTo(b.Known));

static int LastKnownBy(List<Seen> list, double q)
{
    var (lo, hi) = (0, list.Count);
    while (lo < hi)
    {
        var mid = (lo + hi) / 2;
        if (list[mid].Known <= q) lo = mid + 1;
        else hi = mid;
    }
    return lo - 1;
}

// Metro's answer at each grid time: the latest snapshot then, if it's fresh, as a rider would have seen.
var firstActual = targets.Min(t => t.Actual).ToUnixTimeSeconds();
var lastActual = targets.Max(t => t.Actual).ToUnixTimeSeconds();
var gridFrom = (firstActual - (long)MaxLookBackSeconds) / GridSeconds * GridSeconds;
var metro = new Dictionary<long, Dictionary<(string, int), double>>();
{
    var next = gridFrom;
    RawRecord? previous = null;
    void Take(long q)
    {
        if (previous is not { } p || q - p.FetchedAt.ToUnixTimeSeconds() > MaxSnapshotAge) return;
        var rows = new Dictionary<(string, int), double>();
        foreach (var entity in FeedMessage.Parser.ParseFrom(p.Payload).Entity)
        {
            if (entity.TripUpdate is not { Trip: { } trip } update || trip.ScheduleRelationship == TripRelationship.Canceled || trip.StartDate != date) continue;
            foreach (var stu in update.StopTimeUpdate)
            {
                if (!stu.HasStopSequence || stu.ScheduleRelationship != StopTimeRelationship.Scheduled) continue;
                var predicted = stu.Departure is { HasTime: true } d ? d.Time : stu.Arrival is { HasTime: true } a ? a.Time : (long?)null;
                if (predicted is not null) rows[(trip.TripId, (int)stu.StopSequence)] = predicted.Value;
            }
        }
        metro[q] = rows;
    }
    foreach (var record in ArchiveFollower.ReadRange(Path.Combine(dataDir, "raw", "metro-tripupdates"), DateTimeOffset.FromUnixTimeSeconds(gridFrom - 120), DateTimeOffset.FromUnixTimeSeconds(lastActual + 60)))
    {
        var at = record.FetchedAt.ToUnixTimeSeconds();
        while (next < at)
        {
            Take(next);
            next += GridSeconds;
        }
        previous = record;
    }
    Take(next);
}
Console.WriteLine($"{date}: Metro at {metro.Count} of {(lastActual - gridFrom) / GridSeconds} grid times");

static string F(double? v) => v is { } x && !double.IsNaN(x) ? x.ToString("0.#", CultureInfo.InvariantCulture) : "";

using var w = new StreamWriter(outPath);
w.WriteLine("date,trip,route,route_type,direction,stop,seq,q,actual,v1,v0,metro,sched,day_type,local_second,lead_v1,stop_frac,started,stops_left,dist_left,since_last,delay_now,pace,prev_gap,prev_delay,prev_sched_gap,route_delay_15m,route_seen_15m,target_is_rail");
var written = 0L;
foreach (var t in targets)
{
    if (Resolve(t.TripId) is not { } trip) continue;
    var checkpoints = trip.Pattern.Checkpoints;
    var target = Array.FindIndex(checkpoints, c => c.Kind == CheckpointKind.Stop && c.StopSequence == t.StopSequence);
    if (target <= 0) continue;
    var rows = history.Trip(date, t.TripId);
    if (!rows.All(r => Aligned(trip, r)))
    {
        dropped++;
        continue;
    }
    var actual = t.Actual.ToUnixTimeMilliseconds() / 1000.0;
    var scheduled = ScheduledAt(trip, t.StopSequence);
    byStop.TryGetValue((trip.RouteId, trip.DirectionId, t.StopId), out var atStop);
    byRoute.TryGetValue((trip.RouteId, trip.DirectionId), out var onRoute);

    for (var q = (long)Math.Ceiling((actual - MaxLookBackSeconds) / GridSeconds) * GridSeconds; q < actual; q += GridSeconds)
    {
        if (v1.Predict(trip, target, rows, dayType, origin, q, id => history.Trip(date, id)) is not { } predicted) continue;
        var lead = predicted - q;
        if (lead > MaxLeadSeconds) continue;

        // Where the bus is, as far as anyone could know at q: the same rule the predictors use.
        CrossingRow? last = null;
        CrossingRow? lastStop = null;
        foreach (var r in rows)
        {
            if (r.Index >= target || r.Depart is null || r.DepartKnownAt > q || r.Detour) continue;
            last = r;
            if (r.IsStop) lastStop = r;
        }
        double? delayNow = null;
        if (lastStop is { Depart: { } lastDepart } && ScheduledAt(trip, checkpoints[lastStop.Index].StopSequence) is { } lastScheduled)
            delayNow = lastDepart - lastScheduled;
        var position = last?.Dist ?? checkpoints[0].Dist;
        var stopsLeft = 0;
        for (var s = (last?.Index ?? 0) + 1; s <= target; s++)
            if (checkpoints[s].Kind == CheckpointKind.Stop) stopsLeft++;

        // The bus ahead of this one at this stop, and how its route has been running lately.
        double? prevGap = null, prevDelay = null, prevSchedGap = null;
        if (atStop is not null)
            for (var i = LastKnownBy(atStop, q); i >= 0; i--)
            {
                if (atStop[i].Trip == t.TripId) continue;
                prevGap = q - atStop[i].Depart;
                if (!double.IsNaN(atStop[i].Scheduled))
                {
                    prevDelay = atStop[i].Depart - atStop[i].Scheduled;
                    if (scheduled is { } own) prevSchedGap = own - atStop[i].Scheduled;
                }
                break;
            }
        double? routeDelay = null;
        var routeSeen = 0;
        if (onRoute is not null)
        {
            var sum = 0.0;
            for (var i = LastKnownBy(onRoute, q); i >= 0 && onRoute[i].Known >= q - 900; i--)
            {
                if (onRoute[i].Trip == t.TripId) continue;
                sum += onRoute[i].Depart - onRoute[i].Scheduled;
                routeSeen++;
            }
            if (routeSeen > 0) routeDelay = sum / routeSeen;
        }

        double? metroAt = metro.TryGetValue(q, out var snapshot) && snapshot.TryGetValue((t.TripId, t.StopSequence), out var m) ? m : null;
        var older = v0.Predict(trip, target, rows, dayType, origin, q);
        var type = routeTypes.GetValueOrDefault(trip.RouteId, 3);
        w.Write(string.Join(',',
            date, t.TripId, trip.RouteId, type.ToString(CultureInfo.InvariantCulture), trip.DirectionId.ToString(CultureInfo.InvariantCulture), t.StopId,
            t.StopSequence.ToString(CultureInfo.InvariantCulture), q.ToString(CultureInfo.InvariantCulture), F(actual), F(predicted), F(older), F(metroAt), F(scheduled),
            ((int)dayType).ToString(CultureInfo.InvariantCulture), F(((q + localOffset) % 86400 + 86400) % 86400), F(lead),
            F((double)target / (checkpoints.Length - 1)), last is null ? "0" : "1", stopsLeft.ToString(CultureInfo.InvariantCulture), F(checkpoints[target].Dist - position),
            F(last?.Depart is { } d ? q - d : null), F(delayNow), F(pace.Factor(checkpoints, target, q, checkpoints[target].Dist - position)),
            F(prevGap), F(prevDelay), F(prevSchedGap), F(routeDelay), routeSeen.ToString(CultureInfo.InvariantCulture), type is 0 or 1 or 2 ? "1" : "0"));
        w.Write('\n');
        written++;
    }
}
Console.WriteLine($"{date}: wrote {written:N0} predictions; {dropped:N0} departures dropped and {misaligned:N0} crossings ignored for not matching the current feed");
return 0;

readonly record struct Seen(double Known, double Depart, double Scheduled, string Trip);
