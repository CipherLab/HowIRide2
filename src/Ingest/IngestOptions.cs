namespace Ingest;

public sealed class IngestOptions
{
    public const string Section = "Ingest";

    /// <summary>Root directory for everything recorded. Raw feeds land in {DataDir}/raw, static GTFS in {DataDir}/static.</summary>
    public string DataDir { get; set; } = "data";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan StatsInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Snapshots per feed held in memory while the disk is slow. 120 covers 20 min of 10 s updates (~110 MB for tripupdates).</summary>
    public int WriteQueueCapacity { get; set; } = 120;

    public TimeSpan StaticCheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Feeds whose header timestamp lags wall clock by more than this are logged as stale (still archived).</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(120);

    public List<FeedSource> Realtime { get; set; } = [];

    public List<FeedSource> Static { get; set; } = [];
}

/// <summary>A named feed URL. Name doubles as the on-disk directory, so keep it path-safe (e.g. "metro-vehicles").</summary>
public sealed class FeedSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}
