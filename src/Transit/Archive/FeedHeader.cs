namespace Transit.Archive;

/// <summary>
/// Reads FeedMessage.header.timestamp straight from protobuf wire format, which is all ingest needs
/// to dedupe snapshots. Full GTFS-rt parsing belongs to the crossing detector, not the recorder.
/// </summary>
public static class FeedHeader
{
    const int FeedMessageHeaderField = 1;
    const int FeedHeaderTimestampField = 3;

    /// <summary>Returns the header timestamp (unix seconds), or null if the payload isn't a readable FeedMessage.</summary>
    public static ulong? ReadTimestamp(ReadOnlySpan<byte> feedMessage)
    {
        var header = FindLengthDelimited(feedMessage, FeedMessageHeaderField);
        if (header is not { } h) return null;

        var span = feedMessage.Slice(h.Start, h.Length);
        var pos = 0;
        while (pos < span.Length)
        {
            if (!TryReadVarint(span, ref pos, out var tag)) return null;
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (field == FeedHeaderTimestampField && wireType == 0)
                return TryReadVarint(span, ref pos, out var ts) ? ts : null;
            if (!Skip(span, ref pos, wireType)) return null;
        }
        return null;
    }

    static (int Start, int Length)? FindLengthDelimited(ReadOnlySpan<byte> span, int wantedField)
    {
        var pos = 0;
        while (pos < span.Length)
        {
            if (!TryReadVarint(span, ref pos, out var tag)) return null;
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (field == wantedField && wireType == 2)
            {
                if (!TryReadVarint(span, ref pos, out var len) || (ulong)pos + len > (ulong)span.Length) return null;
                return (pos, (int)len);
            }
            if (!Skip(span, ref pos, wireType)) return null;
        }
        return null;
    }

    static bool Skip(ReadOnlySpan<byte> span, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: return TryReadVarint(span, ref pos, out _);
            case 1: pos += 8; return pos <= span.Length;
            case 5: pos += 4; return pos <= span.Length;
            case 2:
                if (!TryReadVarint(span, ref pos, out var len) || (ulong)pos + len > (ulong)span.Length) return false;
                pos += (int)len;
                return true;
            default: return false;
        }
    }

    static bool TryReadVarint(ReadOnlySpan<byte> span, ref int pos, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && pos < span.Length; shift += 7)
        {
            var b = span[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }
}
