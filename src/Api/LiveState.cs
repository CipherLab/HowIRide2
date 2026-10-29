using System.Text.Json;
using System.Threading.Channels;
using Transit.Archive;
using Transit.Crossings;
using Transit.Eta;
using Transit.Gtfs;
using Transit.Realtime;
using TransitRealtime;

namespace Api;

public sealed class ApiOptions
{
    public const string Section = "Api";

    /// <summary>Ingest's DataDir; only read from.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>The detect service's store; read-only here, for model history.</summary>
    public string DbPath { get; set; } = "derived/crossings.db";

    public string BacktestDbPath { get; set; } = "derived/backtest.db";

    /// <summary>Days of crossings history the live v0/v1 tables are learned from.</summary>
    public int HistoryDays { get; set; } = 14;
}

public sealed record VehicleView(
    string Id, string? TripId, string? ServiceDate, string? RouteId, string? Route, string? Color, string? TextColor, string? Headsign,
    string? ShapeId, double? Dist, double? DistAt, double Lat, double Lon, double PingAt, double? Pace);

/// <summary>A stop plus the routes that serve it: rail and rapid lines first, then buses in number order.</summary>
public sealed record StopView(string Id, string Name, double Lat, double Lon, RouteInfo[] Routes);

/// <param name="Dist">Metres along the trip's shape, on the same scale as a vehicle's dist.</param>
public sealed record StopTime(string StopId, string Name, int Sequence, double Dist, double? Scheduled, double? Metro, double? V0, double? V1);

public sealed record VehicleDetail(VehicleView Vehicle, StopTime[] NextStops);

public sealed record Departure(
    string TripId, string RouteId, string Route, string Color, string TextColor, string Headsign, string? VehicleId,
    double? Scheduled, double? Metro, double? V0, double? V1);

/// <summary>
/// Everything the live API serves, fed by the archive followers. One lock guards it all: updates arrive about
/// every 10 s and requests are cheap, so contention isn't worth more machinery.
/// </summary>
public sealed class LiveState(ApiOptions options, TimeProvider clock, ILogger<LiveState> log)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly TimeSpan VehicleExpiry = TimeSpan.FromMinutes(5);
    static readonly TimeSpan PaceRefresh = TimeSpan.FromMinutes(1);
    static readonly TimeSpan RecentTripExpiry = TimeSpan.FromHours(4);

    readonly object _gate = new();
    readonly FeedSet _feeds = new(
        Path.Combine(options.DataDir, "static", "metro-gtfs"), Path.Combine(options.DataDir, "static", "metro-gtfs-next"));
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<string, VehicleView> _vehicles = [];
    readonly Dictionary<(string Date, string Trip), List<CrossingRow>> _recent = [];
    readonly List<Channel<string>> _subscribers = [];
    CrossingDetector? _detector;
    Dictionary<(string Date, string Trip), Dictionary<int, long>> _metro = [];
    Dictionary<string, (Pattern Pattern, int Checkpoint)[]> _stopPatterns = [];
    Dictionary<string, RouteInfo[]> _stopRoutes = [];
    SegmentStats? _stats;
    RecentPace? _pace;
    DateTimeOffset _paceBuiltAt;
    DateTimeOffset? _vehiclesFetchedAt, _metroFetchedAt, _statsBuiltAt;
    DateTime? _detectorHour;

    public Task Ready => _ready.Task;
    public TimeZoneInfo Zone => _feeds.Feeds[0].TimeZone;

    public void LoadStatic()
    {
        _feeds.Refresh();
        lock (_gate)
        {
            _detector = new CrossingDetector(_feeds.Resolve);
            IndexStops();
        }
        log.LogInformation("Static GTFS: {Versions}", string.Join(", ", _feeds.Feeds.Select(f => f.Version)));
        _ready.TrySetResult();
    }

    void IndexStops()
    {
        _stopPatterns = _feeds.Feeds
            .SelectMany(f => f.Trips.Values.Select(t => t.Pattern)).Distinct()
            .SelectMany(p => p.Checkpoints.Where(c => c.Kind == CheckpointKind.Stop).Select(c => (c.Id, Entry: (p, c.Index))))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Entry).ToArray());

        var routeIdsByPattern = _feeds.Feeds.SelectMany(f => f.Trips.Values)
            .GroupBy(t => t.Pattern)
            .ToDictionary(g => g.Key, g => g.Select(t => t.RouteId).Distinct().ToArray());
        _stopRoutes = _stopPatterns.ToDictionary(kv => kv.Key, kv => kv.Value
            .SelectMany(e => routeIdsByPattern.GetValueOrDefault(e.Pattern) ?? [])
            .Distinct()
            .Select(RouteOf)
            .OfType<RouteInfo>()
            .OrderBy(r => IsRapid(r) ? 0 : 1)
            .ThenBy(r => int.TryParse(r.ShortName, out var n) ? n : int.MaxValue)
            .ThenBy(r => r.ShortName, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>Rail and the lettered/coloured rapid bus lines, which get their own colour on the map.</summary>
    static bool IsRapid(RouteInfo r) => r.Type != 3 || !int.TryParse(r.ShortName, out _);

    StopView ViewOf(StopInfo s) => new(s.Id, s.Name, s.Lat, s.Lon, _stopRoutes.GetValueOrDefault(s.Id) ?? []);

    // ---- updates from followers ----

    public void OnVehicleSnapshot(RawRecord record, bool live)
    {
        var message = FeedMessage.Parser.ParseFrom(record.Payload);
        lock (_gate)
        {
            var hour = ArchiveFollower.FloorHour(record.FetchedAt);
            if (hour != _detectorHour)
            {
                _detectorHour = hour;
                _detector!.PruneIdle(record.FetchedAt);
                if (_feeds.Refresh()) IndexStops();
            }

            foreach (var ping in VehiclePings.From(message))
            {
                foreach (var c in _detector!.OnPing(ping, record.FetchedAt))
                {
                    var key = (c.ServiceDate, c.TripId);
                    if (!_recent.TryGetValue(key, out var rows)) _recent[key] = rows = [];
                    rows.Add(CrossingRow.From(c));
                }
                _vehicles[ping.VehicleId] = View(ping, record.FetchedAt);
            }
            _vehiclesFetchedAt = record.FetchedAt;

            foreach (var stale in _vehicles.Where(kv => record.FetchedAt.ToUnixTimeSeconds() - kv.Value.PingAt > VehicleExpiry.TotalSeconds).Select(kv => kv.Key).ToList())
                _vehicles.Remove(stale);
            if (record.FetchedAt - _paceBuiltAt > PaceRefresh)
                RebuildPace(record.FetchedAt);
        }
        if (live) Publish();
    }

    public void OnTripUpdates(RawRecord record)
    {
        var predictions = new Dictionary<(string, string), Dictionary<int, long>>();
        foreach (var entity in FeedMessage.Parser.ParseFrom(record.Payload).Entity)
        {
            if (entity.TripUpdate is not { Trip: { } trip } update || trip.ScheduleRelationship == TripDescriptor.Types.ScheduleRelationship.Canceled)
                continue;
            var stops = new Dictionary<int, long>();
            foreach (var stu in update.StopTimeUpdate)
            {
                if (!stu.HasStopSequence || stu.ScheduleRelationship != TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship.Scheduled) continue;
                if (stu.Departure is { HasTime: true } d) stops[(int)stu.StopSequence] = d.Time;
                else if (stu.Arrival is { HasTime: true } a) stops[(int)stu.StopSequence] = a.Time;
            }
            predictions[(trip.StartDate, trip.TripId)] = stops;
        }
        lock (_gate)
        {
            _metro = predictions;
            _metroFetchedAt = record.FetchedAt;
        }
    }

    public void SetStats(SegmentStats stats)
    {
        lock (_gate)
        {
            _stats = stats;
            _statsBuiltAt = clock.GetUtcNow();
            RebuildPace(clock.GetUtcNow());
        }
    }

    void RebuildPace(DateTimeOffset at)
    {
        _paceBuiltAt = at;
        var cutoff = at.ToUnixTimeSeconds() - RecentTripExpiry.TotalSeconds;
        foreach (var key in _recent.Where(kv => (kv.Value[^1].Depart ?? kv.Value[^1].Arrive ?? 0) < cutoff).Select(kv => kv.Key).ToList())
            _recent.Remove(key);
        if (_stats is not null)
            _pace = RecentPace.Build(CrossingHistory.From(_recent.Values.SelectMany(r => r)), _stats);
    }

    VehicleView View(VehiclePing ping, DateTimeOffset fetchedAt)
    {
        var tracker = _detector!.Trackers.GetValueOrDefault(ping.VehicleId);
        var trip = tracker?.TripId == ping.TripId ? tracker!.Trip : ping.TripId is { } id ? _feeds.Resolve(id)?.Trip : null;
        var route = trip is not null ? RouteOf(trip.RouteId) : null;
        double? pace = null;
        if (tracker is { Distance: not null } && _pace is not null && tracker.NextCheckpoint < tracker.Trip.Pattern.Checkpoints.Length)
            pace = Math.Round(_pace.Factor(tracker.Trip.Pattern.Checkpoints, Math.Max(1, tracker.NextCheckpoint), fetchedAt.ToUnixTimeSeconds(), 0), 2);
        return new VehicleView(ping.VehicleId, trip?.TripId, ping.StartDate, trip?.RouteId, route?.ShortName, route?.Color, route?.TextColor, trip?.Headsign,
            trip?.Pattern.ShapeId, tracker?.Distance is { } d ? Math.Round(d, 1) : null,
            tracker?.Distance is not null ? tracker.DistanceAt.ToUnixTimeMilliseconds() / 1000.0 : null,
            ping.Lat, ping.Lon, ping.Timestamp.ToUnixTimeSeconds(), pace);
    }

    RouteInfo? RouteOf(string routeId) =>
        _feeds.Feeds.Select(f => f.Routes.GetValueOrDefault(routeId)).FirstOrDefault(r => r is not null);

    // ---- streaming ----

    public Channel<string> Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            _subscribers.Add(channel);
            channel.Writer.TryWrite(SnapshotJson());
        }
        return channel;
    }

    public void Unsubscribe(Channel<string> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
    }

    void Publish()
    {
        lock (_gate)
        {
            if (_subscribers.Count == 0) return;
            var json = SnapshotJson();
            foreach (var s in _subscribers) s.Writer.TryWrite(json);
        }
    }

    string SnapshotJson() => JsonSerializer.Serialize(new { serverTime = Now(), vehicles = _vehicles.Values }, Json);

    double Now() => clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    // ---- queries ----

    public object Snapshot()
    {
        lock (_gate) return new { serverTime = Now(), vehicles = _vehicles.Values.ToArray() };
    }

    public ShapeGeometry? Shape(string id) =>
        _feeds.Feeds.Select(f => f.Shapes.GetValueOrDefault(id)).FirstOrDefault(s => s is not null);

    public IEnumerable<object> Routes() =>
        _feeds.Feeds.SelectMany(f => f.Routes.Values).DistinctBy(r => r.Id).OrderBy(r => r.ShortName, StringComparer.Ordinal);

    public StopView[] StopsIn(double south, double west, double north, double east, int limit = 4000)
    {
        lock (_gate)
            return _stopPatterns.Keys
                .Select(StopInfoOf)
                .OfType<StopInfo>()
                .Where(s => s.Lat >= south && s.Lat <= north && s.Lon >= west && s.Lon <= east)
                .Take(limit)
                .Select(ViewOf)
                .ToArray();
    }

    public StopView? Stop(string id)
    {
        lock (_gate) return StopInfoOf(id) is { } s ? ViewOf(s) : null;
    }

    StopInfo? StopInfoOf(string id) =>
        _feeds.Feeds.Select(f => f.Stops.GetValueOrDefault(id)).FirstOrDefault(s => s is not null);

    public VehicleDetail? Vehicle(string id)
    {
        lock (_gate)
        {
            if (!_vehicles.TryGetValue(id, out var view)) return null;
            if (view.TripId is null || view.ServiceDate is null || _feeds.Resolve(view.TripId)?.Trip is not { } trip)
                return new VehicleDetail(view, []);
            var next = _detector!.Trackers.GetValueOrDefault(id) is { } tr && tr.TripId == trip.TripId ? tr.NextCheckpoint : 0;
            var stops = trip.Pattern.Checkpoints
                .Where(c => c.Kind == CheckpointKind.Stop && c.Index >= Math.Max(1, next))
                .Take(12)
                .Select(c => StopTimeAt(trip, view.ServiceDate, c))
                .ToArray();
            return new VehicleDetail(view, stops);
        }
    }

    public Departure[] Departures(string stopId, int limit = 20)
    {
        lock (_gate)
        {
            if (!_stopPatterns.TryGetValue(stopId, out var entries)) return [];
            var now = Now();
            var vehiclesByTrip = _vehicles.Values.Where(v => v.TripId is not null && v.ServiceDate is not null)
                .GroupBy(v => (v.ServiceDate!, v.TripId!)).ToDictionary(g => g.Key, g => g.First());
            var candidates = _metro.Keys.Union(vehiclesByTrip.Keys).ToList();
            var patterns = entries.ToDictionary(e => e.Pattern, e => e.Checkpoint);

            var result = new List<(double Sort, Departure D)>();
            foreach (var (date, tripId) in candidates)
            {
                if (_feeds.Resolve(tripId)?.Trip is not { } trip || !patterns.TryGetValue(trip.Pattern, out var cpIndex) || cpIndex == 0)
                    continue;
                var vehicle = vehiclesByTrip.GetValueOrDefault((date, tripId));
                if (vehicle is not null && _detector!.Trackers.GetValueOrDefault(vehicle.Id) is { } tr && tr.TripId == tripId && tr.NextCheckpoint > cpIndex)
                    continue; // already gone
                var st = StopTimeAt(trip, date, trip.Pattern.Checkpoints[cpIndex]);
                var best = st.V1 ?? st.Metro ?? st.Scheduled;
                if (best is null || best < now - 60 || best > now + 90 * 60) continue;
                var route = RouteOf(trip.RouteId);
                result.Add((best.Value, new Departure(tripId, trip.RouteId, route?.ShortName ?? trip.RouteId, route?.Color ?? "4F5B66",
                    route?.TextColor ?? "FFFFFF", trip.Headsign, vehicle?.Id, st.Scheduled, st.Metro, st.V0, st.V1)));
            }
            return result.OrderBy(r => r.Sort).Take(limit).Select(r => r.D).ToArray();
        }
    }

    /// <summary>Every predictor's departure time for one stop of a trip, as of now.</summary>
    StopTime StopTimeAt(TripInfo trip, string serviceDate, Checkpoint cp)
    {
        var origin = StaticFeed.ServiceDayOrigin(serviceDate, Zone).ToUnixTimeSeconds();
        var pos = Array.FindIndex(trip.Pattern.Stops, s => s.Sequence == cp.StopSequence);
        double? scheduled = pos >= 0 && pos < trip.ScheduledDepartures.Length && trip.ScheduledDepartures[pos] >= 0
            ? origin + trip.ScheduledDepartures[pos] : null;
        double? metro = _metro.TryGetValue((serviceDate, trip.TripId), out var m) && m.TryGetValue(cp.StopSequence, out var t) ? t : null;

        double? v0 = null, v1 = null;
        if (_stats is not null)
        {
            var rows = _recent.GetValueOrDefault((serviceDate, trip.TripId)) ?? [];
            var dayType = ServiceCalendar.DayTypeOf(serviceDate);
            var now = Now();
            var empty = CrossingHistory.From([]);
            TripInfo? Resolve(string id) => _feeds.Resolve(id)?.Trip;
            // The trip before this one in its block, for a trip whose bus is still finishing it.
            IReadOnlyList<CrossingRow> RowsOf(string id) => _recent.GetValueOrDefault((serviceDate, id)) ?? [];
            v0 = new SegmentEtaPredictor("v0", _stats, null, empty, Resolve, Zone).Predict(trip, cp.Index, rows, dayType, origin, now);
            if (_pace is not null)
                v1 = new SegmentEtaPredictor("v1", _stats, _pace, empty, Resolve, Zone) { UseBlocks = true }
                    .Predict(trip, cp.Index, rows, dayType, origin, now, RowsOf);
        }
        var name = StopInfoOf(cp.Id)?.Name ?? cp.Id;
        return new StopTime(cp.Id, name, cp.StopSequence, Math.Round(cp.Dist, 1), scheduled, metro, Round(v0), Round(v1));

        static double? Round(double? v) => v is { } x ? Math.Round(x) : null;
    }

    public object Health()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var s = _detector?.Stats;
            return new
            {
                serverTime = Now(),
                feeds = _feeds.Feeds.Select(f => new { f.Version, trips = f.Trips.Count, patterns = f.PatternCount }),
                vehicleLagSeconds = _vehiclesFetchedAt is { } v ? (now - v).TotalSeconds : (double?)null,
                tripUpdatesLagSeconds = _metroFetchedAt is { } m ? (now - m).TotalSeconds : (double?)null,
                vehicles = _vehicles.Count,
                vehiclesOnTrip = _vehicles.Values.Count(x => x.Dist is not null),
                activeTrips = _detector?.ActiveTrackers ?? 0,
                metroTrips = _metro.Count,
                recentTrips = _recent.Count,
                model = new { builtAt = _statsBuiltAt, segments = _stats?.TravelTable.Keys ?? 0, stops = _stats?.DwellTable.Keys ?? 0 },
                detector = s is null ? null : new
                {
                    s.Pings, s.Used, s.Duplicate, s.Stale, s.NoTrip, s.UnknownTrip, s.OffRoute, s.PreStartOffRoute, s.Ambiguous, s.Lost, s.Crossings, s.TripsStarted,
                },
            };
        }
    }
}
