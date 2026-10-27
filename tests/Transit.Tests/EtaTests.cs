using Transit.Backtest;
using Transit.Eta;
using Transit.Gtfs;

namespace Transit.Tests;

public sealed class EtaTests : IDisposable
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    readonly string _dir = Directory.CreateTempSubdirectory("eta-").FullName;
    readonly StaticFeed _feed;

    public EtaTests() => _feed = StaticFeed.Load(TestGtfs.Write(Path.Combine(_dir, "gtfs.zip")));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SlotMediansFallBackFromSlotToNeighboursToDayTypeToAll()
    {
        var m = new SlotMedians();
        foreach (var v in new[] { 10.0, 20, 30 }) m.Add("seg", DayType.Weekday, 40, v);   // 10:00
        foreach (var v in new[] { 100.0, 200, 300 }) m.Add("seg", DayType.Weekday, 70, v); // 17:30
        m.Add("seg", DayType.Sunday, 40, 999);
        m.Build();

        Assert.Equal(20, m.Get("seg", DayType.Weekday, 40));
        Assert.Equal(20, m.Get("seg", DayType.Weekday, 43));   // 45 min away: borrows the 10:00 cell
        Assert.Equal(65, m.Get("seg", DayType.Weekday, 55));   // nothing within an hour: weekday median of all six
        Assert.Equal(100, m.Get("seg", DayType.Saturday, 40)); // no Saturdays: median of all seven samples
        Assert.Null(m.Get("other", DayType.Weekday, 40));
    }

    static SegmentStats Stats(params (string Key, double Seconds)[] travel)
    {
        var t = new SlotMedians();
        foreach (var (key, seconds) in travel)
            for (var i = 0; i < SlotMedians.MinSamples; i++) t.Add(key, DayType.Weekday, 0, seconds);
        var d = new SlotMedians();
        for (var i = 0; i < SlotMedians.MinSamples; i++) d.Add("B", DayType.Weekday, 0, 10);
        return new SegmentStats(t.Build(), d.Build(), new ServiceCalendar(Chicago));
    }

    static CrossingRow DepartedA(double at) => new("20260101", "T1", "R", 0, "A", null, true, 0, null, at, null, 10, false);

    double Predict(SegmentStats stats, CrossingHistory history, double now, double actual)
    {
        var target = new BacktestTarget("20260101", "T1", 2, "B", "R", DateTimeOffset.FromUnixTimeSeconds((long)actual));
        var table = new PredictionTable(1);
        new SegmentEtaPredictor("v0", stats, null, history, id => _feed.Trips.GetValueOrDefault(id), Chicago).Fill([target], table);
        var h = Array.IndexOf(Horizons.Minutes, (int)((actual - now) / 60));
        return table[0, h];
    }

    [Fact]
    public void SumsSegmentTravelAndTargetDwellFromLastKnownDeparture()
    {
        var stats = Stats(("A|B", 60));
        var departed = 1_767_276_000.0; // 2026-01-01 14:00Z, after the scheduled 08:00 CST start
        // Read 2 min before an actual departure at +175 s: A's departure (known at +50 s) is on record.
        var p = Predict(stats, CrossingHistory.From([DepartedA(departed)]), departed + 55, departed + 175);

        // Arrive B at +60 s (the knowledge-lag floor, now - 55 s = 0, doesn't bind), then dwell 10 s.
        Assert.Equal(departed + 70, p);
    }

    [Fact]
    public void FallsBackToScheduledStartWhenTripNotSeen()
    {
        var stats = Stats(("A|B", 60));
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var scheduledStart = origin + 8 * 3600;
        // 30 min before the scheduled start, nothing on record for the trip.
        var p = Predict(stats, CrossingHistory.From([]), scheduledStart - 1800, scheduledStart);

        Assert.Equal(scheduledStart + 70, p);
    }
}
