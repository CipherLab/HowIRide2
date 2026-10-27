using System.Buffers.Binary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Transit.Archive;

/// <summary>
/// Append-only archive of raw feed snapshots, one file per feed per UTC hour:
/// {root}/{feed}/{yyyy-MM-dd}/{HH}.pbs while open, {HH}.pbs.zst once the hour closes.
/// Each record is [int64 BE fetched-at unix ms][int32 BE length][payload bytes].
/// A crash can leave a torn final record. Readers stop cleanly at it, and the writer cuts it off
/// before appending to that file again so later records stay aligned.
/// </summary>
public sealed class RawArchiveWriter : IDisposable
{
    public const string OpenExtension = ".pbs";
    public const string ClosedExtension = ".pbs.zst";
    const int CompressionLevel = 19;
    const int WindowLog = 27; // 128 MB: spans many ~1 MB snapshots so each compresses as a diff of the last
    public const int HeaderSize = 12;

    readonly string _feedDir;
    readonly Action<string> _onFileClosed;
    FileStream? _current;
    DateTime _currentHour;

    public RawArchiveWriter(string root, string feedName, Action<string>? onFileClosed = null)
    {
        _feedDir = Path.Combine(root, feedName);
        _onFileClosed = onFileClosed ?? (path => Task.Run(() => Compress(path)));
        Directory.CreateDirectory(_feedDir);
    }

    public string FeedDir => _feedDir;

    public void Append(DateTimeOffset fetchedAt, ReadOnlySpan<byte> payload)
    {
        var hour = new DateTime(fetchedAt.UtcDateTime.Year, fetchedAt.UtcDateTime.Month, fetchedAt.UtcDateTime.Day,
            fetchedAt.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc);
        if (_current is null || hour != _currentHour)
            Roll(hour);

        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteInt64BigEndian(header, fetchedAt.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32BigEndian(header[8..], payload.Length);
        _current!.Write(header);
        _current.Write(payload);
        _current.Flush();
    }

    public static string PathFor(string feedDir, DateTime hourUtc) =>
        Path.Combine(feedDir, hourUtc.ToString("yyyy-MM-dd"), hourUtc.ToString("HH") + OpenExtension);

    void Roll(DateTime hour)
    {
        CloseCurrent();
        var path = PathFor(_feedDir, hour);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TruncateTornTail(path);
        _current = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _currentHour = hour;
    }

    /// <summary>Cuts a partially written final record left by a crash, so the next append starts on a record boundary.</summary>
    public static void TruncateTornTail(string path)
    {
        if (!File.Exists(path)) return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderSize];
        long end = 0;
        while (end + HeaderSize <= file.Length)
        {
            file.Position = end;
            file.ReadExactly(header);
            var next = end + HeaderSize + BinaryPrimitives.ReadInt32BigEndian(header[8..]);
            if (next > file.Length) break;
            end = next;
        }
        if (end < file.Length)
            file.SetLength(end);
    }

    void CloseCurrent()
    {
        if (_current is null) return;
        var path = _current.Name;
        _current.Dispose();
        _current = null;
        _onFileClosed(path);
    }

    /// <summary>
    /// Leaves the current hour uncompressed: a restart within the hour appends to it, and a later
    /// start compresses it via <see cref="FindUncompressed"/>. Compressing here would race process exit.
    /// </summary>
    public void Dispose()
    {
        _current?.Dispose();
        _current = null;
    }

    /// <summary>Finds open-format files left behind by a crash or restart, excluding the hour currently being written.</summary>
    public IEnumerable<string> FindUncompressed(DateTimeOffset now)
    {
        var currentPath = PathFor(_feedDir, new DateTime(now.UtcDateTime.Ticks - now.UtcDateTime.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc));
        return Directory.EnumerateFiles(_feedDir, "*" + OpenExtension, SearchOption.AllDirectories)
            .Where(p => p.EndsWith(OpenExtension, StringComparison.Ordinal) && Path.GetFullPath(p) != Path.GetFullPath(currentPath));
    }

    /// <summary>
    /// Compresses a closed hour file with zstd long-distance matching and deletes the original.
    /// Consecutive snapshots are near-identical, so this is ~5x smaller than gzip. Safe to rerun.
    /// </summary>
    public static void Compress(string path)
    {
        var zstPath = path[..^OpenExtension.Length] + ClosedExtension;
        var tmpPath = zstPath + ".tmp";
        using (var src = File.OpenRead(path))
        using (var dst = File.Create(tmpPath))
        using (var zst = new CompressionStream(dst, CompressionLevel))
        {
            zst.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
            zst.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, WindowLog);
            src.CopyTo(zst);
        }
        File.Move(tmpPath, zstPath, overwrite: true);
        File.Delete(path);
    }
}

public readonly record struct RawRecord(DateTimeOffset FetchedAt, byte[] Payload);

public static class RawArchiveReader
{
    static DecompressionStream OpenZstd(Stream file)
    {
        var zst = new DecompressionStream(file);
        zst.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
        return zst;
    }

    /// <summary>Reads records from a .pbs or .pbs.zst file, stopping silently at a torn trailing record.</summary>
    public static IEnumerable<RawRecord> Read(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using Stream stream = path.EndsWith(".zst", StringComparison.Ordinal) ? OpenZstd(file) : file;
        var header = new byte[RawArchiveWriter.HeaderSize];
        while (TryRead(stream, header) is { } record)
            yield return record;
    }

    /// <summary>Reads the next whole record, or returns null at end of data or a partial record (stream position is then undefined).</summary>
    public static RawRecord? TryRead(Stream stream, byte[] header)
    {
        if (stream.ReadAtLeast(header, RawArchiveWriter.HeaderSize, throwOnEndOfStream: false) < RawArchiveWriter.HeaderSize)
            return null;
        var fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(header));
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
        if (length is < 0 or > MaxPayloadBytes)
            throw new InvalidDataException($"Record at fetched-at {fetchedAt:O} claims {length} bytes; archive is corrupt.");
        var payload = new byte[length];
        if (stream.ReadAtLeast(payload, length, throwOnEndOfStream: false) < length)
            return null;
        return new RawRecord(fetchedAt, payload);
    }

    const int MaxPayloadBytes = 64 << 20;
}
