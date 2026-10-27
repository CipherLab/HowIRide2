using Microsoft.Extensions.Logging;
using Transit.Archive;
using Transit.Crossings;
using Transit.Gtfs;
using Transit.Realtime;
using Transit.Storage;
using TransitRealtime;

namespace Transit.Pipeline;

/// <summary>
/// Follows the VehiclePositions archive from where it last stopped, runs crossing detection and
/// stores the results. Restarts replay a warm-up window first, writing nothing, so trips already
/// under way when the process stopped are tracked from their start rather than picked up mid-run.
/// </summary>
public sealed class DetectionPipeline(string dataDir, CrossingStore store, FeedSet feeds, TimeProvider clock, ILogger log, TimeSpan pollInterval)
{
    public const string Feed = "metro-vehicles";

    /// <summary>Longer than nearly every trip, so trackers rebuilt from the warm-up have full context.</summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromMinutes(90);

    readonly CrossingDetector _detector = new(feeds.Resolve);

    public DetectorStats Stats => _detector.Stats;
    public int ActiveTrips => _detector.ActiveTrackers;
    public long SnapshotsWritten { get; private set; }
    public long CrossingsWritten { get; private set; }
    public DateTimeOffset? LastFetchedAt { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var feedDir = Path.Combine(dataDir, "raw", Feed);
        var cursor = store.GetCursor(Feed);
        if (cursor is null)
        {
            var earliest = ArchiveFollower.EarliestHour(feedDir);
            cursor = earliest is { } h ? new DateTimeOffset(h).AddTicks(-1) : clock.GetUtcNow();
            log.LogInformation("No cursor; starting from the beginning of the archive at {Start:O}", cursor);
        }
        else
            log.LogInformation("Resuming after {Cursor:O} with a {Warmup} warm-up", cursor, Warmup);

        var follower = new ArchiveFollower(feedDir, clock, pollInterval);
        var crossings = new List<StopCrossing>();
        DateTime? hour = null;
        await foreach (var record in follower.Follow(cursor.Value - Warmup, ct))
        {
            var recordHour = ArchiveFollower.FloorHour(record.FetchedAt);
            if (recordHour != hour)
            {
                hour = recordHour;
                _detector.PruneIdle(record.FetchedAt);
                if (feeds.Refresh())
                    log.LogInformation("Static GTFS now {Versions}", string.Join(", ", feeds.Feeds.Select(f => f.Version)));
            }

            crossings.Clear();
            FeedMessage message;
            try
            {
                message = FeedMessage.Parser.ParseFrom(record.Payload);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException ex)
            {
                log.LogWarning(ex, "Skipping unparseable snapshot fetched at {FetchedAt:O}", record.FetchedAt);
                continue;
            }
            foreach (var ping in VehiclePings.From(message))
                crossings.AddRange(_detector.OnPing(ping, record.FetchedAt));

            LastFetchedAt = record.FetchedAt;
            // Warm-up: these snapshots were stored by the previous run; only the tracker state is wanted.
            if (record.FetchedAt <= cursor) continue;

            store.Write(crossings, Feed, record.FetchedAt);
            SnapshotsWritten++;
            CrossingsWritten += crossings.Count;
        }
    }
}
