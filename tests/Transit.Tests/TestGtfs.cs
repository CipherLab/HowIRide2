using System.IO.Compression;

namespace Transit.Tests;

/// <summary>
/// A tiny static GTFS feed: one east-west shape of 11 points ~78.6 m apart at 45° N, stops A, B, C at
/// points 0, 2 and 10 (B to C is 629 m, long enough for one synthetic checkpoint), trips T1 and T2 on it.
/// </summary>
static class TestGtfs
{
    public const double Lat = 45.0;
    public const double LonStep = 0.001;

    public static double Lon(int point) => -93.0 + point * LonStep;

    public static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Table(string name, IEnumerable<string> lines)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            foreach (var line in lines) w.WriteLine(line);
        }

        double Dist(int i) => i * 78.6;
        Table("shapes.txt", ["shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence,shape_dist_traveled",
            .. Enumerable.Range(0, 11).Select(i => $"S1,{Lat},{Lon(i)},{i + 1},{Dist(i):F1}")]);
        Table("stops.txt", ["stop_id,stop_name,stop_lat,stop_lon",
            $"A,\"West End, Farside\",{Lat},{Lon(0)}", $"B,Middle,{Lat},{Lon(2)}", $"C,East,{Lat},{Lon(10)}"]);
        Table("agency.txt", ["agency_id,agency_name,agency_url,agency_timezone", "0,Test,https://example.com,America/Chicago"]);
        Table("routes.txt", ["route_id,agency_id,route_short_name,route_type", "R,0,R,3"]);
        Table("trips.txt", ["route_id,service_id,trip_id,direction_id,shape_id", "R,1,T1,0,S1", "R,1,T2,0,S1"]);
        Table("stop_times.txt", ["trip_id,arrival_time,departure_time,stop_id,stop_sequence,shape_dist_traveled",
            .. new[] { "T1", "T2" }.SelectMany(t => new[] { $"{t},08:00:00,08:00:00,A,1,0", $"{t},08:05:00,08:05:00,B,2,{Dist(2):F1}", $"{t},08:10:00,08:10:00,C,3,{Dist(10):F1}" })]);
        return path;
    }

    /// <summary>
    /// Stop B served by route R in both directions (R0a, R0b east, R1a west on a reversed shape), by 25 trips of route
    /// OTHER, and twice per trip by route LOOP (LOOP1 runs A, B, C, then back to B on an out-and-back shape).
    /// </summary>
    public static string WriteBusyStop(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Table(string name, IEnumerable<string> lines)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            foreach (var line in lines) w.WriteLine(line);
        }

        const double step = 78.6;
        string[] Shape(string id, IEnumerable<int> points) =>
            points.Select((point, i) => $"{id},{Lat},{Lon(point)},{i + 1},{i * step:F1}").ToArray();
        var east = Enumerable.Range(0, 11).ToArray();
        var west = east.Reverse().ToArray();
        var loop = east.Concat(Enumerable.Range(2, 8).Reverse()).ToArray();
        Table("shapes.txt", ["shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence,shape_dist_traveled",
            .. Shape("EAST", east), .. Shape("WEST", west), .. Shape("LOOP", loop)]);
        Table("stops.txt", ["stop_id,stop_name,stop_lat,stop_lon", $"A,West End,{Lat},{Lon(0)}", $"B,Middle,{Lat},{Lon(2)}", $"C,East,{Lat},{Lon(10)}", $"Z,Middle of nowhere,{Lat},{Lon(5)}"]);
        Table("agency.txt", ["agency_id,agency_name,agency_url,agency_timezone", "0,Test,https://example.com,America/Chicago"]);
        Table("routes.txt", ["route_id,agency_id,route_short_name,route_type", "R,0,R,3", "OTHER,0,O,3", "LOOP,0,L,3"]);

        var others = Enumerable.Range(0, 25).Select(i => $"O{i}").ToArray();
        Table("trips.txt", ["route_id,service_id,trip_id,direction_id,shape_id,trip_headsign",
            "R,1,R0a,0,EAST,East", "R,1,R0b,0,EAST,East", "R,1,R1a,1,WEST,West", "LOOP,1,LOOP1,0,LOOP,Loop",
            .. others.Select(t => $"OTHER,1,{t},0,EAST,Other")]);
        string[] Times(string trip, params (string Stop, double Dist)[] stops) =>
            stops.Select((s, i) => $"{trip},08:{i:00}:00,08:{i:00}:00,{s.Stop},{i + 1},{s.Dist:F1}").ToArray();
        (string, double)[] eastStops = [("A", 0), ("B", 2 * step), ("C", 10 * step)];
        Table("stop_times.txt", ["trip_id,arrival_time,departure_time,stop_id,stop_sequence,shape_dist_traveled",
            .. new[] { "R0a", "R0b" }.Concat(others).SelectMany(t => Times(t, eastStops)),
            .. Times("R1a", ("C", 0), ("B", 8 * step), ("A", 10 * step)),
            .. Times("LOOP1", ("A", 0), ("B", 2 * step), ("C", 10 * step), ("B", 18 * step))]);
        return path;
    }

    /// <summary>
    /// Route R. Direction 0: three trips A, B, C (headsign East) and a branch A, B, D, C on its own shape (East via D).
    /// Direction 1: C, B, A (West), and a short trip B, A. Route N has no headsigns.
    /// </summary>
    public static string WriteBranches(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Table(string name, IEnumerable<string> lines)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            foreach (var line in lines) w.WriteLine(line);
        }

        const double step = 78.6;
        string[] Shape(string id, IEnumerable<int> points) =>
            points.Select((point, i) => $"{id},{Lat},{Lon(point)},{i + 1},{i * step:F1}").ToArray();
        var east = Enumerable.Range(0, 11).ToArray();
        Table("shapes.txt", ["shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence,shape_dist_traveled",
            .. Shape("EAST", east), .. Shape("BRANCH", east), .. Shape("WEST", east.Reverse())]);
        Table("stops.txt", ["stop_id,stop_name,stop_lat,stop_lon",
            $"A,West End,{Lat},{Lon(0)}", $"B,Middle,{Lat},{Lon(2)}", $"D,Branch,{Lat},{Lon(6)}", $"C,East End,{Lat},{Lon(10)}"]);
        Table("agency.txt", ["agency_id,agency_name,agency_url,agency_timezone", "0,Test,https://example.com,America/Chicago"]);
        Table("routes.txt", ["route_id,agency_id,route_short_name,route_type", "R,0,R,3", "N,0,N,3"]);
        Table("trips.txt", ["route_id,service_id,trip_id,direction_id,shape_id,trip_headsign",
            "R,1,E1,0,EAST,East", "R,1,E2,0,EAST,East", "R,1,E3,0,EAST,East", "R,1,EB,0,BRANCH,East via D",
            "R,1,W1,1,WEST,West", "R,1,W2,1,WEST,West", "R,1,WS,1,WEST,West", "N,1,N1,0,EAST,"]);
        string[] Times(string trip, params (string Stop, int Point)[] stops) =>
            stops.Select((s, i) => $"{trip},08:{i:00}:00,08:{i:00}:00,{s.Stop},{i + 1},{s.Point * step:F1}").ToArray();
        Table("stop_times.txt", ["trip_id,arrival_time,departure_time,stop_id,stop_sequence,shape_dist_traveled",
            .. new[] { "E1", "E2", "E3", "N1" }.SelectMany(t => Times(t, ("A", 0), ("B", 2), ("C", 10))),
            .. Times("EB", ("A", 0), ("B", 2), ("D", 6), ("C", 10)),
            .. new[] { "W1", "W2" }.SelectMany(t => Times(t, ("C", 0), ("B", 8), ("A", 10))),
            .. Times("WS", ("B", 8), ("A", 10))]);
        return path;
    }
}
