namespace Transit.Gtfs;

/// <summary>
/// Display colours for routes. Metro Transit's feed gives most bus routes one shared brand colour, so on a map
/// they can't be told apart. Lines named for a colour (Blue, Green, Gold, Orange, Red) keep the feed's colour;
/// every other route gets one from <see cref="Palette"/>, chosen so routes that share streets differ.
/// </summary>
public static class RouteColors
{
    /// <summary>Hues kept clear of the named lines', all readable against a pale basemap.</summary>
    public static readonly string[] Palette =
    [
        "7B2D8E", // purple
        "0E9AA7", // teal
        "D6338A", // magenta
        "8C5A2B", // brown
        "4B4FC2", // indigo
        "6E7F1F", // olive
        "4FB3E8", // sky
        "2B3A4A", // slate
        "EE88BE", // pink
        "9BC53D", // lime
        "8E1B3A", // maroon
        "A68AD9", // lavender
    ];

    static readonly string[] NamedColours = ["Blue", "Green", "Gold", "Orange", "Red"];

    /// <summary>Grid cell size for deciding which routes share streets.</summary>
    const double CellDegrees = 0.004; // ~450 m north-south, ~300 m east-west at 45°N

    public static Dictionary<string, RouteInfo> Assign(Dictionary<string, RouteInfo> routes, IReadOnlyDictionary<string, IEnumerable<ShapeGeometry>> shapesByRoute)
    {
        var result = new Dictionary<string, RouteInfo>(routes);
        var free = routes.Values.Where(r => !IsNamedColour(r)).Select(r => r.Id).ToList();

        var cells = free.ToDictionary(id => id, id => (shapesByRoute.TryGetValue(id, out var shapes) ? shapes : [])
            .SelectMany(s => s.Lat.Zip(s.Lon, (lat, lon) => (Math.Floor(lat / CellDegrees), Math.Floor(lon / CellDegrees))))
            .ToHashSet());
        var routesInCell = new Dictionary<(double, double), List<string>>();
        foreach (var (id, set) in cells)
            foreach (var cell in set)
                (routesInCell.TryGetValue(cell, out var list) ? list : routesInCell[cell] = []).Add(id);

        // Shared cells between each pair of routes: how much they'd be confused if they matched.
        var overlap = free.ToDictionary(id => id, _ => new Dictionary<string, int>());
        foreach (var ids in routesInCell.Values)
            foreach (var a in ids)
                foreach (var b in ids)
                    if (a != b)
                        overlap[a][b] = overlap[a].GetValueOrDefault(b) + 1;

        // Greedy colouring, busiest routes first; each takes the colour it shares the fewest cells with so far.
        // Ties go to the route's number, so a route keeps its colour when the feed changes elsewhere.
        var chosen = new Dictionary<string, int>();
        foreach (var id in free.OrderByDescending(id => overlap[id].Values.Sum()).ThenBy(id => id, StringComparer.Ordinal))
        {
            var cost = new int[Palette.Length];
            foreach (var (other, shared) in overlap[id])
                if (chosen.TryGetValue(other, out var c))
                    cost[c] += shared;
            var start = StableHash(routes[id].ShortName) % Palette.Length;
            var best = Enumerable.Range(0, Palette.Length).Select(i => (start + i) % Palette.Length).MinBy(i => cost[i]);
            chosen[id] = best;
            result[id] = routes[id] with { Color = Palette[best], TextColor = TextOn(Palette[best]) };
        }
        return result;
    }

    public static bool IsNamedColour(RouteInfo route) =>
        NamedColours.Any(c => route.LongName.Contains($"{c} Line", StringComparison.OrdinalIgnoreCase));

    /// <summary>White or ink, whichever reads better on the colour.</summary>
    public static string TextOn(string hex)
    {
        double Channel(int i)
        {
            var v = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
        // Contrast with white (1.05 / (L + 0.05)) against contrast with ink (#14212B, L ≈ 0.015).
        return 1.05 / (luminance + 0.05) >= (luminance + 0.05) / 0.065 ? "FFFFFF" : "14212B";
    }

    static int StableHash(string s)
    {
        var h = 17;
        foreach (var ch in s)
            h = unchecked(h * 31 + ch);
        return h & int.MaxValue;
    }
}
