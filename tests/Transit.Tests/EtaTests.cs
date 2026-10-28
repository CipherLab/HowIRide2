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

    [Fact]
    public void TrimmedMeansKeepTheSlowRunsAMedianIgnoresButNotTheWorstOne()
    {
        var m = new SlotMedians(trim: 0.1);
        foreach (var v in new[] { 10.0, 10, 10, 10, 10, 10, 10, 40, 40, 900 }) m.Add("seg", DayType.Weekday, 40, v);
        m.Build();

        Assert.Equal(17.5, m.Get("seg", DayType.Weekday, 40)); // drops one 10 and the 900; the median would say 10
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

    /// <summary>T1 then T2 on one bus; T2 is rescheduled to start at 08:15, five minutes after T1 is due in.</summary>
    (TripInfo First, TripInfo Second, Dictionary<string, TripInfo> Trips) Block()
    {
        var first = _feed.Trips["T1"];
        var second = _feed.Trips["T2"] with { ScheduledDepartures = [8 * 3600 + 900, 8 * 3600 + 1200, 8 * 3600 + 1500], PreviousInBlock = "T1" };
        return (first, second, new() { ["T1"] = first, ["T2"] = second });
    }

    [Fact]
    public void NotStartedTripWaitsForItsBusToFinishThePreviousTrip()
    {
        var stats = Stats(("A|B", 60));
        var (first, second, trips) = Block();
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var departed = origin + 8 * 3600 + 20 * 60; // T1 left A at 08:20, twenty minutes late
        var now = departed + 55;
        var rows = new Dictionary<string, IReadOnlyList<CrossingRow>> { ["T1"] = [DepartedA(departed)], ["T2"] = [] };
        var end = first.Pattern.Checkpoints.Length - 1;
        var b = Array.FindIndex(second.Pattern.Checkpoints, c => c.Id == "B");
        SegmentEtaPredictor Model(bool blocks) => new("v0", stats, null, CrossingHistory.From([]), id => trips.GetValueOrDefault(id), Chicago)
            { UseBlocks = blocks, MinLayoverSeconds = 60 };

        // T1 reaches C when it would leave it, less the default dwell there.
        var firstIn = Model(false).Predict(first, end, rows["T1"], DayType.Weekday, origin, now)!.Value - SegmentEtaPredictor.DefaultDwellSeconds;
        Assert.True(firstIn > origin + 8 * 3600 + 900, "the late T1 should finish after T2's scheduled start");

        // Without blocks T2 is overdue, so it's assumed to leave right now.
        Assert.Equal(now + 70, Model(false).Predict(second, b, [], DayType.Weekday, origin, now, id => rows[id]));
        Assert.Equal(firstIn + 60 + 70, Model(true).Predict(second, b, [], DayType.Weekday, origin, now, id => rows[id]));
    }

    [Fact]
    public void TripWhoseBusIsAlreadyBackKeepsItsSchedule()
    {
        var stats = Stats(("A|B", 60));
        var (first, second, trips) = Block();
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var end = first.Pattern.Checkpoints.Length - 1;
        var arrived = origin + 8 * 3600 + 600; // T1 in at C on time, 08:10
        var rows = new Dictionary<string, IReadOnlyList<CrossingRow>>
        {
            ["T1"] = [new("20260101", "T1", "R", end, "C", null, true, 786, arrived, null, 10, null, false)],
        };
        var model = new SegmentEtaPredictor("v0", stats, null, CrossingHistory.From([]), id => trips.GetValueOrDefault(id), Chicago) { UseBlocks = true };
        var b = Array.FindIndex(second.Pattern.Checkpoints, c => c.Id == "B");

        Assert.Equal(origin + 8 * 3600 + 900 + 70, model.Predict(second, b, [], DayType.Weekday, origin, arrived + 120, id => rows.GetValueOrDefault(id) ?? []));
    }

    [Fact]
    public void BlocksLinkEachTripToTheOneBeforeIt()
    {
        var path = Path.Combine(_dir, "blocks.zip");
        TestGtfs.Write(path);
        // Rewrite trips.txt with a block, and T2 an hour later so the order is unambiguous.
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry("trips.txt")!.Delete();
            using (var w = new StreamWriter(zip.CreateEntry("trips.txt").Open()))
                w.Write("route_id,service_id,trip_id,direction_id,shape_id,block_id\nR,1,T2,0,S1,K\nR,1,T1,0,S1,K\n");
            var times = zip.GetEntry("stop_times.txt")!;
            string text;
            using (var r = new StreamReader(times.Open())) text = r.ReadToEnd();
            times.Delete();
            using var w2 = new StreamWriter(zip.CreateEntry("stop_times.txt").Open());
            w2.Write(string.Join("\n", text.Split('\n').Select(l => l.StartsWith("T2,") ? l.Replace(",08:", ",09:") : l)));
        }
        var feed = StaticFeed.Load(path);
        Assert.Null(feed.Trips["T1"].PreviousInBlock);
        Assert.Equal("T1", feed.Trips["T2"].PreviousInBlock);
    }
}
