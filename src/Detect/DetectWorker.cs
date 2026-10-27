using Transit.Gtfs;
using Transit.Pipeline;
using Transit.Storage;

namespace Detect;

public sealed class DetectOptions
{
    public const string Section = "Detect";

    /// <summary>Ingest's DataDir; only read from.</summary>
    public string DataDir { get; set; } = "data";

    public string DbPath { get; set; } = "derived/crossings.db";

    /// <summary>How often to check the live hour file for new snapshots. Ingest writes one about every 10 s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan StatsInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Runs <see cref="DetectionPipeline"/> and logs its health. A pipeline failure stops the host so the container restarts and resumes from the cursor.</summary>
public sealed class DetectWorker(DetectOptions options, TimeProvider clock, ILogger<DetectWorker> log, ILoggerFactory loggers) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var staticDir = Path.Combine(options.DataDir, "static");
        var feeds = new FeedSet(Path.Combine(staticDir, "metro-gtfs"), Path.Combine(staticDir, "metro-gtfs-next"));
        feeds.Refresh();
        log.LogInformation("Static GTFS: {Versions}", string.Join(", ", feeds.Feeds.Select(f => $"{f.Version} ({f.Trips.Count:N0} trips)")));

        using var store = new CrossingStore(options.DbPath);
        log.LogInformation("Crossing store {Path}: {Rows:N0} rows", options.DbPath, store.Count());

        var pipeline = new DetectionPipeline(options.DataDir, store, feeds, clock, loggers.CreateLogger<DetectionPipeline>(), options.PollInterval);
        using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var stats = LogStats(pipeline, statsCts.Token);
        try
        {
            await pipeline.RunAsync(stoppingToken);
        }
        finally
        {
            statsCts.Cancel();
            await stats.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    async Task LogStats(DetectionPipeline pipeline, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.StatsInterval, clock);
        var (snapshots, crossings) = (0L, 0L);
        var (used, offRoute, ambiguous, lost, unknown) = (0L, 0L, 0L, 0L, 0L);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var s = pipeline.Stats;
            var lag = pipeline.LastFetchedAt is { } last ? clock.GetUtcNow() - last : (TimeSpan?)null;
            log.LogInformation(
                "{Snapshots} snapshots, {Crossings} crossings written; lag {Lag}; {Active} active trips | pings used {Used}, off-route {OffRoute}, ambiguous {Ambiguous}, lost {Lost}, unknown trip {Unknown}",
                pipeline.SnapshotsWritten - snapshots, pipeline.CrossingsWritten - crossings, lag?.ToString(@"hh\:mm\:ss") ?? "-", pipeline.ActiveTrips,
                s.Used - used, s.OffRoute - offRoute, s.Ambiguous - ambiguous, s.Lost - lost, s.UnknownTrip - unknown);
            (snapshots, crossings) = (pipeline.SnapshotsWritten, pipeline.CrossingsWritten);
            (used, offRoute, ambiguous, lost, unknown) = (s.Used, s.OffRoute, s.Ambiguous, s.Lost, s.UnknownTrip);
        }
    }
}
