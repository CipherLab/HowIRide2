using Transit.Archive;

namespace Ingest.Tests;

public sealed class RawArchiveTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("raw-archive-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RoundTripsRecordsWithinAnHour()
    {
        var t0 = new DateTimeOffset(2026, 10, 27, 14, 0, 1, TimeSpan.Zero);
        using (var writer = new RawArchiveWriter(_root, "feed", onFileClosed: _ => { }))
        {
            writer.Append(t0, [1, 2, 3]);
            writer.Append(t0.AddSeconds(5), [4]);
        }

        var records = RawArchiveReader.Read(RawArchiveWriter.PathFor(Path.Combine(_root, "feed"), t0.UtcDateTime.Date.AddHours(14))).ToList();

        Assert.Equal(2, records.Count);
        Assert.Equal(t0, records[0].FetchedAt);
        Assert.Equal([1, 2, 3], records[0].Payload);
        Assert.Equal([4], records[1].Payload);
    }

    [Fact]
    public void RollsToNewFileAtHourBoundaryAndClosesPreviousOnly()
    {
        var closed = new List<string>();
        var t0 = new DateTimeOffset(2026, 10, 27, 14, 59, 58, TimeSpan.Zero);
        using (var writer = new RawArchiveWriter(_root, "feed", closed.Add))
        {
            writer.Append(t0, [1]);
            writer.Append(t0.AddSeconds(5), [2]);
        }

        Assert.EndsWith(Path.Combine("2026-10-27", "14.pbs"), Assert.Single(closed));
    }

    [Fact]
    public void RestartWithinHourAppendsToSameFile()
    {
        var t0 = new DateTimeOffset(2026, 10, 27, 14, 10, 0, TimeSpan.Zero);
        using (var writer = new RawArchiveWriter(_root, "feed", _ => { }))
            writer.Append(t0, [1]);
        using (var writer = new RawArchiveWriter(_root, "feed", _ => { }))
            writer.Append(t0.AddSeconds(30), [2]);

        var records = RawArchiveReader.Read(RawArchiveWriter.PathFor(Path.Combine(_root, "feed"), t0.UtcDateTime.Date.AddHours(14))).ToList();

        Assert.Equal([[1], [2]], records.Select(r => r.Payload));
    }

    [Fact]
    public void CompressedFileReadsBackIdentically()
    {
        var t0 = new DateTimeOffset(2026, 10, 27, 3, 10, 0, TimeSpan.Zero);
        using (var writer = new RawArchiveWriter(_root, "feed", _ => { }))
            writer.Append(t0, [9, 8, 7]);
        var closedPath = RawArchiveWriter.PathFor(Path.Combine(_root, "feed"), t0.UtcDateTime.Date.AddHours(3));

        RawArchiveWriter.Compress(closedPath);

        Assert.False(File.Exists(closedPath));
        var record = Assert.Single(RawArchiveReader.Read(closedPath[..^4] + RawArchiveWriter.ClosedExtension));
        Assert.Equal([9, 8, 7], record.Payload);
    }

    [Fact]
    public void ReaderStopsAtTornTrailingRecord()
    {
        var t0 = new DateTimeOffset(2026, 10, 27, 3, 10, 0, TimeSpan.Zero);
        var path = RawArchiveWriter.PathFor(Path.Combine(_root, "feed"), t0.UtcDateTime.Date.AddHours(3));
        using (var writer = new RawArchiveWriter(_root, "feed", _ => { }))
        {
            writer.Append(t0, [1, 2]);
            writer.Append(t0.AddSeconds(5), [3, 4, 5, 6]);
        }
        using (var f = new FileStream(path, FileMode.Open))
            f.SetLength(f.Length - 2);

        var record = Assert.Single(RawArchiveReader.Read(path));
        Assert.Equal([1, 2], record.Payload);
    }

    [Fact]
    public void ReopeningAfterCrashCutsTornRecordBeforeAppending()
    {
        var t0 = new DateTimeOffset(2026, 10, 27, 3, 10, 0, TimeSpan.Zero);
        var path = RawArchiveWriter.PathFor(Path.Combine(_root, "feed"), t0.UtcDateTime.Date.AddHours(3));
        using (var writer = new RawArchiveWriter(_root, "feed", _ => { }))
        {
            writer.Append(t0, [1, 2]);
            writer.Append(t0.AddSeconds(5), [3, 4, 5, 6]);
        }
        using (var f = new FileStream(path, FileMode.Open))
            f.SetLength(f.Length - 2);

        using (var writer = new RawArchiveWriter(_root, "feed", _ => { }))
            writer.Append(t0.AddSeconds(10), [7]);

        Assert.Equal([[1, 2], [7]], RawArchiveReader.Read(path).Select(r => r.Payload));
    }

    [Fact]
    public void FindUncompressedSkipsCurrentHour()
    {
        var now = new DateTimeOffset(2026, 10, 27, 15, 20, 0, TimeSpan.Zero);
        using var writer = new RawArchiveWriter(_root, "feed", _ => { });
        writer.Append(now.AddHours(-2), [1]);
        writer.Append(now, [2]);

        var leftovers = writer.FindUncompressed(now).ToList();

        Assert.EndsWith(Path.Combine("2026-10-27", "13.pbs"), Assert.Single(leftovers));
    }
}
