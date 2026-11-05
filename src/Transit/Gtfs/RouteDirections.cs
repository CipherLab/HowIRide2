namespace Transit.Gtfs;

/// <param name="Label">The direction's most common headsign, or its last stop's name when trips have none.</param>
/// <param name="Stops">Stops a rider could board at, in route order, merged across the direction's patterns.</param>
public sealed record RouteDirection(int DirectionId, string Label, StopInfo[] Stops);

/// <param name="ShapeId">The shape of the most common pattern reaching this stop.</param>
public sealed record StopLine(string RouteId, int DirectionId, string Label, string ShapeId);

/// <summary>What the static feeds say about each route's directions, for picking a stop to watch.</summary>
public static class RouteDirections
{
    public static IReadOnlyDictionary<string, RouteDirection[]> Build(IEnumerable<StaticFeed> feeds) =>
        Directions(feeds).GroupBy(d => d.RouteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.Direction.DirectionId).Select(d => d.Direction).ToArray());

    public static IReadOnlyDictionary<string, StopLine[]> LinesByStop(IEnumerable<StaticFeed> feeds) =>
        Directions(feeds)
            .SelectMany(d => d.Direction.Stops.Select(stop => (stop.Id, Line: new StopLine(d.RouteId, d.Direction.DirectionId, d.Direction.Label, d.ShapeOf(stop.Id)))))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Line).ToArray());

    sealed record Merged(string RouteId, RouteDirection Direction, Func<string, string> ShapeOf);

    static List<Merged> Directions(IEnumerable<StaticFeed> feeds)
    {
        var all = feeds.ToList();
        StopInfo? Stop(string id) => all.Select(f => f.Stops.GetValueOrDefault(id)).FirstOrDefault(s => s is not null);

        return all.SelectMany(f => f.Trips.Values)
            .GroupBy(t => (t.RouteId, t.DirectionId))
            .Select(group =>
            {
                // Most trips first, so the common pattern sets the order and the shape.
                var patterns = group.GroupBy(t => t.Pattern).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                var stopIds = MergeStops(patterns);
                // Departures skip a trip's first stop, so a stop only ever seen first can't be watched.
                var boardable = stopIds.Where(id => patterns.Any(p => ReachedLater(p, id))).ToList();
                var label = group.GroupBy(t => t.Headsign).Where(g => g.Key.Length > 0)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key
                    ?? Stop(patterns[0].Stops[^1].StopId)?.Name
                    ?? "";
                var stops = boardable.Select(Stop).OfType<StopInfo>().ToArray();
                return new Merged(group.Key.RouteId, new RouteDirection(group.Key.DirectionId, label, stops),
                    stopId => patterns.First(p => ReachedLater(p, stopId)).ShapeId);
            })
            .ToList();
    }

    static bool ReachedLater(Pattern pattern, string stopId) => pattern.Stops.Skip(1).Any(s => s.StopId == stopId);

    /// <summary>Starts from the first pattern; each other pattern's new stops go right after their predecessor in it.</summary>
    static List<string> MergeStops(List<Pattern> patterns)
    {
        var merged = new List<string>();
        foreach (var pattern in patterns)
        {
            var at = -1;
            foreach (var stop in pattern.Stops)
            {
                var existing = merged.IndexOf(stop.StopId);
                if (existing >= 0)
                {
                    at = existing;
                    continue;
                }
                merged.Insert(++at, stop.StopId);
            }
        }
        return merged;
    }
}
