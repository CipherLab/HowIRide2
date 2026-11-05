using Api;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Transit.Archive;
using TransitRealtime;

namespace Transit.Tests;

public sealed class LiveStateDepartureTests : IDisposable
{
    const string ServiceDate = "20260105";
    static readonly DateTimeOffset Noon = new(2026, 1, 5, 18, 0, 0, TimeSpan.Zero);

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    readonly string _dir = Directory.CreateTempSubdirectory("livestate").FullName;
    readonly LiveState _state;

    public LiveStateDepartureTests()
    {
        TestGtfs.WriteBusyStop(Path.Combine(_dir, "static", "metro-gtfs", "20260101T000000Z.zip"));
        _state = new LiveState(new ApiOptions { DataDir = _dir }, new FixedClock(Noon), NullLogger<LiveState>.Instance);
        _state.LoadStatic();
    }

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Metro's estimate for each trip at stop sequence <paramref name="sequence"/>, in minutes from now.</summary>
    void MetroSays(int sequence, params (string Trip, int Minutes)[] trips)
    {
        var message = new FeedMessage { Header = new FeedHeader { GtfsRealtimeVersion = "2.0" } };
        foreach (var (trip, minutes) in trips)
            message.Entity.Add(new FeedEntity
            {
                Id = trip,
                TripUpdate = new TripUpdate
                {
                    Trip = new TripDescriptor { TripId = trip, StartDate = ServiceDate },
                    StopTimeUpdate =
                    {
                        new TripUpdate.Types.StopTimeUpdate
                        {
                            StopSequence = (uint)sequence,
                            Departure = new TripUpdate.Types.StopTimeEvent { Time = Noon.AddMinutes(minutes).ToUnixTimeSeconds() },
                        },
                    },
                },
            });
        _state.OnTripUpdates(new RawRecord(Noon, message.ToByteArray()));
    }

    /// <summary>Everyone at stop B (sequence 2): 25 OTHER trips due first, then route R's trips.</summary>
    void BusyStop()
    {
        var others = Enumerable.Range(0, 25).Select(i => ($"O{i}", 1 + i));
        MetroSays(2, [.. others, ("R0a", 30), ("R0b", 40), ("R1a", 35)]);
    }

    void Ping(string trip, int point, int seconds)
    {
        var at = Noon.AddSeconds(seconds);
        var message = new FeedMessage
        {
            Header = new FeedHeader { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)at.ToUnixTimeSeconds() },
            Entity =
            {
                new FeedEntity
                {
                    Id = "1",
                    Vehicle = new VehiclePosition
                    {
                        Trip = new TripDescriptor { TripId = trip, StartDate = ServiceDate },
                        Position = new Position { Latitude = (float)TestGtfs.Lat, Longitude = (float)TestGtfs.Lon(point) },
                        Timestamp = (ulong)at.ToUnixTimeSeconds(),
                        Vehicle = new VehicleDescriptor { Id = "V1" },
                    },
                },
            },
        };
        _state.OnVehicleSnapshot(new RawRecord(at, message.ToByteArray()), live: true);
    }

    [Fact]
    public void Filter_returns_route_and_direction_even_when_other_routes_fill_the_limit()
    {
        BusyStop();
        var trips = _state.Departures("B", routeId: "R", directionId: 0).Select(d => d.TripId);
        Assert.Equal(["R0a", "R0b"], trips);
    }

    [Fact]
    public void Unfiltered_call_behaves_as_before()
    {
        BusyStop();
        var departures = _state.Departures("B");
        Assert.Equal(20, departures.Length);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"O{i}"), departures.Select(d => d.TripId));
    }

    [Fact]
    public void Trips_with_no_vehicle_are_kept()
    {
        BusyStop();
        var first = _state.Departures("B", routeId: "R", directionId: 0)[0];
        Assert.Null(first.VehicleId);
        Assert.Equal(Noon.AddMinutes(30).ToUnixTimeSeconds(), first.Metro);
    }

    [Fact]
    public void Departure_carries_direction_dist_and_shape()
    {
        BusyStop();
        var east = _state.Departures("B", routeId: "R", directionId: 0)[0];
        Assert.Equal(0, east.DirectionId);
        Assert.Equal(157.2, east.Dist, 1);
        Assert.Equal("EAST", east.ShapeId);
        var west = _state.Departures("B", routeId: "R", directionId: 1)[0];
        Assert.Equal(1, west.DirectionId);
        Assert.Equal("WEST", west.ShapeId);
    }

    [Fact]
    public void Stop_visited_twice_in_a_pattern_does_not_throw()
    {
        MetroSays(2, ("LOOP1", 5));
        var departure = Assert.Single(_state.Departures("B"));
        Assert.Equal("LOOP1", departure.TripId);
        Assert.Equal(157.2, departure.Dist, 1);
    }

    [Fact]
    public void Loop_bus_past_its_first_visit_counts_toward_its_second()
    {
        MetroSays(4, ("LOOP1", 5));
        foreach (var (point, i) in new[] { 1, 3, 5, 7 }.Select((p, i) => (p, i)))
            Ping("LOOP1", point, -120 + i * 30);
        var departure = Assert.Single(_state.Departures("B"));
        Assert.Equal("V1", departure.VehicleId);
        Assert.Equal(18 * 78.6, departure.Dist, 1);
    }
}
