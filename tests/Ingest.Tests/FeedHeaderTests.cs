using Transit.Archive;

namespace Ingest.Tests;

public sealed class FeedHeaderTests
{
    // FeedMessage { header { gtfs_realtime_version: "2.0" (field 1), incrementality: 0 (field 2), timestamp: 1761580800 (field 3) }, entity {...} }
    static byte[] Message(ulong timestamp, bool timestampFirst = false)
    {
        var ts = new List<byte> { 0x18 };
        ts.AddRange(Varint(timestamp));
        byte[] version = [0x0A, 0x03, (byte)'2', (byte)'.', (byte)'0', 0x10, 0x00];
        var header = timestampFirst ? [.. ts, .. version] : version.Concat(ts).ToArray();
        byte[] entity = [0x12, 0x04, 0x0A, 0x02, (byte)'v', (byte)'1'];
        return [0x0A, (byte)header.Length, .. header, .. entity];
    }

    static IEnumerable<byte> Varint(ulong v)
    {
        while (v >= 0x80) { yield return (byte)(v | 0x80); v >>= 7; }
        yield return (byte)v;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsHeaderTimestamp(bool timestampFirst) =>
        Assert.Equal(1761580800UL, FeedHeader.ReadTimestamp(Message(1761580800, timestampFirst)));

    [Fact]
    public void ReturnsNullForNonProtobufPayload() =>
        Assert.Null(FeedHeader.ReadTimestamp("<html>503</html>"u8));

    [Fact]
    public void ReturnsNullForTruncatedMessage() =>
        Assert.Null(FeedHeader.ReadTimestamp(Message(1761580800).AsSpan(0, 6)));
}
