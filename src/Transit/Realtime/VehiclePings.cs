using Transit.Crossings;
using TransitRealtime;

namespace Transit.Realtime;

public static class VehiclePings
{
    /// <summary>Extracts every vehicle position that has an id, a timestamp and coordinates.</summary>
    public static IEnumerable<VehiclePing> From(FeedMessage message)
    {
        foreach (var entity in message.Entity)
        {
            if (entity.Vehicle is not { } v || v.Position is null || !v.HasTimestamp)
                continue;
            var id = v.Vehicle?.Id is { Length: > 0 } vid ? vid : entity.Id;
            yield return new VehiclePing(
                id,
                v.Trip?.TripId is { Length: > 0 } trip ? trip : null,
                v.Trip?.StartDate is { Length: > 0 } date ? date : null,
                DateTimeOffset.FromUnixTimeSeconds((long)v.Timestamp),
                v.Position.Latitude,
                v.Position.Longitude,
                v.HasCurrentStopSequence ? (int)v.CurrentStopSequence : null);
        }
    }
}
