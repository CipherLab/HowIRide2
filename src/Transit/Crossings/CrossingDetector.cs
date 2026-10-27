using Transit.Gtfs;

namespace Transit.Crossings;

/// <summary>A single vehicle position, decoupled from the protobuf types.</summary>
public sealed record VehiclePing(
    string VehicleId,
    string? TripId,
    string? StartDate,
    DateTimeOffset Timestamp,
    double Lat,
    double Lon,
    int? CurrentStopSequence);

public sealed class DetectorStats
{
    public long Pings, NoTrip, Stale, Duplicate, UnknownTrip, Used, OffRoute, PreStartOffRoute, Ambiguous, Lost, Crossings, TripsStarted;
    public Dictionary<string, (long Used, long OffRoute, long PreStart)> ByRoute { get; } = [];
}

/// <summary>
/// Routes pings to a <see cref="TripTracker"/> per vehicle, starting a new tracker whenever the
/// vehicle's trip changes (the handoff at the end of each trip on a block).
/// </summary>
public sealed class CrossingDetector(StaticFeed feed)
{
    /// <summary>Pings older than this when fetched are dropped; the feed's normal lag is ~40 s.</summary>
    public static readonly TimeSpan MaxPingAge = TimeSpan.FromSeconds(120);

    /// <summary>Trackers with no ping for this long are dropped; a vehicle that comes back starts fresh.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    readonly Dictionary<string, TripTracker> _trackers = new(StringComparer.Ordinal);
    readonly List<StopCrossing> _buffer = [];

    public DetectorStats Stats { get; } = new();

    /// <summary>Attached to trackers for trips on routes accepted by <see cref="TraceRoute"/>.</summary>
    public Action<PingTrace>? Trace { get; set; }
    public Func<string, bool> TraceRoute { get; set; } = _ => false;
    public int ActiveTrackers => _trackers.Count;

    public IReadOnlyList<StopCrossing> OnPing(VehiclePing ping, DateTimeOffset fetchedAt)
    {
        _buffer.Clear();
        Stats.Pings++;
        if (string.IsNullOrEmpty(ping.TripId))
        {
            Stats.NoTrip++;
            return _buffer;
        }
        if (fetchedAt - ping.Timestamp > MaxPingAge)
        {
            Stats.Stale++;
            return _buffer;
        }
        if (!feed.Trips.TryGetValue(ping.TripId, out var trip))
        {
            Stats.UnknownTrip++;
            return _buffer;
        }

        var serviceDate = ping.StartDate ?? "";
        if (!_trackers.TryGetValue(ping.VehicleId, out var tracker) || tracker.TripId != trip.TripId || tracker.ServiceDate != serviceDate)
        {
            _trackers[ping.VehicleId] = tracker = new TripTracker(trip, ping.VehicleId, serviceDate);
            if (Trace is not null && TraceRoute(trip.RouteId)) tracker.Trace = Trace;
            Stats.TripsStarted++;
        }
        if (ping.Timestamp <= tracker.LastPing)
        {
            // The feed repeats a vehicle's last report until it sends a new one.
            Stats.Duplicate++;
            return _buffer;
        }

        Stats.Used++;
        var (offBefore, preBefore, ambBefore, lostBefore) = (tracker.OffRoutePings, tracker.PreStartOffRoutePings, tracker.AmbiguousPings, tracker.LostPings);
        var (x, y) = feed.Projection.Project(ping.Lat, ping.Lon);
        tracker.OnPing(ping.Timestamp, x, y, ping.CurrentStopSequence, _buffer);
        var (off, pre) = (tracker.OffRoutePings - offBefore, tracker.PreStartOffRoutePings - preBefore);
        Stats.OffRoute += off;
        Stats.PreStartOffRoute += pre;
        Stats.Ambiguous += tracker.AmbiguousPings - ambBefore;
        Stats.Lost += tracker.LostPings - lostBefore;
        var r = Stats.ByRoute.GetValueOrDefault(trip.RouteId);
        Stats.ByRoute[trip.RouteId] = (r.Used + 1, r.OffRoute + off, r.PreStart + pre);
        Stats.Crossings += _buffer.Count;
        return _buffer;
    }

    public void PruneIdle(DateTimeOffset now)
    {
        foreach (var (vehicle, tracker) in _trackers.Where(kv => now - kv.Value.LastPing > IdleTimeout).ToList())
            _trackers.Remove(vehicle);
    }
}
