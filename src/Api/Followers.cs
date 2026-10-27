using Transit.Archive;
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
                var stats = SegmentStats.Build(history, new ServiceCalendar(state.Zone), (_, end) => end < now);
                state.SetStats(stats);
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
