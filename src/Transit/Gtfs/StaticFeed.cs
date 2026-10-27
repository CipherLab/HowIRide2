using System.Globalization;
using System.IO.Compression;
using Transit.Crossings;
using Transit.Geo;

namespace Transit.Gtfs;

public sealed record StopPoint(string StopId, int Sequence, double Dist);

/// <summary>
/// A distinct (shape, stop list) combination. Thousands of trips share a few hundred patterns,
/// so geometry and checkpoints are built once per pattern rather than per trip.
/// </summary>
public sealed class Pattern(string shapeId, Polyline shape, StopPoint[] stops)
{
    public string ShapeId { get; } = shapeId;
    public Polyline Shape { get; } = shape;
    public StopPoint[] Stops { get; } = stops;
    public Checkpoint[] Checkpoints { get; } = CheckpointBuilder.Build(stops);
}

/// <param name="FeedVersion">Which static GTFS file this trip came from, recorded with each crossing.</param>
public sealed record TripInfo(string TripId, string RouteId, int DirectionId, Pattern Pattern, string FeedVersion = "")
{
    public string Headsign { get; init; } = "";

    /// <summary>Scheduled departure at each of Pattern.Stops, in seconds after the service day's origin (noon minus 12 h, local); -1 if absent.</summary>
    public int[] ScheduledDepartures { get; init; } = [];
}

public sealed record StopInfo(string Id, string Name, double Lat, double Lon);

public sealed record RouteInfo(string Id, string ShortName, string LongName, string Color, string TextColor, int Type);

/// <summary>A shape in WGS84 with its distance coordinate, for clients that place vehicles by distance along it.</summary>
public sealed record ShapeGeometry(string Id, double[] Lat, double[] Lon, double[] Dist);

/// <summary>A trip plus the projection its pattern's geometry is in, as needed to match pings against it.</summary>
public readonly record struct ResolvedTrip(TripInfo Trip, LocalProjection Projection);

/// <summary>The parts of a static GTFS feed that crossing detection needs.</summary>
public sealed class StaticFeed
{
    StaticFeed(string version, LocalProjection projection, Dictionary<string, TripInfo> trips, int patternCount,
        Dictionary<string, RouteInfo> routes, TimeZoneInfo timeZone, Dictionary<string, StopInfo> stops, Dictionary<string, ShapeGeometry> shapes)
    {
        Version = version;
        Routes = routes;
        RouteTypes = routes.ToDictionary(kv => kv.Key, kv => kv.Value.Type);
        Stops = stops;
        Shapes = shapes;
        TimeZone = timeZone;
        Projection = projection;
        Trips = trips;
        PatternCount = patternCount;
    }

    public string Version { get; }
    public LocalProjection Projection { get; }
    public IReadOnlyDictionary<string, TripInfo> Trips { get; }
    public int PatternCount { get; }

    /// <summary>GTFS route_type by route_id: 0 light rail, 2 commuter rail, 3 bus.</summary>
    public IReadOnlyDictionary<string, int> RouteTypes { get; }

    public IReadOnlyDictionary<string, RouteInfo> Routes { get; }
    public IReadOnlyDictionary<string, StopInfo> Stops { get; }
    public IReadOnlyDictionary<string, ShapeGeometry> Shapes { get; }

    /// <summary>The agency time zone that GTFS clock times are in.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// The instant GTFS times on a service day count from: noon minus 12 hours, local time. Equals midnight
    /// except on DST change days, and lets times like 25:10:00 run past midnight.
    /// </summary>
    public static DateTimeOffset ServiceDayOrigin(string serviceDate, TimeZoneInfo zone)
    {
        var day = DateTime.ParseExact(serviceDate, "yyyyMMdd", CultureInfo.InvariantCulture);
        var noon = DateTime.SpecifyKind(day.AddHours(12), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(noon, zone)).AddHours(-12);
    }

    public ResolvedTrip? Resolve(string tripId) =>
        Trips.TryGetValue(tripId, out var trip) ? new ResolvedTrip(trip, Projection) : null;

    /// <param name="version">Recorded with each crossing; defaults to the file name.</param>
    public static StaticFeed Load(string zipPath, string? version = null)
    {
        version ??= Path.GetFileNameWithoutExtension(zipPath);
        using var zip = ZipFile.OpenRead(zipPath);
        IEnumerable<CsvRow> Table(string name, bool required = true)
        {
            var entry = zip.GetEntry(name);
            if (entry is null && !required) yield break;
            if (entry is null) throw new InvalidDataException($"{zipPath} has no {name}");
            using var reader = new StreamReader(entry.Open());
            foreach (var row in Csv.Read(reader))
                yield return row;
        }

        var shapePoints = new Dictionary<string, List<(int Seq, double Lat, double Lon, double Dist)>>();
        foreach (var r in Table("shapes.txt"))
        {
            if (!shapePoints.TryGetValue(r["shape_id"], out var list))
                shapePoints[r["shape_id"]] = list = [];
            list.Add((int.Parse(r["shape_pt_sequence"]), D(r["shape_pt_lat"]), D(r["shape_pt_lon"]), ParseOrNaN(r["shape_dist_traveled"])));
        }

        var all = shapePoints.Values.SelectMany(p => p).ToList();
        var projection = new LocalProjection(all.Average(p => p.Lat), all.Average(p => p.Lon));
        var shapes = shapePoints.ToDictionary(kv => kv.Key, kv => BuildPolyline(kv.Value, projection));
        // BuildPolyline sorted each point list; its distances are reused so the client and matcher agree.
        var geometry = shapePoints.ToDictionary(kv => kv.Key, kv => new ShapeGeometry(kv.Key,
            kv.Value.Select(p => p.Lat).ToArray(), kv.Value.Select(p => p.Lon).ToArray(), shapes[kv.Key].Distances));

        var stopRows = Table("stops.txt").Where(r => r["stop_lat"].Length > 0).ToList();
        var stopInfo = stopRows.ToDictionary(r => r["stop_id"], r => new StopInfo(r["stop_id"], r["stop_name"], D(r["stop_lat"]), D(r["stop_lon"])));
        var stopCoords = stopInfo.ToDictionary(kv => kv.Key, kv => projection.Project(kv.Value.Lat, kv.Value.Lon));

        var tripRows = Table("trips.txt").ToDictionary(r => r["trip_id"], r => (Route: r["route_id"], Direction: int.TryParse(r["direction_id"], out var d) ? d : 0, Shape: r["shape_id"], Headsign: r["trip_headsign"]));

        var stopTimes = new Dictionary<string, List<(int Seq, string StopId, double Dist, int Departure)>>();
        foreach (var r in Table("stop_times.txt"))
        {
            if (!stopTimes.TryGetValue(r["trip_id"], out var list))
                stopTimes[r["trip_id"]] = list = [];
            var departure = ParseClock(r["departure_time"]) ?? ParseClock(r["arrival_time"]) ?? -1;
            list.Add((int.Parse(r["stop_sequence"]), r["stop_id"], ParseOrNaN(r["shape_dist_traveled"]), departure));
        }

        // Rapid lines have no short name ("METRO Orange Line"); badges and map labels need "Orange".
        static string Short(string longName) => longName.Replace("METRO ", "").Replace(" Line", "").Replace(" Commuter Rail", "");
        var routes = Table("routes.txt", required: false).ToDictionary(r => r["route_id"], r => new RouteInfo(r["route_id"],
            r["route_short_name"].Length > 0 ? r["route_short_name"] : Short(r["route_long_name"]), r["route_long_name"],
            r["route_color"].Length > 0 ? r["route_color"] : "4F5B66", r["route_text_color"].Length > 0 ? r["route_text_color"] : "FFFFFF",
            int.TryParse(r["route_type"], out var t) ? t : 3));
        var shapesByRoute = tripRows.Values.GroupBy(tr => tr.Route)
            .ToDictionary(g => g.Key, g => g.Select(tr => tr.Shape).Distinct().Where(geometry.ContainsKey).Select(id => geometry[id]));
        routes = RouteColors.Assign(routes, shapesByRoute);
        var zoneId = Table("agency.txt", required: false).Select(r => r["agency_timezone"]).FirstOrDefault(z => z.Length > 0) ?? "UTC";
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);

        var patterns = new Dictionary<string, Pattern>();
        var trips = new Dictionary<string, TripInfo>();
        foreach (var (tripId, times) in stopTimes)
        {
            if (!tripRows.TryGetValue(tripId, out var trip) || !shapes.TryGetValue(trip.Shape, out var shape))
                continue;
            times.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            var stops = ResolveStopDistances(times, shape, stopCoords);
            var key = trip.Shape + "|" + string.Join(";", stops.Select(s => $"{s.StopId}@{s.Dist:F1}"));
            if (!patterns.TryGetValue(key, out var pattern))
                patterns[key] = pattern = new Pattern(trip.Shape, shape, stops);
            trips[tripId] = new TripInfo(tripId, trip.Route, trip.Direction, pattern, version)
            {
                ScheduledDepartures = times.Select(t => t.Departure).ToArray(),
                Headsign = trip.Headsign,
            };
        }
        return new StaticFeed(version, projection, trips, patterns.Count, routes, timeZone, stopInfo, geometry);
    }

    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    static double ParseOrNaN(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>Parses GTFS "H:MM:SS" (hours may exceed 23) to seconds, or null if empty or malformed.</summary>
    public static int? ParseClock(string s)
    {
        var parts = s.Trim().Split(':');
        return parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var sec)
            ? h * 3600 + m * 60 + sec
            : null;
    }

    static Polyline BuildPolyline(List<(int Seq, double Lat, double Lon, double Dist)> points, LocalProjection projection)
    {
        points.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        var x = new double[points.Count];
        var y = new double[points.Count];
        var dist = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            (x[i], y[i]) = projection.Project(points[i].Lat, points[i].Lon);
            // Fall back to measured length if the feed omits shape_dist_traveled.
            dist[i] = !double.IsNaN(points[i].Dist) ? points[i].Dist
                : i == 0 ? 0 : dist[i - 1] + Math.Sqrt(Math.Pow(x[i] - x[i - 1], 2) + Math.Pow(y[i] - y[i - 1], 2));
        }
        return new Polyline(x, y, dist);
    }

    /// <summary>Uses stop_times shape_dist_traveled when present; otherwise projects each stop onto the shape, never behind the previous stop.</summary>
    static StopPoint[] ResolveStopDistances(List<(int Seq, string StopId, double Dist, int Departure)> times, Polyline shape, Dictionary<string, (double X, double Y)> stopCoords)
    {
        var result = new StopPoint[times.Count];
        var previous = 0.0;
        for (var i = 0; i < times.Count; i++)
        {
            var (seq, stopId, dist, _) = times[i];
            if (double.IsNaN(dist))
                dist = stopCoords.TryGetValue(stopId, out var p) && shape.Match(p.X, p.Y, previous) is { } m ? m.Distance : previous;
            dist = Math.Max(dist, previous);
            result[i] = new StopPoint(stopId, seq, dist);
            previous = dist;
        }
        return result;
    }
}
