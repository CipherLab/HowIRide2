namespace Transit.Gtfs;

/// <summary>Finds stops by number or by the words in their name.</summary>
public static class StopSearch
{
    public static StopInfo[] Find(IEnumerable<StopInfo> stops, string query, int limit = 20)
    {
        var words = query.Split(query.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var number = query.Trim();

        int Rank(StopInfo s) =>
            s.Id.Equals(number, StringComparison.OrdinalIgnoreCase) ? 0
            : s.Id.StartsWith(number, StringComparison.OrdinalIgnoreCase) ? 1
            : words.All(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 2
            : 3;

        return stops.Select(s => (Stop: s, Rank: Rank(s)))
            .Where(x => x.Rank < 3)
            .OrderBy(x => x.Rank).ThenBy(x => x.Stop.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Stop)
            .ToArray();
    }
}
