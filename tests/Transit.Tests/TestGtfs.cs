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
}
