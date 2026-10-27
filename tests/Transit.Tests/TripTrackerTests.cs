using Transit.Crossings;
using Transit.Geo;
using Transit.Gtfs;

namespace Transit.Tests;

public sealed class TripTrackerTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);

    // Straight street along y=0, stops at 0, 500 and 1000 m: zones [-30,30], [470,530], [970,1030].
    static TripTracker Straight() => Tracker(new Polyline([0, 1000], [0, 0], [0, 1000]),
        [new("A", 1, 0), new("B", 2, 500), new("C", 3, 1000)]);

    static TripTracker Tracker(Polyline shape, StopPoint[] stops) =>
        new(new TripInfo("trip", "route", 0, new Pattern("shape", shape, stops)), "bus", "20261027");

    static List<StopCrossing> Run(TripTracker tracker, params (double Seconds, double X, double Y)[] pings) =>
        Run(tracker, null, pings);

    static List<StopCrossing> Run(TripTracker tracker, int? hint, params (double Seconds, double X, double Y)[] pings)
    {
        var output = new List<StopCrossing>();
        foreach (var (s, x, y) in pings)
            tracker.OnPing(T0.AddSeconds(s), x, y, hint, output);
        return output;
    }

    static double? Sec(DateTimeOffset? t) => t is { } v ? Math.Round((v - T0).TotalSeconds, 2) : null;

    [Fact]
    public void InterpolatesArrivalAndDepartureAtZoneEdges()
    {
        var crossings = Run(Straight(), (0, 0, 0), (10, 100, 0), (60, 600, 0), (100, 1000, 0));

        Assert.Equal(["A", "B", "C"], crossings.Select(c => c.Checkpoint.Id));
        // A: trip origin, so no arrival; departs when passing 30 m, 3 s into a 10 m/s run.
        Assert.Equal((null, 3.0), (Sec(crossings[0].Arrive), Sec(crossings[0].Depart)));
        // B: 100->600 m over 50 s, so 470 m at 47 s and 530 m at 53 s.
        Assert.Equal((47.0, 53.0), (Sec(crossings[1].Arrive), Sec(crossings[1].Depart)));
        Assert.Equal((50.0, 50.0), (crossings[1].ArriveGapSeconds, crossings[1].DepartGapSeconds));
        // C: terminal, so arrival only.
        Assert.Equal((97.0, null), (Sec(crossings[2].Arrive), Sec(crossings[2].Depart)));
        Assert.All(crossings, c => Assert.False(c.Detour));
    }

    [Fact]
    public void SeparatesDwellFromTravel()
    {
        var crossings = Run(Straight(), (0, 400, 0), (10, 490, 0), (40, 500, 0), (50, 600, 0));

        var b = Assert.Single(crossings);
        Assert.Equal("B", b.Checkpoint.Id);
        Assert.Equal(7.78, Sec(b.Arrive));
        Assert.Equal(43.0, Sec(b.Depart));
        Assert.Equal((10.0, 10.0), (b.ArriveGapSeconds, b.DepartGapSeconds));
    }

    [Fact]
    public void ClampsBackwardJitter()
    {
        var crossings = Run(Straight(), (0, 400, 0), (10, 480, 0), (20, 470, 0), (30, 600, 0));

        var b = Assert.Single(crossings);
        Assert.Equal(8.75, Sec(b.Arrive));
        // Departure interpolates from the clamped 480 m at 20 s, not the jittered 470 m.
        Assert.Equal(24.17, Sec(b.Depart));
    }

    [Fact]
    public void FlagsCrossingsBracketingAnOffRoutePing()
    {
        var tracker = Straight();
        var crossings = Run(tracker, (0, 100, 0), (10, 300, 200), (20, 600, 0));

        var b = Assert.Single(crossings);
        Assert.True(b.Detour);
        Assert.Equal(14.8, Sec(b.Arrive));
        Assert.Equal(20.0, b.ArriveGapSeconds);
        Assert.Equal(1, tracker.OffRoutePings);
    }

    [Fact]
    public void JoiningMidTripSkipsCheckpointsAlreadyPassed()
    {
        var crossings = Run(Straight(), (0, 700, 0), (30, 1000, 0));

        Assert.Equal(["C"], crossings.Select(c => c.Checkpoint.Id));
    }

    [Fact]
    public void JoiningInsideAStopZoneRecordsDepartureOnly()
    {
        var crossings = Run(Straight(), (0, 510, 0), (10, 610, 0));

        var b = Assert.Single(crossings);
        Assert.Null(b.Arrive);
        Assert.Equal(2.0, Sec(b.Depart));
    }

    [Fact]
    public void StopSequenceHintPlacesFirstPingOnCorrectLegOfLoop()
    {
        // Out along y=0, back along y=20. Stop D (seq 4) sits on the return leg at x=500.
        var shape = new Polyline([0, 1000, 1000, 0], [0, 0, 20, 20], [0, 1000, 1020, 2020]);
        var tracker = Tracker(shape, [new("A", 1, 0), new("B", 2, 500), new("C", 3, 1010), new("D", 4, 1520), new("E", 5, 2020)]);

        // (400, 8) is nearer the outbound leg, but the bus reports it's heading to D, so it's on the way back.
        var crossings = Run(tracker, hint: 4, (0, 400, 8), (10, 300, 20), (20, 0, 20));

        var e = Assert.Single(crossings);
        Assert.Equal("E", e.Checkpoint.Id);
        Assert.Equal(19.0, Sec(e.Arrive));
    }

    [Fact]
    public void IgnoresRepeatedTimestamps()
    {
        var tracker = Straight();
        var crossings = Run(tracker, (0, 400, 0), (10, 600, 0), (10, 900, 0));

        Assert.Single(crossings);
        Assert.Equal(T0.AddSeconds(10), tracker.LastPing);
    }

    [Fact]
    public void NoisyPingNearTerminalDoesNotFakeADeparture()
    {
        // Parked at the origin, one ping 60 m off the line at 200 m along, then still parked.
        var tracker = Straight();
        var crossings = Run(tracker, (0, 0, 0), (10, 5, 0), (20, 200, 60), (30, 3, 0), (40, 10, 0), (100, 600, 0));

        Assert.Equal(1, tracker.AmbiguousPings);
        Assert.Equal((0, 0), (tracker.OffRoutePings, tracker.LostPings));
        Assert.Equal(["A", "B"], crossings.Select(c => c.Checkpoint.Id));
        // Departure from A is interpolated from the 10 m ping at 40 s, not the bogus 200 m one.
        Assert.Equal(42.03, Sec(crossings[0].Depart));
        Assert.False(crossings[0].Detour);
    }

    [Fact]
    public void ReanchorsBackwardsUntilFirstBoundaryIsCrossed()
    {
        // Like a light-rail terminal: the shape starts on a tail track 200 m before the first platform.
        var tracker = Tracker(new Polyline([0, 1000], [0, 0], [0, 1000]), [new("A", 1, 200), new("B", 2, 600), new("C", 3, 1000)]);

        // First report is just past A's zone, then the train is actually back on the tail track before leaving.
        var crossings = Run(tracker, (0, 260, 0), (10, 50, 0), (20, 55, 0), (60, 400, 0), (80, 700, 0));

        Assert.Equal(0, tracker.LostPings);
        Assert.Equal(["A", "B"], crossings.Select(c => c.Checkpoint.Id));
        Assert.Equal((null, 40.29), (Sec(crossings[0].Arrive), Sec(crossings[0].Depart)));
        Assert.Equal((71.33, 75.33), (Sec(crossings[1].Arrive), Sec(crossings[1].Depart)));
    }

    [Fact]
    public void CountsOnShapePingsOutsideWindowAsLostNotDetour()
    {
        var tracker = Straight();
        // Past B (so a crossing is on record), then a ping on the line at 100 m: far behind the window, but squarely on the shape.
        Run(tracker, (0, 400, 0), (10, 600, 0), (20, 100, 0));

        Assert.Equal((1, 0), (tracker.LostPings, tracker.OffRoutePings));
    }
}
