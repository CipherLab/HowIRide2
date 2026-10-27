using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Transit.Archive;
using Transit.Gtfs;
using Transit.Pipeline;
using Transit.Storage;
using TransitRealtime;

namespace Transit.Tests;

public sealed class DetectionPipelineTests : IDisposable
{
    // Two minutes before an hour boundary, so the second run's snapshots straddle it.
    static readonly DateTimeOffset Base = new(2026, 1, 1, 10, 58, 0, TimeSpan.Zero);
    readonly string _data = Directory.CreateTempSubdirectory("pipeline-").FullName;
    string DbPath => Path.Combine(_data, "derived", "crossings.db");

    public DetectionPipelineTests() =>
        TestGtfs.Write(Path.Combine(_data, "static", "metro-gtfs", "20251231T000000Z.zip"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_data, recursive: true);
    }

    /// <summary>Archives a VehiclePositions snapshot of bus V1 on trip T1 at a point along the test shape, fetched 20 s after the ping.</summary>
    void Snapshot(double seconds, int point)
    {
        var ts = Base.AddSeconds(seconds);
        var message = new FeedMessage
        {
            Header = new FeedHeader { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)ts.ToUnixTimeSeconds() },
            Entity =
            {
                new FeedEntity
                {
                    Id = "1",
                    Vehicle = new VehiclePosition
                    {
                        Trip = new TripDescriptor { TripId = "T1", StartDate = "20260101" },
                        Position = new Position { Latitude = (float)TestGtfs.Lat, Longitude = (float)TestGtfs.Lon(point) },
                        Timestamp = (ulong)ts.ToUnixTimeSeconds(),
                        Vehicle = new VehicleDescriptor { Id = "V1" },
                    },
                },
            },
        };
        using var writer = new RawArchiveWriter(Path.Combine(_data, "raw"), DetectionPipeline.Feed, _ => { });
        writer.Append(ts.AddSeconds(20), message.ToByteArray());
    }

    /// <summary>Runs a fresh pipeline (as after a restart) until it has written <paramref name="snapshots"/> snapshots.</summary>
    async Task RunUntil(int snapshots)
    {
        using var store = new CrossingStore(DbPath);
        var feeds = new FeedSet(Path.Combine(_data, "static", "metro-gtfs"));
        var pipeline = new DetectionPipeline(_data, store, feeds, TimeProvider.System, NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = pipeline.RunAsync(cts.Token);
        while (pipeline.SnapshotsWritten < snapshots && !run.IsCompleted)
            await Task.Delay(20);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(snapshots, pipeline.SnapshotsWritten);
    }

    List<(string Checkpoint, double? Arrive, double? Depart)> Rows()
    {
        using var db = new SqliteConnection($"Data Source={DbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT checkpoint_id, arrive, depart FROM crossings ORDER BY checkpoint_index";
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, double?, double?)>();
        while (r.Read())
            rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetDouble(1) - Base.ToUnixTimeSeconds(), r.IsDBNull(2) ? null : r.GetDouble(2) - Base.ToUnixTimeSeconds()));
        return rows;
    }

    [Fact]
    public async Task ResumesAfterRestartWithTripStateRebuiltFromWarmup()
    {
        // First run sees the bus leave A (0 m -> 78.6 m).
        Snapshot(0, 0);
        Snapshot(10, 1);
        await RunUntil(2);
        Assert.Equal(["A"], Rows().Select(r => r.Checkpoint));

        // After a restart the bus passes B and the synthetic, then reaches C in the next hour's file.
        Snapshot(70, 6);
        Snapshot(130, 10);
        await RunUntil(2);

        var rows = Rows();
        Assert.Equal(["A", "B", "B>C#1", "C"], rows.Select(r => r.Checkpoint));
        // B's zone starts at 127.2 m, between the pre-restart ping (10 s, 78.6 m) and the next (70 s, 471.6 m).
        // Without the warm-up the tracker would start at 471.6 m and never record B.
        Assert.Equal(17.42, rows[1].Arrive!.Value, 0.05); // GTFS-rt positions are 32-bit floats
        Assert.Null(rows[3].Depart);
    }

    [Fact]
    public void StoreKeepsOneRowPerTripCheckpointAndTracksCursor()
    {
        using var store = new CrossingStore(DbPath);
        var feed = StaticFeed.Load(Path.Combine(_data, "static", "metro-gtfs", "20251231T000000Z.zip"));
        var cp = feed.Trips["T1"].Pattern.Checkpoints[1];
        var crossing = new Crossings.StopCrossing("20260101", "T1", "R", 0, "V1", cp, Base, Base.AddSeconds(5), 10, 10, false, feed.Trips["T1"]);

        store.Write([crossing], "feed", Base);
        store.Write([crossing with { Depart = Base.AddSeconds(9) }], "feed", Base.AddSeconds(10));

        Assert.Equal(1, store.Count());
        Assert.Equal(Base.AddSeconds(10), store.GetCursor("feed"));
        Assert.Null(store.GetCursor("other"));
        Assert.Equal(9.0, Rows().Single().Depart);
    }
}
