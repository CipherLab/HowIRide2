using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using Transit.Eta;

namespace Transit.Gtfs;

/// <summary>
/// Dates whose timetable runs as a different day type from their weekday, such as Thanksgiving on Sunday service,
/// read from the GTFS calendars rather than a list of holidays.
/// </summary>
public static class ServiceDays
{
    /// <summary>How closely a date's trips must match another day type's timetable (and beat its own) to count as that day type.</summary>
    public const double MinMatch = 0.5;

    static readonly string[] Columns = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    /// <summary>Archived feeds never change, so each is read once per process.</summary>
    static readonly ConcurrentDictionary<(string Path, long Length), FeedDays> Cache = new();

    public sealed record FeedDays(string From, string To, IReadOnlyDictionary<string, DayType> Overrides);

    /// <summary>
    /// Overrides from every feed archived in <paramref name="directories"/>, oldest first, so a newer feed's view of a
    /// date it covers wins. Feeds that have since been replaced still label the past days their calendars covered.
    /// </summary>
    public static IReadOnlyDictionary<string, DayType> FromArchives(params string[] directories)
    {
        var result = new SortedDictionary<string, DayType>(StringComparer.Ordinal);
        var zips = directories.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.zip"))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ThenBy(p => p, StringComparer.Ordinal);
        foreach (var zip in zips)
        {
            FeedDays days;
            try { days = Cache.GetOrAdd((zip, new FileInfo(zip).Length), key => FromFeed(key.Path)); }
            catch (Exception e) when (e is IOException or InvalidDataException) { continue; }
            foreach (var date in result.Keys.Where(d => string.CompareOrdinal(d, days.From) >= 0 && string.CompareOrdinal(d, days.To) <= 0).ToList())
                result.Remove(date);
            foreach (var (date, type) in days.Overrides)
                result[date] = type;
        }
        return result;
    }

    public static FeedDays FromFeed(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        List<CsvRow> Table(string name)
        {
            if (zip.GetEntry(name) is not { } entry) return [];
            using var reader = new StreamReader(entry.Open());
            return Csv.Read(reader).ToList();
        }
        var tripsPerService = Table("trips.txt").GroupBy(r => r["service_id"]).ToDictionary(g => g.Key, g => g.Count());
        var calendar = Table("calendar.txt");
        var exceptions = Table("calendar_dates.txt");
        return Classify(calendar, exceptions, tripsPerService);
    }

    /// <summary>
    /// Compares the trips running on each date with the trips its calendar lists for every Saturday and every Sunday
    /// (and for its own weekday), weighting each service by its trips. Exceptions are what make a holiday differ.
    /// </summary>
    public static FeedDays Classify(IReadOnlyList<CsvRow> calendar, IReadOnlyList<CsvRow> exceptions, IReadOnlyDictionary<string, int> tripsPerService)
    {
        var overrides = new Dictionary<string, DayType>();
        var dates = calendar.SelectMany(r => new[] { r["start_date"], r["end_date"] }).Concat(exceptions.Select(r => r["date"]))
            .Where(d => d.Length == 8).ToList();
        if (dates.Count == 0) return new FeedDays("", "", overrides);
        var (from, to) = (dates.Min(StringComparer.Ordinal)!, dates.Max(StringComparer.Ordinal)!);
        var byDate = exceptions.ToLookup(r => r["date"]);

        int Trips(string service) => tripsPerService.GetValueOrDefault(service);
        Dictionary<string, int> Listed(string date, string column) => calendar
            .Where(r => string.CompareOrdinal(r["start_date"], date) <= 0 && string.CompareOrdinal(date, r["end_date"]) <= 0 && r[column] == "1")
            .ToDictionary(r => r["service_id"], r => Trips(r["service_id"]));

        var first = DateTime.ParseExact(from, "yyyyMMdd", CultureInfo.InvariantCulture);
        var last = DateTime.ParseExact(to, "yyyyMMdd", CultureInfo.InvariantCulture);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var date = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var own = Columns[((int)day.DayOfWeek + 6) % 7];
            var running = Listed(date, own);
            foreach (var r in byDate[date])
                if (r["exception_type"] == "1") running[r["service_id"]] = Trips(r["service_id"]);
                else if (r["exception_type"] == "2") running.Remove(r["service_id"]);
            if (running.Values.Sum() == 0) continue;

            var nominal = ServiceCalendar.ByWeekday(date);
            if (nominal == DayType.Sunday) continue;
            var ownMatch = Match(running, Listed(date, own));
            (DayType Type, double Match)? best = null;
            foreach (var (type, column) in new[] { (DayType.Saturday, "saturday"), (DayType.Sunday, "sunday") })
            {
                if (type == nominal) continue;
                var match = Match(running, Listed(date, column));
                if (match >= MinMatch && match > ownMatch && match > (best?.Match ?? 0))
                    best = (type, match);
            }
            if (best is { } b) overrides[date] = b.Type;
        }
        return new FeedDays(from, to, overrides);
    }

    /// <summary>Trip-weighted overlap of two sets of services, 0 to 1.</summary>
    static double Match(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        double shared = 0, either = 0;
        foreach (var key in a.Keys.Union(b.Keys))
        {
            var (x, y) = (a.GetValueOrDefault(key), b.GetValueOrDefault(key));
            shared += Math.Min(x, y);
            either += Math.Max(x, y);
        }
        return either == 0 ? 0 : shared / either;
    }
}
