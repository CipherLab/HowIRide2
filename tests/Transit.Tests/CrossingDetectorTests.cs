using System.IO.Compression;
using Transit.Crossings;
using Transit.Gtfs;

namespace Transit.Tests;

public sealed class CrossingDetectorTests : IDisposable
{
    const double Lat = 45.0;
    const double LonStep = 0.001; // ~78.6 m at this latitude
    static readonly DateTimeOffset T0 = new(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);

    readonly string _dir = Directory.CreateTempSubdirectory("gtfs-").FullName;
    readonly StaticFeed _feed;

    public CrossingDetectorTests() => _feed = StaticFeed.Load(WriteFeed());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>One east-west shape of 11 points; stops at points 0, 2 and 10 (B to C is 629 m, long enough for one synthetic); trips T1 and T2 share it.</summary>
    string WriteFeed()
    {
        var path = Path.Combine(_dir, "gtfs.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Table(string name, IEnumerable<string> lines)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            foreach (var line in lines) w.WriteLine(line);
        }

        double Lon(int i) => -93.0 + i * LonStep;
        double Dist(int i) => i * 78.6;
        Table("shapes.txt", ["shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence,shape_dist_traveled",
            .. Enumerable.Range(0, 11).Select(i => $"S1,{Lat},{Lon(i)},{i + 1},{Dist(i):F1}")]);
        Table("stops.txt", ["stop_id,stop_name,stop_lat,stop_lon",
            $"A,\"West End, Farside\",{Lat},{Lon(0)}", $"B,Middle,{Lat},{Lon(2)}", $"C,East,{Lat},{Lon(10)}"]);
        Table("trips.txt", ["route_id,service_id,trip_id,direction_id,shape_id", "R,1,T1,0,S1", "R,1,T2,0,S1"]);
        Table("stop_times.txt", ["trip_id,arrival_time,departure_time,stop_id,stop_sequence,shape_dist_traveled",
            .. new[] { "T1", "T2" }.SelectMany(t => new[] { $"{t},08:00:00,08:00:00,A,1,0", $"{t},08:05:00,08:05:00,B,2,{Dist(2):F1}", $"{t},08:10:00,08:10:00,C,3,{Dist(10):F1}" })]);
        return path;
    }

    static VehiclePing Ping(string? trip, double seconds, int point) =>
        new("V1", trip, "20261027", T0.AddSeconds(seconds), Lat, -93.0 + point * LonStep, null);

    [Fact]
    public void LoadsTripsSharingOnePattern()
    {
        Assert.Equal(2, _feed.Trips.Count);
        Assert.Equal(1, _feed.PatternCount);
        Assert.Same(_feed.Trips["T1"].Pattern, _feed.Trips["T2"].Pattern);
        Assert.Equal(["A", "B", "B>C#1", "C"], _feed.Trips["T1"].Pattern.Checkpoints.Select(c => c.Id));
    }

    [Fact]
    public void FiltersBadPingsAndStartsNewTrackerOnTripHandoff()
    {
        var detector = new CrossingDetector(_feed);
        var crossings = new List<StopCrossing>();
        void Send(VehiclePing p, double fetchedSeconds) => crossings.AddRange(detector.OnPing(p, T0.AddSeconds(fetchedSeconds)));

        Send(Ping(null, 0, 0), 30);              // no trip
        Send(Ping("T9", 0, 0), 30);              // not in the static feed
        Send(Ping("T1", 0, 0), 200);             // fetched 200 s after it was recorded
        Send(Ping("T1", 10, 0), 40);
        Send(Ping("T1", 10, 0), 50);             // repeated report
        Send(Ping("T1", 70, 6), 100);            // passes A and B
        Send(Ping("T1", 130, 10), 160);          // passes the synthetic and arrives at C
        Send(Ping("T2", 400, 10), 430);          // same bus starts its next trip

        var s = detector.Stats;
        Assert.Equal((1L, 1L, 1L, 1L), (s.NoTrip, s.UnknownTrip, s.Stale, s.Duplicate));
        Assert.Equal(2, s.TripsStarted);
        Assert.Equal(["A", "B", "B>C#1", "C"], crossings.Select(c => c.Checkpoint.Id));
        Assert.All(crossings, c => Assert.Equal(("T1", "V1", "20261027"), (c.TripId, c.VehicleId, c.ServiceDate)));
        Assert.Equal(4, s.Crossings);
    }

    [Fact]
    public void PrunesIdleTrackers()
    {
        var detector = new CrossingDetector(_feed);
        detector.OnPing(Ping("T1", 0, 0), T0.AddSeconds(30));
        Assert.Equal(1, detector.ActiveTrackers);

        detector.PruneIdle(T0 + CrossingDetector.IdleTimeout + TimeSpan.FromSeconds(1));

        Assert.Equal(0, detector.ActiveTrackers);
    }
}
