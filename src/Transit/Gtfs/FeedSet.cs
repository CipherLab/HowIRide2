namespace Transit.Gtfs;

/// <summary>
/// The newest static GTFS from each archive directory, consulted in order. Loading both the current
/// feed and next/gtfs.zip means trips from an upcoming pick resolve as soon as it's published,
/// instead of waiting for the main feed to be replaced.
/// </summary>
public sealed class FeedSet(params string[] directories)
{
    volatile IReadOnlyList<StaticFeed> _feeds = [];
    string _signature = "";

    public IReadOnlyList<StaticFeed> Feeds => _feeds;

    /// <summary>Loads whatever changed since the last call. Returns true if the set of feeds is different.</summary>
    public bool Refresh()
    {
        var newest = directories
            .Select(dir => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.zip").Order(StringComparer.Ordinal).LastOrDefault() : null)
            .OfType<string>()
            // Between picks next/gtfs.zip is the same file as gtfs.zip; load it once.
            .DistinctBy(path => (Path.GetFileName(path), new FileInfo(path).Length))
            .ToList();

        var signature = string.Join("|", newest);
        if (signature == _signature) return false;

        var loaded = _feeds.ToDictionary(f => f.Version);
        _feeds = newest.Select(path => loaded.GetValueOrDefault(VersionOf(path)) ?? StaticFeed.Load(path, VersionOf(path))).ToList();
        _signature = signature;
        return true;
    }

    public ResolvedTrip? Resolve(string tripId)
    {
        foreach (var feed in _feeds)
            if (feed.Resolve(tripId) is { } resolved)
                return resolved;
        return null;
    }

    /// <summary>"metro-gtfs/20261022T170534Z": directory plus Last-Modified, unique across the archive.</summary>
    static string VersionOf(string path) =>
        $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileNameWithoutExtension(path)}";
}
