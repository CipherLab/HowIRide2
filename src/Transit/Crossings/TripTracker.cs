using Transit.Geo;
using Transit.Gtfs;

namespace Transit.Crossings;

/// <summary>
/// One row of history: when a vehicle on a trip entered and left a checkpoint.
/// Arrive is null at a trip's first stop (layover, not arrival) or when tracking began inside the
/// zone; Depart is null at the last stop. Gaps are the seconds between the two pings each time was
/// interpolated from, so a time inferred across a long GPS silence can be down-weighted.
/// Detour is set when off-route pings fell between those pings.
/// </summary>
public sealed record StopCrossing(
    string ServiceDate,
    string TripId,
    string RouteId,
    int DirectionId,
    string VehicleId,
    Checkpoint Checkpoint,
    DateTimeOffset? Arrive,
    DateTimeOffset? Depart,
    double? ArriveGapSeconds,
    double? DepartGapSeconds,
    bool Detour,
    TripInfo Trip);

public sealed record PingTrace(TripTracker Tracker, DateTimeOffset Time, double? LastDist, double WindowMin, double WindowMax, LineMatch? Windowed, LineMatch? Nearest);

/// <summary>
/// Follows one vehicle along one trip in one dimension (meters along the shape) and emits a
/// <see cref="StopCrossing"/> as it passes each checkpoint.
/// </summary>
public sealed class TripTracker
{
    /// <summary>
    /// Pings must be this close to the shape to move the vehicle forward. 85% of real pings sit within
    /// 30 m; the 35-75 m band is mostly GPS noise and terminal manoeuvring, which once let a parked
    /// bus "depart" its first stop.
    /// </summary>
    public const double ConfidentMatchMeters = 35;

    /// <summary>Pings farther than this from the shape are treated as off-route (detour). Between the two thresholds a ping is ignored.</summary>
    public const double OffRouteMeters = 75;

    /// <summary>How far behind its last position a ping may match; anything farther back is GPS jitter and gets clamped forward.</summary>
    public const double BackwardSlackMeters = 50;

    /// <summary>Fastest plausible progress, bounding how far ahead the next ping can match. Covers light rail and freeway express.</summary>
    public const double MaxSpeedMetersPerSecond = 30;

    /// <summary>Minimum lookahead so short ping gaps still allow for GPS error.</summary>
    public const double MinLookaheadMeters = 300;

    const double HintSlackMeters = 200;

    readonly TripInfo _trip;
    readonly Checkpoint[] _checkpoints;
    readonly Polyline _shape;

    bool _started;
    // Set when the first zone boundary is crossed. Until then nothing is on record, so the tracker may
    // re-anchor backwards freely; vehicles shuffle around terminals before a trip begins.
    bool _committed;
    int _next;
    DateTimeOffset _lastTime;
    double _lastDist;
    bool _offRouteSinceLast;

    // Entry into _checkpoints[_next] has been crossed and departure is pending.
    bool _arrived;
    DateTimeOffset? _arriveAt;
    double? _arriveGap;
    bool _arriveDetour;

    public TripTracker(TripInfo trip, string vehicleId, string serviceDate)
    {
        _trip = trip;
        _checkpoints = trip.Pattern.Checkpoints;
        _shape = trip.Pattern.Shape;
        VehicleId = vehicleId;
        ServiceDate = serviceDate;
    }

    public string VehicleId { get; }
    public string ServiceDate { get; }
    public string TripId => _trip.TripId;
    public DateTimeOffset LastPing { get; private set; } = DateTimeOffset.MinValue;
    public int OffRoutePings { get; private set; }

    /// <summary>Off-route pings before the first on-route one: pulling out of the garage or waiting off-street at a terminal, not detours.</summary>
    public int PreStartOffRoutePings { get; private set; }

    /// <summary>Pings 35-75 m from the shape: too far to trust, too close to call a detour.</summary>
    public int AmbiguousPings { get; private set; }

    /// <summary>Pings clearly on the shape but outside the search window: the tracker has lost the vehicle. Should be near zero.</summary>
    public int LostPings { get; private set; }
    public bool Finished => _started && _next >= _checkpoints.Length;

    /// <summary>Diagnostics hook: called for every ping with the windowed match and the unrestricted nearest match.</summary>
    public Action<PingTrace>? Trace { get; set; }

    /// <param name="nextStopSequence">The feed's current_stop_sequence, used only to place the first ping on loop routes.</param>
    public void OnPing(DateTimeOffset time, double x, double y, int? nextStopSequence, List<StopCrossing> output)
    {
        if (time <= LastPing) return;
        LastPing = time;

        var (min, max) = SearchWindow(time, nextStopSequence);
        var windowed = _shape.Match(x, y, min, max);
        Trace?.Invoke(new PingTrace(this, time, _started ? _lastDist : null, min, max, windowed, _shape.Match(x, y)));
        if (windowed is not { Offset: <= ConfidentMatchMeters } match)
        {
            Reject(windowed, x, y);
            return;
        }

        if (!_started || (!_committed && match.Distance < _lastDist))
        {
            Start(match.Distance);
            (_lastTime, _lastDist, _offRouteSinceLast) = (time, match.Distance, false);
            return;
        }

        // Progress along a trip only goes forward; a small backward match is GPS jitter.
        var dist = Math.Max(match.Distance, _lastDist);
        Advance(_lastTime, _lastDist, time, dist, _offRouteSinceLast, output);
        (_lastTime, _lastDist, _offRouteSinceLast) = (time, dist, false);
    }

    void Reject(LineMatch? windowed, double x, double y)
    {
        if (windowed is { Offset: <= OffRouteMeters })
        {
            AmbiguousPings++;
            return;
        }
        if (_started && _shape.Match(x, y) is { Offset: <= ConfidentMatchMeters })
        {
            LostPings++;
            return;
        }
        OffRoutePings++;
        if (!_started) PreStartOffRoutePings++;
        _offRouteSinceLast = true;
    }

    (double Min, double Max) SearchWindow(DateTimeOffset time, int? nextStopSequence)
    {
        if (_started)
        {
            var seconds = (time - _lastTime).TotalSeconds;
            var behind = _committed ? _lastDist - BackwardSlackMeters : double.NegativeInfinity;
            return (behind, _lastDist + Math.Max(MinLookaheadMeters, MaxSpeedMetersPerSecond * seconds));
        }

        // GTFS-rt current_stop_sequence without a status means "in transit to": the bus is between the previous stop and this one.
        var stops = _trip.Pattern.Stops;
        var i = nextStopSequence is int seq ? Array.FindIndex(stops, s => s.Sequence == seq) : -1;
        if (i < 0)
            return (double.NegativeInfinity, double.PositiveInfinity);
        var from = i > 0 ? stops[i - 1].Dist : stops[0].Dist;
        return (from - HintSlackMeters, stops[i].Dist + HintSlackMeters);
    }

    void Start(double dist)
    {
        _started = true;
        (_arrived, _arriveAt, _arriveGap, _arriveDetour) = (false, null, null, false);
        // Only checkpoints still fully or partly ahead can be timed.
        _next = Array.FindIndex(_checkpoints, c => c.Exit > dist);
        if (_next < 0)
        {
            _next = _checkpoints.Length;
            return;
        }
        if (_checkpoints[_next].Entry <= dist)
        {
            // Already inside this stop's zone: departure is knowable, arrival isn't.
            if (_next == _checkpoints.Length - 1)
                _next = _checkpoints.Length;
            else
                (_arrived, _arriveAt, _arriveGap, _arriveDetour) = (true, null, null, false);
        }
    }

    void Advance(DateTimeOffset t1, double d1, DateTimeOffset t2, double d2, bool detour, List<StopCrossing> output)
    {
        var gap = (t2 - t1).TotalSeconds;
        while (_next < _checkpoints.Length)
        {
            var cp = _checkpoints[_next];
            var first = _next == 0;
            var last = _next == _checkpoints.Length - 1;

            if (!_arrived)
            {
                if (d2 < cp.Entry) return;
                _arrived = _committed = true;
                // A trip's first stop is a layover point; time spent there isn't an arrival.
                _arriveAt = first ? null : Interpolate(t1, d1, t2, d2, cp.Entry);
                _arriveGap = first ? null : gap;
                _arriveDetour = detour;
                if (last)
                {
                    // Terminal: record arrival only; the bus sits there between trips.
                    Emit(cp, departAt: null, departGap: null, detour, output);
                    continue;
                }
            }

            if (d2 < cp.Exit) return;
            Emit(cp, Interpolate(t1, d1, t2, d2, cp.Exit), gap, detour, output);
        }
    }

    void Emit(Checkpoint cp, DateTimeOffset? departAt, double? departGap, bool detour, List<StopCrossing> output)
    {
        output.Add(new StopCrossing(ServiceDate, _trip.TripId, _trip.RouteId, _trip.DirectionId, VehicleId, cp,
            _arriveAt, departAt, _arriveGap, departGap, _arriveDetour || detour, _trip));
        _committed = true; // also covers a departure from a zone the tracker started inside
        _next++;
        (_arrived, _arriveAt, _arriveGap, _arriveDetour) = (false, null, null, false);
    }

    /// <summary>Time at which distance <paramref name="boundary"/> was passed, assuming constant speed between the two pings.</summary>
    static DateTimeOffset Interpolate(DateTimeOffset t1, double d1, DateTimeOffset t2, double d2, double boundary)
    {
        if (d2 <= d1) return t2;
        var f = Math.Clamp((boundary - d1) / (d2 - d1), 0, 1);
        return t1 + (t2 - t1) * f;
    }
}
