using System.Text;
using Transit.Archive;
using Transit.Eta;
using Transit.Gtfs;

namespace Transit.Backtest;

/// <summary>One trip's predicted departures in a logged sample; times are whole unix seconds, as the app shows them.</summary>
public sealed record LiveTripPrediction(string ServiceDate, string TripId, IReadOnlyList<(int StopSequence, int Depart)> Stops);

/// <summary>
/// The payload of one sample of what the live API was predicting. Samples are stored as records in a
/// <see cref="RawArchiveWriter"/> archive, so hours close, compress and tolerate crashes as the feed archive does.
/// </summary>
public static class LivePredictionLog
{
    const byte Version = 1;

    public static byte[] Encode(IReadOnlyList<LiveTripPrediction> trips)
    {
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer, Encoding.UTF8);
        w.Write(Version);
        w.Write7BitEncodedInt(trips.Count);
        foreach (var trip in trips)
        {
            w.Write(trip.ServiceDate);
            w.Write(trip.TripId);
            w.Write7BitEncodedInt(trip.Stops.Count);
            foreach (var (sequence, depart) in trip.Stops)
            {
                w.Write7BitEncodedInt(sequence);
                w.Write(depart);
            }
        }
        w.Flush();
        return buffer.ToArray();
    }

    public static IReadOnlyList<LiveTripPrediction> Decode(byte[] payload)
    {
        using var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        var version = r.ReadByte();
        if (version != Version) throw new InvalidDataException($"Unknown live prediction format {version}.");
        var trips = new LiveTripPrediction[r.Read7BitEncodedInt()];
        for (var t = 0; t < trips.Length; t++)
        {
            var (date, tripId) = (r.ReadString(), r.ReadString());
            var stops = new (int, int)[r.Read7BitEncodedInt()];
            for (var s = 0; s < stops.Length; s++)
                stops[s] = (r.Read7BitEncodedInt(), r.ReadInt32());
            trips[t] = new LiveTripPrediction(date, tripId, stops);
        }
        return trips;
    }
}

public static class LivePredictionSampler
{
    /// <summary>The model's departure time for every stop of every candidate trip the bus should reach within the window.</summary>
    public static IReadOnlyList<LiveTripPrediction> Sample(
        SegmentEtaPredictor model, IEnumerable<(string Date, string TripId)> candidates, Func<string, TripInfo?> resolve,
        Func<string, string, IReadOnlyList<CrossingRow>> rowsOf, TimeZoneInfo zone, double now, double windowSeconds)
    {
        var origins = new Dictionary<string, double>();
        var result = new List<LiveTripPrediction>();
        foreach (var (date, tripId) in candidates)
        {
            if (resolve(tripId) is not { } trip) continue;
            if (!origins.TryGetValue(date, out var origin))
                origins[date] = origin = StaticFeed.ServiceDayOrigin(date, zone).ToUnixTimeSeconds();
            var stops = model.PredictAlong(trip, rowsOf(date, tripId), model.Calendar.DayTypeOf(date), origin, now, id => rowsOf(date, id), now + windowSeconds);
            if (stops.Count > 0)
                result.Add(new LiveTripPrediction(date, tripId, stops.Select(s => (s.StopSequence, (int)Math.Round(s.Depart))).ToArray()));
        }
        return result;
    }
}

/// <summary>What the live API actually predicted, read back from its log and scored by the rules Metro's feed is scored by.</summary>
public sealed class LivePredictor(string logDir) : IPredictor
{
    /// <summary>Samples are a minute apart; this allows one to be late without letting a real gap count as a prediction.</summary>
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromSeconds(90);

    public string Name => "live-v1";

    public void Fill(IReadOnlyList<BacktestTarget> targets, PredictionTable table) =>
        LatestSnapshot.Fill(targets, table, MaxSampleAge, (from, to) => ArchiveFollower.ReadRange(logDir, from, to).Select(record =>
            (record.FetchedAt.ToUnixTimeMilliseconds() / 1000.0, Rows(LivePredictionLog.Decode(record.Payload)))));

    static IEnumerable<(string Date, string Trip, int Sequence, double Predicted)> Rows(IReadOnlyList<LiveTripPrediction> trips) =>
        trips.SelectMany(t => t.Stops.Select(s => (t.ServiceDate, t.TripId, s.StopSequence, (double)s.Depart)));
}
