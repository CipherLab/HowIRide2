using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Ingest;

/// <summary>
/// Keeps a versioned copy of every static GTFS zip ever published:
/// {DataDir}/static/{feed}/{last-modified:yyyyMMddTHHmmssZ}.zip. Uses If-Modified-Since so routine
/// checks cost one 304, and skips saving when the bytes match the newest copy we already have.
/// A 404 is expected for next/gtfs.zip outside the Thursday-to-Saturday window.
/// </summary>
public sealed class StaticGtfsWatcher(
    IngestOptions options,
    HttpClient http,
    TimeProvider clock,
    ILogger<StaticGtfsWatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.StaticCheckInterval, clock);
        do
        {
            foreach (var feed in options.Static)
            {
                try
                {
                    await Check(feed, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning(ex, "{Feed}: static check failed", feed.Name);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    async Task Check(FeedSource feed, CancellationToken ct)
    {
        var dir = Path.Combine(options.DataDir, "static", feed.Name);
        Directory.CreateDirectory(dir);
        var newest = Directory.EnumerateFiles(dir, "*.zip").Order(StringComparer.Ordinal).LastOrDefault();

        using var request = new HttpRequestMessage(HttpMethod.Get, feed.Url);
        if (newest is not null)
            request.Headers.IfModifiedSince = File.GetLastWriteTimeUtc(newest);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound)
        {
            log.LogDebug("{Feed}: {Status}", feed.Name, response.StatusCode);
            return;
        }
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var newestBytes = newest is null ? null : await File.ReadAllBytesAsync(newest, ct);
        if (newestBytes is not null && SHA256.HashData(bytes).AsSpan().SequenceEqual(SHA256.HashData(newestBytes)))
        {
            log.LogDebug("{Feed}: unchanged content", feed.Name);
            return;
        }

        var lastModified = response.Content.Headers.LastModified ?? clock.GetUtcNow();
        var path = Path.Combine(dir, lastModified.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'") + ".zip");
        await File.WriteAllBytesAsync(path, bytes, ct);
        File.SetLastWriteTimeUtc(path, lastModified.UtcDateTime);
        log.LogInformation("{Feed}: saved new version {Path} ({MB:F1} MB)", feed.Name, path, bytes.Length / 1e6);
    }
}
