using System.IO.Compression;
using Transit.Eta;
using Transit.Gtfs;

namespace Transit.Tests;

public sealed class ServiceDaysTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("service-days-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static List<CsvRow> Rows(string csv) => Csv.Read(new StringReader(csv)).ToList();

    // Shaped like Metro Transit's November 2026 pick: weekday, Saturday and Sunday services, where Thanksgiving
    // (Thursday 26th) drops the weekday services and runs the ones also scheduled for Sundays.
    const string Calendar = """
        service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date
        WK,1,1,1,1,1,0,0,20261109,20261204
        SAT,0,0,0,0,0,1,0,20261107,20261128
        SUN,0,0,0,1,0,0,1,20261108,20261129
        """;
    const string Exceptions = """
        service_id,date,exception_type
        WK,20261126,2
        SUN,20261112,2
        SUN,20261119,2
        SUN,20261203,2
        WK,20261127,2
        SAT,20261127,1
        """;
    static readonly Dictionary<string, int> Trips = new() { ["WK"] = 8000, ["SAT"] = 6000, ["SUN"] = 5000 };

    [Fact]
    public void AHolidayOnSundayServiceRunsAsSunday()
    {
        var days = ServiceDays.Classify(Rows(Calendar), Rows(Exceptions), Trips);

        Assert.Equal(DayType.Sunday, days.Overrides["20261126"]);
        Assert.Equal(DayType.Saturday, days.Overrides["20261127"]); // Friday on Saturday service
        Assert.False(days.Overrides.ContainsKey("20261119"));        // an ordinary Thursday
        Assert.False(days.Overrides.ContainsKey("20261121"));        // Saturdays and Sundays are what they say
        Assert.Equal(("20261107", "20261204"), (days.From, days.To));
    }

    [Fact]
    public void AReducedWeekdayStaysAWeekday()
    {
        // Fewer weekday trips, but nothing resembling a weekend timetable.
        var calendar = Calendar + "\nWK2,1,1,1,1,1,0,0,20261109,20261204";
        var exceptions = "service_id,date,exception_type\nWK,20261203,2\nSUN,20261112,2";
        var trips = new Dictionary<string, int>(Trips) { ["WK2"] = 3000 };

        var days = ServiceDays.Classify(Rows(calendar), Rows(exceptions), trips);

        Assert.Empty(days.Overrides);
    }

    [Fact]
    public void ANewerFeedDecidesTheDatesItCoversAndOlderFeedsKeepThePast()
    {
        var current = Path.Combine(_dir, "metro-gtfs");
        Directory.CreateDirectory(current);
        // Older pick: Thanksgiving on Sunday service, and an earlier holiday the newer feed no longer covers.
        WriteFeed(Path.Combine(current, "20261001T000000Z.zip"), """
            service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date
            WK,1,1,1,1,1,0,0,20261005,20261129
            SUN,1,0,0,1,0,0,1,20261004,20261129
            """, """
            service_id,date,exception_type
            WK,20261012,2
            WK,20261126,2
            """);
        // Newer pick from November 20 on, where the 26th is an ordinary Thursday.
        WriteFeed(Path.Combine(current, "20261101T000000Z.zip"), """
            service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date
            WK,1,1,1,1,1,0,0,20261120,20261231
            SUN,0,0,0,0,0,0,1,20261120,20261231
            """, "service_id,date,exception_type\n");

        var overrides = ServiceDays.FromArchives(current, Path.Combine(_dir, "missing"));

        Assert.Equal(DayType.Sunday, overrides["20261012"]);
        Assert.False(overrides.ContainsKey("20261126"));
        Assert.Equal(DayType.Sunday, new ServiceCalendar(TimeZoneInfo.Utc, overrides).DayTypeOf("20261012"));
        Assert.Equal(DayType.Weekday, new ServiceCalendar(TimeZoneInfo.Utc, overrides).DayTypeOf("20261013"));
    }

    static void WriteFeed(string path, string calendar, string exceptions)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
        Entry("calendar.txt", calendar);
        Entry("calendar_dates.txt", exceptions);
        Entry("trips.txt", "route_id,service_id,trip_id\nR,WK,1\nR,WK,2\nR,SUN,3\n");
    }
}
