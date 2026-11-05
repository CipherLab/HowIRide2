using System.Globalization;

namespace Transit.Eta;

public enum DayType { Weekday, Saturday, Sunday }

/// <summary>Calendar helpers in the agency's time zone.</summary>
public sealed class ServiceCalendar(TimeZoneInfo zone)
{
    public const int SlotMinutes = 15;
    public const int SlotsPerDay = 24 * 60 / SlotMinutes;

    /// <summary>Holidays are not distinguished yet; they fall under their weekday.</summary>
    public static DayType DayTypeOf(string serviceDate) =>
        DateTime.ParseExact(serviceDate, "yyyyMMdd", CultureInfo.InvariantCulture).DayOfWeek switch
        {
            DayOfWeek.Saturday => DayType.Saturday,
            DayOfWeek.Sunday => DayType.Sunday,
            _ => DayType.Weekday,
        };

    /// <summary>15-minute slot of local clock time, 0-95.</summary>
    public int SlotOf(double unixSeconds)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)), zone);
        return (local.Hour * 60 + local.Minute) / SlotMinutes;
    }
}

/// <summary>
/// Historical medians per key (a segment, or a stop for dwell) by day type and 15-minute slot: the v0 lookup table.
/// Sparse cells fall back to nearby slots, then the whole day type, then all days.
/// </summary>
/// <param name="trim">
/// When set, each cell holds a mean with this share cut from each end instead of a median. Medians don't add up:
/// summed over many short segments they drop each one's occasional slow run, so long trips come out short. Means add.
/// </param>
public sealed class SlotMedians(double? trim = null)
{
    /// <summary>Samples a cell needs before its median is trusted.</summary>
    public const int MinSamples = 3;

    /// <summary>How many slots either side to borrow from (an hour).</summary>
    const int MaxSlotDistance = 4;

    readonly Dictionary<string, Profile> _profiles = [];

    sealed class Profile
    {
        public readonly Dictionary<int, List<double>> Raw = [];
        public readonly Dictionary<int, (double Median, int N)> Cells = [];
        public readonly (double Median, int N)[] ByDayType = new (double, int)[3];
        public (double Median, int N) All;
    }

    public int Keys => _profiles.Count;

    public void Add(string key, DayType dayType, int slot, double value)
    {
        if (!_profiles.TryGetValue(key, out var p))
            _profiles[key] = p = new Profile();
        var cell = (int)dayType * ServiceCalendar.SlotsPerDay + slot;
        if (!p.Raw.TryGetValue(cell, out var list))
            p.Raw[cell] = list = [];
        list.Add(value);
    }

    /// <summary>Computes medians (or trimmed means) from everything added. Call once after the last Add.</summary>
    public SlotMedians Build()
    {
        foreach (var p in _profiles.Values)
        {
            foreach (var (cell, values) in p.Raw)
                p.Cells[cell] = (Typical(values), values.Count);
            for (var d = 0; d < 3; d++)
            {
                var day = p.Raw.Where(kv => kv.Key / ServiceCalendar.SlotsPerDay == d).SelectMany(kv => kv.Value).ToList();
                p.ByDayType[d] = day.Count > 0 ? (Typical(day), day.Count) : (double.NaN, 0);
            }
            var all = p.Raw.Values.SelectMany(v => v).ToList();
            p.All = (Typical(all), all.Count);
            p.Raw.Clear();
        }
        return this;
    }

    public double? Get(string key, DayType dayType, int slot)
    {
        if (!_profiles.TryGetValue(key, out var p)) return null;
        var baseCell = (int)dayType * ServiceCalendar.SlotsPerDay;
        for (var distance = 0; distance <= MaxSlotDistance; distance++)
        {
            foreach (var s in distance == 0 ? [slot] : new[] { slot - distance, slot + distance })
            {
                if (s < 0 || s >= ServiceCalendar.SlotsPerDay) continue;
                if (p.Cells.TryGetValue(baseCell + s, out var c) && c.N >= MinSamples)
                    return c.Median;
            }
        }
        var byDay = p.ByDayType[(int)dayType];
        if (byDay.N >= MinSamples) return byDay.Median;
        return p.All.N > 0 ? p.All.Median : null;
    }

    double Typical(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        if (trim is { } share)
        {
            var cut = (int)(n * share);
            return values.Skip(cut).Take(n - 2 * cut).Average();
        }
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}

/// <summary>Segment travel and stop dwell tables learned from history.</summary>
public sealed class SegmentStats(SlotMedians travel, SlotMedians dwell, ServiceCalendar calendar, SlotMedians? startDelays = null)
{
    /// <summary>A start this far from the timetable is a swapped or replaced bus, not how the stop usually runs.</summary>
    const double MaxStartDelaySeconds = 1800;

    /// <summary>How long after the timetable (negative: before) trips typically leave this first stop; 0 with no history.</summary>
    public double StartDelay(string firstCheckpoint, DayType dayType, double scheduledUnix) =>
        startDelays?.Get(firstCheckpoint, dayType, calendar.SlotOf(scheduledUnix)) ?? 0;

    public SlotMedians TravelTable => travel;
    public SlotMedians DwellTable => dwell;
    public ServiceCalendar Calendar => calendar;

    public double? Travel(string segment, DayType dayType, double atUnix) => travel.Get(segment, dayType, calendar.SlotOf(atUnix));
    public double? Dwell(string stopCheckpoint, DayType dayType, double atUnix) => dwell.Get(stopCheckpoint, dayType, calendar.SlotOf(atUnix));

    /// <summary>The timetabled start of a trip on a service date, for learning start delays.</summary>
    public static Func<string, string, double?> ScheduledStarts(Func<string, Gtfs.TripInfo?> resolve, TimeZoneInfo zone) => (date, tripId) =>
        resolve(tripId) is { ScheduledDepartures: [>= 0 and var first, ..] } ? Gtfs.StaticFeed.ServiceDayOrigin(date, zone).ToUnixTimeSeconds() + first : null;

    /// <summary>Learns from history the caller has judged to be in the past (the training window).</summary>
    public static SegmentStats Build(CrossingHistory history, ServiceCalendar calendar, Func<string, double, bool> include, double? trim = null,
        Func<string, string, double?>? scheduledStart = null)
    {
        var travel = new SlotMedians(trim);
        foreach (var o in history.Segments())
            if (include(o.ServiceDate, o.End))
                travel.Add(o.Segment, ServiceCalendar.DayTypeOf(o.ServiceDate), calendar.SlotOf(o.Start), o.Travel);
        var dwell = new SlotMedians(trim);
        foreach (var (cp, date, arrive, seconds) in history.Dwells())
            if (include(date, arrive + seconds))
                dwell.Add(cp, ServiceCalendar.DayTypeOf(date), calendar.SlotOf(arrive), seconds);
        SlotMedians? startDelays = null;
        if (scheduledStart is not null)
        {
            startDelays = new SlotMedians(trim);
            foreach (var (cp, date, trip, depart) in history.Starts())
                if (include(date, depart) && scheduledStart(date, trip) is { } scheduled && Math.Abs(depart - scheduled) <= MaxStartDelaySeconds)
                    startDelays.Add(cp, ServiceCalendar.DayTypeOf(date), calendar.SlotOf(scheduled), depart - scheduled);
            startDelays.Build();
        }
        return new SegmentStats(travel.Build(), dwell.Build(), calendar, startDelays);
    }
}
