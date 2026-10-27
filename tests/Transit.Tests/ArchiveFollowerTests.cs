using Transit.Archive;

namespace Transit.Tests;

public sealed class ArchiveFollowerTests : IDisposable
{
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
    readonly string _root = Directory.CreateTempSubdirectory("follow-").FullName;
    string FeedDir => Path.Combine(_root, "feed");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static async Task<List<RawRecord>> Take(IAsyncEnumerable<RawRecord> source, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = new List<RawRecord>();
        await foreach (var r in source.WithCancellation(cts.Token))
        {
            result.Add(r);
            if (result.Count == count) break;
        }
        return result;
    }

    [Fact]
    public async Task ReadsClosedAndAbandonedHoursInOrderAfterTheStartTime()
    {
        var h10 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        string closed;
        using (var w = new RawArchiveWriter(_root, "feed", _ => { }))
        {
            w.Append(h10.AddMinutes(1), [1]);
            w.Append(h10.AddMinutes(2), [2]);
            closed = RawArchiveWriter.PathFor(FeedDir, h10.UtcDateTime);
        }
        RawArchiveWriter.Compress(closed);
        // Nothing at 11:00; 12:00 was left uncompressed, as if ingest died before rollover.
        using (var w = new RawArchiveWriter(_root, "feed", _ => { }))
        {
            w.Append(h10.AddHours(2).AddMinutes(5), [3]);
            w.Append(h10.AddHours(2).AddMinutes(6), [4]);
        }

        var records = await Take(new ArchiveFollower(FeedDir, TimeProvider.System, Poll).Follow(h10.AddMinutes(1)), 3);

        Assert.Equal([[2], [3], [4]], records.Select(r => r.Payload));
    }

    [Fact]
    public async Task TailsTheLiveHourAndMovesOnWhenTheNextHourStarts()
    {
        var now = TimeProvider.System.GetUtcNow();
        var nextHour = new DateTimeOffset(ArchiveFollower.FloorHour(now).AddHours(1));
        using var writer = new RawArchiveWriter(_root, "feed", _ => { });
        writer.Append(now, [1]);

        await using var records = new ArchiveFollower(FeedDir, TimeProvider.System, Poll).Follow(now.AddSeconds(-1)).GetAsyncEnumerator();
        Assert.True(await records.MoveNextAsync());
        Assert.Equal([1], records.Current.Payload);

        // At the end of the live file the follower waits rather than finishing.
        var pending = records.MoveNextAsync().AsTask();
        await Task.Delay(Poll * 5);
        Assert.False(pending.IsCompleted);

        writer.Append(now.AddMilliseconds(1), [2]);
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([2], records.Current.Payload);

        writer.Append(nextHour.AddSeconds(1), [3]);
        Assert.True(await records.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([3], records.Current.Payload);
    }

    [Fact]
    public void FindsEarliestRecordedHour()
    {
        using (var w = new RawArchiveWriter(_root, "feed", _ => { }))
        {
            w.Append(new DateTimeOffset(2026, 3, 2, 7, 30, 0, TimeSpan.Zero), [1]);
            w.Append(new DateTimeOffset(2026, 3, 1, 23, 5, 0, TimeSpan.Zero), [2]);
        }

        Assert.Equal(new DateTime(2026, 3, 1, 23, 0, 0, DateTimeKind.Utc), ArchiveFollower.EarliestHour(FeedDir));
        Assert.Null(ArchiveFollower.EarliestHour(Path.Combine(_root, "missing")));
    }
}
