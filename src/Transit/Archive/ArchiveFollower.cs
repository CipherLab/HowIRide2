using System.Globalization;
using System.Runtime.CompilerServices;

namespace Transit.Archive;

/// <summary>
/// Streams one feed's archive in fetch order, starting after a given time: closed (.zst) hours first,
/// then the hour ingest is writing, tailed as it grows. Never ends on its own.
/// </summary>
public sealed class ArchiveFollower(string feedDir, TimeProvider clock, TimeSpan pollInterval)
{
    /// <summary>
    /// After this long past an hour's end, an open file that has stopped growing is treated as finished even
    /// with no later file (ingest was down at rollover). Longer than ingest's write queue can lag.
    /// </summary>
    public static readonly TimeSpan AbandonedHourGrace = TimeSpan.FromMinutes(30);

    public async IAsyncEnumerable<RawRecord> Follow(DateTimeOffset after, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hour = FloorHour(after);
        var header = new byte[RawArchiveWriter.HeaderSize];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var closed = PathFor(hour, RawArchiveWriter.ClosedExtension);
            if (File.Exists(closed))
            {
                foreach (var record in RawArchiveReader.Read(closed))
                    if (record.FetchedAt > after)
                        yield return record;
                hour = hour.AddHours(1);
                continue;
            }

            var open = TryOpen(PathFor(hour, RawArchiveWriter.OpenExtension));
            if (open is null)
            {
                // Ingest compresses by writing .zst and then deleting .pbs; recheck before concluding the hour is empty.
                if (File.Exists(closed)) continue;
                if (hour.AddHours(1) <= clock.GetUtcNow()) hour = hour.AddHours(1);
                else await Task.Delay(pollInterval, clock, ct);
                continue;
            }

            using (open)
            {
                while (true)
                {
                    var start = open.Position;
                    if (RawArchiveReader.TryRead(open, header) is { } record)
                    {
                        if (record.FetchedAt > after)
                            yield return record;
                        continue;
                    }
                    open.Position = start;
                    if (HourFinished(hour)) break;
                    await Task.Delay(pollInterval, clock, ct);
                }
            }
            hour = hour.AddHours(1);
        }
    }

    /// <summary>Ingest only starts the next hour's file after the last record of this one is flushed, so either marks the end.</summary>
    bool HourFinished(DateTime hour)
    {
        var next = hour.AddHours(1);
        return File.Exists(PathFor(hour, RawArchiveWriter.ClosedExtension))
            || File.Exists(PathFor(next, RawArchiveWriter.OpenExtension))
            || File.Exists(PathFor(next, RawArchiveWriter.ClosedExtension))
            || clock.GetUtcNow() >= next + AbandonedHourGrace;
    }

    string PathFor(DateTime hour, string extension) =>
        Path.Combine(feedDir, hour.ToString("yyyy-MM-dd"), hour.ToString("HH") + extension);

    static FileStream? TryOpen(string path)
    {
        try
        {
            // Delete sharing: ingest removes the .pbs after compressing it, and this handle must not block that.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static DateTime FloorHour(DateTimeOffset t)
    {
        var utc = t.UtcDateTime;
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Start of the earliest hour with any file, or null if the feed has no archive yet.</summary>
    public static DateTime? EarliestHour(string feedDir)
    {
        if (!Directory.Exists(feedDir)) return null;
        var first = Directory.EnumerateFiles(feedDir, "*.pbs*", SearchOption.AllDirectories)
            .Select(p => (Day: Path.GetFileName(Path.GetDirectoryName(p))!, Hour: Path.GetFileName(p)[..2]))
            .Where(x => IsDay(x.Day) && int.TryParse(x.Hour, out _))
            .Order()
            .FirstOrDefault();
        return first.Day is null ? null : ParseDay(first.Day).AddHours(int.Parse(first.Hour, CultureInfo.InvariantCulture));

        static bool IsDay(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        static DateTime ParseDay(string s) => DateTime.SpecifyKind(DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
}
