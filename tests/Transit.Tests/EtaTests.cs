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

    [Fact]
    public void HistoryLoadedFromTheStoreSharesRepeatedStrings()
    {
        // Two weeks of crossings repeat the same few thousand ids millions of times.
        var db = Path.Combine(_dir, "crossings.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE crossings (service_date TEXT, trip_id TEXT, route_id TEXT, checkpoint_index INT, checkpoint_id TEXT, prev_checkpoint_id TEXT,
                    kind TEXT, dist_m REAL, arrive REAL, depart REAL, arrive_gap_s REAL, depart_gap_s REAL, detour INT);
                INSERT INTO crossings VALUES ('20260101', 'T1', 'R', 0, 'A', NULL, 'stop', 0, NULL, 100, NULL, 5, 0);
                INSERT INTO crossings VALUES ('20260101', 'T1', 'R', 1, 'B', 'A', 'stop', 9, 150, 160, 5, 5, 0);
                INSERT INTO crossings VALUES ('20260101', 'T2', 'R', 0, 'A', NULL, 'stop', 0, NULL, 200, NULL, 5, 0);
                """;
            cmd.ExecuteNonQuery();
        }

        var history = CrossingHistory.Load(db, "20260101", "20260101");

        var (a, b, other) = (history.Trip("20260101", "T1")[0], history.Trip("20260101", "T1")[1], history.Trip("20260101", "T2")[0]);
        Assert.Same(a.TripId, b.TripId);
        Assert.Same(a.RouteId, other.RouteId);
        Assert.Same(a.ServiceDate, other.ServiceDate);
        Assert.Same(a.CheckpointId, other.CheckpointId);
        Assert.Same(a.CheckpointId, b.PreviousId); // the same stop reached from either column
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

    /// <summary>Buses left stop A this many seconds after the timetable, in every 15-minute slot's history.</summary>
    static SegmentStats StatsWithStartDelay(double delay)
    {
        var travel = new SlotMedians();
        var dwell = new SlotMedians();
        var starts = new SlotMedians();
        for (var i = 0; i < SlotMedians.MinSamples; i++)
        {
            travel.Add("A|B", DayType.Weekday, 0, 60);
            dwell.Add("B", DayType.Weekday, 0, 10);
            starts.Add("A", DayType.Weekday, 0, delay);
        }
        return new SegmentStats(travel.Build(), dwell.Build(), new ServiceCalendar(Chicago), starts.Build());
    }

    [Fact]
    public void NotStartedTripLeavesItsFirstStopTheTypicalDelayAfterTheTimetable()
    {
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var start = origin + 8 * 3600;

        var p = Predict(StatsWithStartDelay(40), CrossingHistory.From([]), start - 1800, start);

        Assert.Equal(start + 40 + 70, p);
    }

    [Fact]
    public void TripOverdueBeyondItsTypicalDelayIsAssumedToLeaveNowNotInThePast()
    {
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var start = origin + 8 * 3600;
        var now = start + 120;

        var p = Predict(StatsWithStartDelay(40), CrossingHistory.From([]), now, now + 120);

        Assert.Equal(now + 70, p);
    }

    [Fact]
    public void AnEarlyDepartureHabitMovesThePredictionEarlierToo()
    {
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var start = origin + 8 * 3600;

        var p = Predict(StatsWithStartDelay(-20), CrossingHistory.From([]), start - 1800, start);

        Assert.Equal(start - 20 + 70, p);
    }

    [Fact]
    public void BusStillOnThePreviousTripOverridesTheTypicalStartDelay()
    {
        var (first, second, trips) = Block();
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var departed = origin + 8 * 3600 + 20 * 60; // T1 left A at 08:20, so it can't be back for T2's 08:15 start
        var now = departed + 55;
        var rows = new Dictionary<string, IReadOnlyList<CrossingRow>> { ["T1"] = [DepartedA(departed)], ["T2"] = [] };
        var stats = StatsWithStartDelay(40);
        var model = new SegmentEtaPredictor("v1", stats, null, CrossingHistory.From([]), id => trips.GetValueOrDefault(id), Chicago)
            { UseBlocks = true, MinLayoverSeconds = 60 };
        var b = Array.FindIndex(second.Pattern.Checkpoints, c => c.Id == "B");

        var firstIn = model.Predict(first, first.Pattern.Checkpoints.Length - 1, rows["T1"], DayType.Weekday, origin, now)!.Value - SegmentEtaPredictor.DefaultDwellSeconds;

        Assert.Equal(firstIn + 60 + 70, model.Predict(second, b, [], DayType.Weekday, origin, now, id => rows[id]));
    }

    SegmentEtaPredictor BlockModel(Dictionary<string, TripInfo> trips, SegmentStats stats) =>
        new("v1", stats, null, CrossingHistory.From([]), id => trips.GetValueOrDefault(id), Chicago) { UseBlocks = true, MinLayoverSeconds = 60 };

    [Fact]
    public void PreviousTripNeverSeenLongAfterItsScheduleDoesNotHoldTheNextOne()
    {
        // T1 was due out at 08:00 and has never appeared: cancelled or run by another bus, so T2's bus isn't on it.
        var (_, second, trips) = Block();
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var now = origin + 8 * 3600 + 20 * 60;
        var b = Array.FindIndex(second.Pattern.Checkpoints, c => c.Id == "B");

        var p = BlockModel(trips, Stats(("A|B", 60))).Predict(second, b, [], DayType.Weekday, origin, now, _ => []);

        Assert.Equal(now + 70, p);
    }

    [Fact]
    public void PreviousTripRecordedOnlyAfterTheQueryStillCountsAsNotSeen()
    {
        // In a replay the store holds T1's later crossings. Reading them would leak the future into the score.
        var (_, second, trips) = Block();
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var now = origin + 8 * 3600 + 20 * 60;
        var b = Array.FindIndex(second.Pattern.Checkpoints, c => c.Id == "B");

        var p = BlockModel(trips, Stats(("A|B", 60))).Predict(second, b, [], DayType.Weekday, origin, now, _ => [DepartedA(now + 60)]);

        Assert.Equal(now + 70, p);
    }

    [Fact]
    public void PreviousTripOnlyAFewMinutesOverdueStillHoldsTheNextOne()
    {
        // T1 is 14 minutes overdue, not long enough to write off: T2's 08:15 start waits for it.
        var (first, second, trips) = Block();
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var now = origin + 8 * 3600 + 14 * 60;
        var b = Array.FindIndex(second.Pattern.Checkpoints, c => c.Id == "B");
        var model = BlockModel(trips, Stats(("A|B", 60)));

        var firstIn = model.Predict(first, first.Pattern.Checkpoints.Length - 1, [], DayType.Weekday, origin, now)!.Value - SegmentEtaPredictor.DefaultDwellSeconds;

        Assert.True(firstIn + 60 > origin + 8 * 3600 + 15 * 60, "T1 should finish after T2's scheduled start");
        Assert.Equal(firstIn + 60 + 70, model.Predict(second, b, [], DayType.Weekday, origin, now, _ => []));
    }

    [Fact]
    public void OverdueTripStillUnseenIsPredictedAsIfItLeftOnTime()
    {
        // Tracking often starts a few stops in, so a trip well past its start with no sighting has usually gone already.
        var trip = _feed.Trips["T1"];
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var start = origin + 8 * 3600.0;
        var now = start + 400;
        var c = trip.Pattern.Checkpoints.Length - 1;
        var model = new SegmentEtaPredictor("v1", Stats(("A|B", 60)), null, CrossingHistory.From([]), id => _feed.Trips.GetValueOrDefault(id), Chicago);

        var unseen = model.Predict(trip, c, [], DayType.Weekday, origin, now);

        Assert.Equal(model.Predict(trip, c, [new CrossingRow("20260101", "T1", "R", 0, "A", null, true, 0, null, start, null, -1000, false)], DayType.Weekday, origin, now), unseen);
    }

    [Fact]
    public void ADepartureIsNeverPredictedInThePast()
    {
        var trip = _feed.Trips["T1"];
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var now = origin + 8 * 3600.0 + 400;
        var b = Array.FindIndex(trip.Pattern.Checkpoints, c => c.Id == "B");
        var model = new SegmentEtaPredictor("v1", Stats(("A|B", 60)), null, CrossingHistory.From([]), id => _feed.Trips.GetValueOrDefault(id), Chicago);

        Assert.Equal(now, model.Predict(trip, b, [], DayType.Weekday, origin, now));
    }

    [Fact]
    public void TripOverdueOnlyBecauseItsBusIsStillOutIsNotAssumedGone()
    {
        // T2 was due at 08:15, but T1 only left at 08:20: its bus can't have started T2, however overdue that looks.
        var (first, second, trips) = Block();
        var origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
        var departed = origin + 8 * 3600 + 20 * 60;
        var now = departed + 55;
        IReadOnlyList<CrossingRow> t1 = [DepartedA(departed)];
        var b = Array.FindIndex(second.Pattern.Checkpoints, c => c.Id == "B");
        var model = BlockModel(trips, Stats(("A|B", 60)));

        var firstIn = model.Predict(first, first.Pattern.Checkpoints.Length - 1, t1, DayType.Weekday, origin, now)!.Value - SegmentEtaPredictor.DefaultDwellSeconds;

        Assert.Equal(firstIn + 60 + 70, model.Predict(second, b, [], DayType.Weekday, origin, now, id => id == "T1" ? t1 : []));
    }

    static CrossingRow StartRow(string trip, string date, double depart, bool detour = false, int index = 0, string checkpoint = "A") =>
        new(date, trip, "R", index, checkpoint, null, true, 0, null, depart, null, 10, detour);

    [Fact]
    public void StartDelaysAreLearnedFromFirstStopDeparturesAgainstTheTimetable()
    {
        var calendar = new ServiceCalendar(Chicago);
        const double scheduled = 1_767_276_000; // 2026-01-01 08:00 CST, a Thursday
        var history = CrossingHistory.From([
            StartRow("T1", "20260101", scheduled + 30),
            StartRow("T2", "20260101", scheduled + 40),
            StartRow("T3", "20260101", scheduled + 50),
            StartRow("T4", "20260101", scheduled + 2 * 3600),                 // over 30 min late: a swapped bus, not a habit
            StartRow("T5", "20260101", scheduled + 900, detour: true),        // detoured
            StartRow("T6", "20260101", scheduled + 900, index: 3, checkpoint: "M"), // tracking began mid-route, not a start
            StartRow("T7", "20260101", scheduled + 900),                      // no timetable to compare with
        ]);

        var stats = SegmentStats.Build(history, calendar, (_, _) => true, scheduledStart: (_, trip) => trip == "T7" ? null : scheduled);

        Assert.Equal(40, stats.StartDelay("A", DayType.Weekday, scheduled));
        Assert.Equal(0, stats.StartDelay("M", DayType.Weekday, scheduled));
    }

    [Fact]
    public void StartDelaysExcludeDaysOutsideTheTrainingWindow()
    {
        var calendar = new ServiceCalendar(Chicago);
        const double scheduled = 1_767_276_000;
        var history = CrossingHistory.From([
            StartRow("T1", "20260101", scheduled + 30),
            StartRow("T2", "20260101", scheduled + 30),
            StartRow("T3", "20260101", scheduled + 30),
            StartRow("T1", "20260102", scheduled + 500),
        ]);

        var stats = SegmentStats.Build(history, calendar, (date, _) => date == "20260101", scheduledStart: (_, _) => scheduled);

        Assert.Equal(30, stats.StartDelay("A", DayType.Weekday, scheduled));
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
        var departed = origin + 8 * 3600 + 18 * 60; // T1 left A at 08:18, eighteen minutes late
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
