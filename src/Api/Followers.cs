using Transit.Archive;
using Transit.Backtest;
using Transit.Eta;
using Transit.Gtfs;

namespace Api;

/// <summary>Loads static GTFS before anything else needs it; the followers wait on <see cref="LiveState.Ready"/>.</summary>
public sealed class StaticLoader(LiveState state, ILogger<StaticLoader> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            state.LoadStatic();
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Could not load static GTFS");
            throw;
        }
    }
}

/// <summary>
/// Follows VehiclePositions from 90 minutes back so in-progress trips have full tracking state, publishing to
/// the map only once caught up to live.
/// </summary>
public sealed class VehicleFollower(LiveState state, ApiOptions options, TimeProvider clock) : BackgroundService
{
    static readonly TimeSpan Warmup = TimeSpan.FromMinutes(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.Ready.WaitAsync(stoppingToken);
        var started = clock.GetUtcNow();
        var follower = new ArchiveFollower(Path.Combine(options.DataDir, "raw", "metro-vehicles"), clock, TimeSpan.FromSeconds(1));
        await foreach (var record in follower.Follow(started - Warmup, stoppingToken))
            state.OnVehicleSnapshot(record, live: record.FetchedAt >= started - TimeSpan.FromMinutes(1));
    }
}

/// <summary>Keeps Metro Transit's latest TripUpdates predictions.</summary>
public sealed class TripUpdatesFollower(LiveState state, ApiOptions options, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.Ready.WaitAsync(stoppingToken);
        var follower = new ArchiveFollower(Path.Combine(options.DataDir, "raw", "metro-tripupdates"), clock, TimeSpan.FromSeconds(1));
        await foreach (var record in follower.Follow(clock.GetUtcNow() - TimeSpan.FromMinutes(1), stoppingToken))
            state.OnTripUpdates(record);
    }
}

/// <summary>Rebuilds the v0 median tables from the detect store every hour.</summary>
public sealed class ModelRefresher(LiveState state, ApiOptions options, TimeProvider clock, ILogger<ModelRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.Ready.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                if (!File.Exists(options.DbPath))
                {
                    log.LogWarning("No crossings store at {Path}; v0/v1 unavailable until detect creates it", options.DbPath);
                    continue;
                }
                var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), state.Zone).Date;
                var history = await Task.Run(() => CrossingHistory.Load(options.DbPath,
                    today.AddDays(-options.HistoryDays).ToString("yyyyMMdd"), today.ToString("yyyyMMdd")), stoppingToken);
                var now = clock.GetUtcNow().ToUnixTimeSeconds();
                var stats = SegmentStats.Build(history, new ServiceCalendar(state.Zone), (_, end) => end < now,
                    scheduledStart: SegmentStats.ScheduledStarts(state.ResolveTrip, state.Zone));
                state.SetStats(stats);
                var margins = BeByMargins.TryLoad(options.BeByMarginsPath);
                state.SetMargins(margins);
                if (margins is null) log.LogInformation("No be-there-by margins yet; the app won't offer that time");
                else log.LogInformation("Be-there-by margins from {From}-{To}, {Samples:N0} views", margins.From, margins.To, margins.Samples);
                log.LogInformation("Model tables rebuilt: {Segments:N0} segments, {Stops:N0} stops from {Trips:N0} trips",
                    stats.TravelTable.Keys, stats.DwellTable.Keys, history.Trips);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Model refresh failed; keeping the previous tables");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Archives what v1 is predicting once a minute, so tomorrow's backtest can score the numbers riders actually saw
/// rather than a replay of them. Losing a sample costs coverage and nothing else, so failures are logged and survived.
/// </summary>
public sealed class PredictionLogger(LiveState state, ApiOptions options, TimeProvider clock, ILogger<PredictionLogger> log) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Past the longest horizon the backtest scores (30 min), with room for predictions that were optimistic.</summary>
    static readonly double WindowSeconds = TimeSpan.FromMinutes(45).TotalSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.Ready.WaitAsync(stoppingToken);
        var directory = Path.GetFullPath(options.PredictionLogDir);
        using var writer = new RawArchiveWriter(Path.GetDirectoryName(directory)!, Path.GetFileName(directory));
        foreach (var leftover in writer.FindUncompressed(clock.GetUtcNow()).ToList())
            _ = Task.Run(() => RawArchiveWriter.Compress(leftover), stoppingToken);

        var (samples, stops) = (0, 0L);
        var slowest = TimeSpan.Zero;
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var started = clock.GetUtcNow();
                var sample = await Task.Run(() => state.SamplePredictions(WindowSeconds), stoppingToken);
                if (sample is null) continue;
                writer.Append(clock.GetUtcNow(), LivePredictionLog.Encode(sample));
                samples++;
                stops += sample.Sum(t => t.Stops.Count);
                if (clock.GetUtcNow() - started > slowest) slowest = clock.GetUtcNow() - started;
                if (samples % 60 == 1)
                {
                    log.LogInformation("Logged {Samples} prediction samples, {Stops:N0} stops per sample on average; slowest {Slowest:F1} s",
                        samples, stops / samples, slowest.TotalSeconds);
                    slowest = TimeSpan.Zero;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Could not log a prediction sample");
            }
        }
    }
}
