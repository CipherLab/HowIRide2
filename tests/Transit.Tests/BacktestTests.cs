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
}
