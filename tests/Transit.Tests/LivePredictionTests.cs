using Transit.Archive;
using Transit.Backtest;
using Transit.Eta;
using Transit.Gtfs;

namespace Transit.Tests;

public sealed class LivePredictionTests : IDisposable
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateTimeOffset Actual = new(2026, 1, 1, 14, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("live-").FullName;
    readonly StaticFeed _feed;
    readonly long _origin;

    public LivePredictionTests()
    {
        _feed = StaticFeed.Load(TestGtfs.Write(Path.Combine(_dir, "gtfs.zip")));
        _origin = StaticFeed.ServiceDayOrigin("20260101", Chicago).ToUnixTimeSeconds();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A to B takes 60 s and B's dwell is 10 s; everything else falls back to the default speed and dwell.</summary>
    SegmentEtaPredictor Model()
    {
        var travel = new SlotMedians();
        var dwell = new SlotMedians();
        for (var i = 0; i < SlotMedians.MinSamples; i++)
        {
            travel.Add("A|B", DayType.Weekday, 0, 60);
            dwell.Add("B", DayType.Weekday, 0, 10);
        }
        var stats = new SegmentStats(travel.Build(), dwell.Build(), new ServiceCalendar(Chicago));
        return new SegmentEtaPredictor("v1", stats, null, CrossingHistory.From([]), id => _feed.Trips.GetValueOrDefault(id), Chicago);
    }

    static CrossingRow Departed(int index, string checkpoint, double at) =>
        new("20260101", "T1", "R", index, checkpoint, null, true, 0, null, at, null, 10, false);

    int CheckpointOf(string id) => Array.FindIndex(_feed.Trips["T1"].Pattern.Checkpoints, c => c.Id == id);

    // ---- PredictAlong: a change to one code path but not the other would log numbers the app never showed ----

    [Fact]
    public void PredictAlongGivesEveryStopAheadTheSameTimeTheAppShows()
    {
        var trip = _feed.Trips["T1"];
        var departed = _origin + 8 * 3600.0;
        CrossingRow[] rows = [Departed(0, "A", departed)];
        var now = departed + 55;

        var along = Model().PredictAlong(trip, rows, DayType.Weekday, _origin, now);

        Assert.Equal([2, 3], along.Select(s => s.StopSequence));
        Assert.Equal(departed + 70, along[0].Depart); // 60 s to B, then its 10 s dwell
        Assert.Equal(Model().Predict(trip, trip.Pattern.Checkpoints.Length - 1, rows, DayType.Weekday, _origin, now), along[1].Depart);
    }

    [Fact]
    public void PredictAlongLeavesOutStopsTheBusHasAlreadyLeft()
    {
        var departed = _origin + 8 * 3600.0;
        CrossingRow[] rows = [Departed(0, "A", departed), Departed(CheckpointOf("B"), "B", departed + 70)];

        var along = Model().PredictAlong(_feed.Trips["T1"], rows, DayType.Weekday, _origin, departed + 200);

        Assert.Equal([3], along.Select(s => s.StopSequence));
    }

    [Fact]
    public void PredictAlongMatchesTheAppForAnOverdueTripWeHaveNotSeen()
    {
        var trip = _feed.Trips["T1"];
        var now = _origin + 8 * 3600.0 + 400;

        var along = Model().PredictAlong(trip, [], DayType.Weekday, _origin, now);

        Assert.Equal(Model().Predict(trip, CheckpointOf("B"), [], DayType.Weekday, _origin, now), along[0].Depart);
        Assert.Equal(Model().Predict(trip, trip.Pattern.Checkpoints.Length - 1, [], DayType.Weekday, _origin, now), along[1].Depart);
    }

    [Fact]
    public void PredictAlongStopsAtTheHorizon()
    {
        var departed = _origin + 8 * 3600.0;

        var along = Model().PredictAlong(_feed.Trips["T1"], [Departed(0, "A", departed)], DayType.Weekday, _origin, departed + 55, notAfter: departed + 71);

        Assert.Equal([2], along.Select(s => s.StopSequence));
    }

    // ---- the sampler: which trips and stops get logged each minute ----

    IReadOnlyList<LiveTripPrediction> Sample(double now, double window, params string[] tripIds) =>
        LivePredictionSampler.Sample(Model(), tripIds.Select(t => ("20260101", t)), id => _feed.Trips.GetValueOrDefault(id), (_, _) => [], Chicago, now, window);

    [Fact]
    public void SampleIncludesATripThatHasNotStartedOnceItsStopIsInsideTheWindow()
    {
        var start = _origin + 8 * 3600.0;

        var sample = Sample(start - 1800, 1900, "T2");

        var trip = Assert.Single(sample);
        Assert.Equal(("20260101", "T2"), (trip.ServiceDate, trip.TripId));
        Assert.Equal((2, (int)start + 70), Assert.Single(trip.Stops));
    }

    [Fact]
    public void SampleLeavesOutATripWithNothingInsideTheWindow() =>
        Assert.Empty(Sample(_origin + 8 * 3600.0 - 1800, 1000, "T2"));

    [Fact]
    public void SampleSkipsATripMissingFromStaticGtfs() =>
        Assert.Empty(Sample(_origin + 8 * 3600.0, 3600, "not-in-the-feed"));

    // ---- the log format ----

    [Fact]
    public void LogPayloadRoundTrips()
    {
        LiveTripPrediction[] trips = [new("20260101", "T1", [(2, 1_767_276_100), (3, 1_767_276_200)]), new("20260102", "T2", [(9, 1_767_362_000)])];

        var decoded = LivePredictionLog.Decode(LivePredictionLog.Encode(trips));

        Assert.Equal(2, decoded.Count);
        Assert.Equal(("20260101", "T1"), (decoded[0].ServiceDate, decoded[0].TripId));
        Assert.Equal([(2, 1_767_276_100), (3, 1_767_276_200)], decoded[0].Stops);
        Assert.Equal(("20260102", "T2"), (decoded[1].ServiceDate, decoded[1].TripId));
        Assert.Equal([(9, 1_767_362_000)], decoded[1].Stops);
    }

    [Fact]
    public void LogPayloadFromAnUnknownFormatVersionIsRejected() =>
        Assert.Throws<InvalidDataException>(() => LivePredictionLog.Decode([99, 0]));

    // ---- scoring the log: same rules as Metro's feed, and no peeking at later samples ----

    void Log(double secondsBefore, double? predictedAfterActual, RawArchiveWriter writer)
    {
        var trips = predictedAfterActual is { } after
            ? new[] { new LiveTripPrediction("20260101", "T1", [(2, (int)(Actual.ToUnixTimeSeconds() + after))]) }
            : [];
        writer.Append(Actual.AddSeconds(-secondsBefore), LivePredictionLog.Encode(trips));
    }

    double Scored(int minutes, params (double SecondsBefore, double? After)[] samples)
    {
        using (var w = new RawArchiveWriter(_dir, "log", _ => { }))
            foreach (var (s, after) in samples)
                Log(s, after, w);
        var table = new PredictionTable(1);
        new LivePredictor(Path.Combine(_dir, "log")).Fill([new BacktestTarget("20260101", "T1", 2, "B", "R", Actual)], table);
        return table[0, Array.IndexOf(Horizons.Minutes, minutes)] - Actual.ToUnixTimeSeconds();
    }

    [Fact]
    public void ScoringUsesTheLatestSampleBeforeTheQueryAndNeverALaterOne() =>
        Assert.Equal(100, Scored(10, (660, 300), (610, 100), (590, 50)));

    [Fact]
    public void ScoringTreatsAGapInTheLogAsNoPrediction() =>
        Assert.True(double.IsNaN(Scored(10, (700, 100))));

    [Fact]
    public void ScoringTreatsATripGoneFromTheLatestSampleAsNoPrediction() =>
        Assert.True(double.IsNaN(Scored(10, (660, 100), (610, null))));

    [Fact]
    public void ScoringToleratesAMissedMinute() =>
        Assert.Equal(100, Scored(10, (680, 100)));
}
