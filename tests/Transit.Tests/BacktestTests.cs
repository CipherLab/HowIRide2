using Google.Protobuf;
using Transit.Archive;
using Transit.Backtest;
using Transit.Gtfs;
using TransitRealtime;
using StopTimeUpdate = TransitRealtime.TripUpdate.Types.StopTimeUpdate;

namespace Transit.Tests;

public sealed class BacktestTests : IDisposable
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly DateTimeOffset Actual = new(2026, 1, 1, 14, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("backtest-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static int H(int minutes) => Array.IndexOf(Horizons.Minutes, minutes);

    [Theory]
    [InlineData("20261027", "2026-10-27T05:00:00Z")] // CDT: local midnight
    [InlineData("20261101", "2026-11-01T06:00:00Z")] // DST ends: noon CST minus 12 h is 1 h after midnight CDT
    public void ServiceDayOriginIsNoonMinusTwelveHours(string date, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), StaticFeed.ServiceDayOrigin(date, Chicago));

    [Theory]
    [InlineData("08:05:00", 29100)]
    [InlineData("25:10:00", 90600)]
    [InlineData("7:00:30", 25230)]
    [InlineData("", null)]
    public void ParsesGtfsClockTimes(string text, int? seconds) => Assert.Equal(seconds, StaticFeed.ParseClock(text));

    [Fact]
    public void ScheduleUsesScheduledDepartureOnTheServiceDay()
    {
        var feed = StaticFeed.Load(TestGtfs.Write(Path.Combine(_dir, "gtfs.zip")));
        var targets = new[] { new BacktestTarget("20260101", "T1", 2, "B", "R", Actual) };
        var table = new PredictionTable(1);

        new SchedulePredictor(id => feed.Trips.GetValueOrDefault(id), feed.TimeZone).Fill(targets, table);

        // B departs 08:05 CST = 14:05 UTC, at every horizon.
        var expected = new DateTimeOffset(2026, 1, 1, 14, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.All(Enumerable.Range(0, Horizons.Minutes.Length), h => Assert.Equal(expected, table[0, h]));
    }

    /// <summary>Archives a TripUpdates snapshot fetched <paramref name="secondsBefore"/> before the actual departure.</summary>
    void Snapshot(double secondsBefore, params StopTimeUpdate[] updates)
    {
        var message = new FeedMessage { Header = new FeedHeader { GtfsRealtimeVersion = "2.0" } };
        if (updates.Length > 0)
        {
            var tu = new TripUpdate { Trip = new TripDescriptor { TripId = "T1", StartDate = "20260101" } };
            tu.StopTimeUpdate.AddRange(updates);
            message.Entity.Add(new FeedEntity { Id = "1", TripUpdate = tu });
        }
        using var w = new RawArchiveWriter(_dir, "tu", _ => { });
        w.Append(Actual.AddSeconds(-secondsBefore), message.ToByteArray());
    }

    static StopTimeUpdate Departs(int seq, double secondsAfterActual) =>
        new() { StopSequence = (uint)seq, Departure = new TripUpdate.Types.StopTimeEvent { Time = Actual.AddSeconds(secondsAfterActual).ToUnixTimeSeconds() } };

    [Fact]
    public void TripUpdatesUseOnlyTheLatestSnapshotAtEachHorizon()
    {
        // Every 10 s from 35 min out; prediction drifts from +300 s late to +30 s as the bus approaches.
        for (var s = 2100; s >= 0; s -= 10)
        {
            if (s is > 1080 and < 1300) continue;   // ~18 to 22 minutes out: the archive has a gap
            if (s is >= 540 and <= 660) Snapshot(s); // 9 to 11 minutes out: the trip is missing from the feed
            else Snapshot(s, Departs(2, 30 + s / 7.0), new StopTimeUpdate { StopSequence = 3, ScheduleRelationship = StopTimeUpdate.Types.ScheduleRelationship.Skipped });
        }

        var targets = new[] { new BacktestTarget("20260101", "T1", 2, "B", "R", Actual), new BacktestTarget("20260101", "T1", 3, "X", "R", Actual) };
        var table = new PredictionTable(2);
        new TripUpdatesPredictor(Path.Combine(_dir, "tu")).Fill(targets, table);

        double Err(int target, int minutes) => table[target, H(minutes)] - Actual.ToUnixTimeSeconds();
        Assert.Equal(30 + 120 / 7.0, Err(0, 2), 1.0); // GTFS-rt times are whole seconds
        Assert.Equal(30 + 300 / 7.0, Err(0, 5), 1.0); // GTFS-rt times are whole seconds
        Assert.True(double.IsNaN(Err(0, 10)), "trip absent from the latest snapshot");
        Assert.Equal(30 + 900 / 7.0, Err(0, 15), 1.0); // GTFS-rt times are whole seconds
        Assert.True(double.IsNaN(Err(0, 20)), "archive gap: latest snapshot >60 s old");
        Assert.Equal(30 + 1800 / 7.0, Err(0, 30), 1.0); // GTFS-rt times are whole seconds
        Assert.All(Enumerable.Range(0, Horizons.Minutes.Length), h => Assert.True(double.IsNaN(table[1, h]), "skipped stop"));
    }

    [Fact]
    public void ScoresErrorsCoverageAndEarlyDepartures()
    {
        var targets = Enumerable.Range(0, 5).Select(i => new BacktestTarget("d", "t" + i, 1, "s", "R", Actual)).ToList();
        var table = new PredictionTable(5);
        double[] errors = [-30, 0, 45, 90, double.NaN];
        for (var i = 0; i < 5; i++) table[i, 0] = Actual.ToUnixTimeSeconds() + errors[i];

        var s = Scorer.Compute("p", targets, table, _ => "all").First(s => s.HorizonMinutes == Horizons.Minutes[0]);

        Assert.Equal((5, 4, 0.8), (s.Targets, s.Predicted, s.Coverage));
        Assert.Equal(45, s.MedianAbsError);
        Assert.Equal(26.25, s.Bias);
        Assert.Equal((0.75, 1.0, 0.25), (s.Within1Min, s.Within2Min, s.EarlyRate));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(119, 0)]
    [InlineData(120, 1)]
    [InlineData(899, 3)]
    [InlineData(1799, 5)]
    [InlineData(5000, 6)]
    public void BinsLeadTimes(double leadSeconds, int bin) => Assert.Equal(bin, MetroBiasCorrection.Bin(leadSeconds));

    [Fact]
    public void CorrectionUsesRouteBinsWithEnoughSamplesElseAllRoutes()
    {
        // Route A: 60 samples at ~10 min lead, buses 90 s later than predicted. Route B: 30 samples, 30 s later.
        var samples = Enumerable.Repeat(("A", 600.0, 90.0), 60).Concat(Enumerable.Repeat(("B", 600.0, 30.0), 30)).ToList();

        var c = MetroBiasCorrection.Fit(samples);

        Assert.Equal(90, c.Correction("A", 610));
        Assert.Equal(90, c.Correction("B", 610));     // too few of its own; all-routes median (of 60x90, 30x30) is 90
        Assert.Equal(0, c.Correction("A", 30));       // no training data at that lead time
        Assert.Equal(1, c.RoutesWithOwnCorrection);
    }

    [Fact]
    public void AppliesCorrectionByLeadTimeAtTheMomentOfReading()
    {
        var samples = Enumerable.Repeat(("A", 500.0, 60.0), 60).Concat(Enumerable.Repeat(("A", 60.0, 5.0), 60)); // 5-10 and 0-2 min bands
        var c = MetroBiasCorrection.Fit(samples);
        var targets = new[] { new BacktestTarget("d", "t", 1, "s", "A", Actual) };
        var metro = new PredictionTable(1);
        var actual = Actual.ToUnixTimeSeconds();
        metro[0, H(10)] = actual - 50; // read 10 min before departure, predicting 9 min 10 s ahead: 5-10 min band
        metro[0, H(2)] = actual - 10;  // read 2 min before, predicting 1 min 50 s ahead: 0-2 min band

        var corrected = c.Apply(targets, metro);

        Assert.Equal(actual + 10, corrected[0, H(10)]);
        Assert.Equal(actual - 5, corrected[0, H(2)]);
        Assert.True(double.IsNaN(corrected[0, H(5)]));
    }

    [Fact]
    public void StoreCachesPredictionsPerDay()
    {
        using var store = new BacktestStore(Path.Combine(_dir, "bt.db"));
        var targets = new[] { new BacktestTarget("20260101", "T1", 2, "B", "R", Actual), new BacktestTarget("20260101", "T1", 3, "C", "R", Actual) };
        var table = new PredictionTable(2);
        table[0, 1] = 1234.5;

        Assert.False(store.TryLoad("p", "20260101", targets, [0, 1], new PredictionTable(2)));
        store.Save("p", "20260101", targets, [0, 1], table);
        var loaded = new PredictionTable(2);

        Assert.True(store.TryLoad("p", "20260101", targets, [0, 1], loaded));
        Assert.Equal(1234.5, loaded[0, 1]);
        Assert.True(double.IsNaN(loaded[1, 1]));
        Assert.False(store.TryLoad("p", "20260102", targets, [0, 1], loaded));
    }

    [Fact]
    public void EarlyRateCapLimitsTheShift()
    {
        // Residuals spread evenly from -60 to +240 s (median +90). Shifting by 90 would make the 30% of buses
        // with residual below +30 leave more than 60 s early; a 5% cap allows a shift of at most q05 + 60 = 15 s.
        var samples = Enumerable.Range(0, 301).Select(r => ("A", 600.0, r - 60.0)).ToList();

        Assert.Equal(90, MetroBiasCorrection.Fit(samples).Correction("A", 600), 1e-9);
        Assert.Equal(15, MetroBiasCorrection.Fit(samples, maxEarlyRate: 0.05).Correction("A", 600), 1e-9);
    }
}
