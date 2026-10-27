using System.Threading.Channels;

namespace Ingest;

/// <summary>
/// Polls one GTFS-realtime feed on a fixed interval and archives every snapshot whose header
/// timestamp is new. Parsing is only for dedupe and health logging; the bytes are stored untouched
/// so later pipeline bugs can be fixed by replaying the archive.
/// Writes go through a bounded queue drained by a separate task, so a stalled disk (seen as 20+ s
/// NVMe write timeouts on the Pi) delays the archive rather than making the poller skip ticks.
/// </summary>
public sealed class RealtimePoller : BackgroundService
{
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    readonly FeedSource _feed;
    readonly IngestOptions _options;
    readonly HttpClient _http;
    readonly TimeProvider _clock;
    readonly ILogger<RealtimePoller> _log;
    readonly Channel<RawRecord> _queue;

    ulong _lastHeaderTimestamp;
    long _fetched, _queued, _duplicates, _errors;
    TimeSpan _fetchTime, _slowestFetch;
    DateTimeOffset _lastStats;

    // Updated by the writer task, read and reset by the stats logger on the poll loop.
    long _archived, _bytes, _dropped, _writeErrors, _slowestAppendTicks;

    public RealtimePoller(FeedSource feed, IngestOptions options, HttpClient http, TimeProvider clock, ILogger<RealtimePoller> log)
    {
        _feed = feed;
        _options = options;
        _http = http;
        _clock = clock;
        _log = log;
        _queue = Channel.CreateBounded<RawRecord>(
            new BoundedChannelOptions(options.WriteQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var writer = new RawArchiveWriter(Path.Combine(_options.DataDir, "raw"), _feed.Name);
        var leftovers = writer.FindUncompressed(_clock.GetUtcNow()).ToList();
        if (leftovers.Count > 0)
        {
            _log.LogInformation("{Feed}: compressing {Count} leftover hour file(s) in background", _feed.Name, leftovers.Count);
            _ = Task.Run(() => leftovers.ForEach(RawArchiveWriter.Compress), CancellationToken.None);
        }

        // Drains until the queue is completed, so snapshots already fetched still reach disk on shutdown.
        var writing = Task.Run(() => DrainQueue(writer), CancellationToken.None);
        try
        {
            await PollLoop(stoppingToken);
        }
        finally
        {
            _queue.Writer.Complete();
            await writing;
        }
    }

    async Task PollLoop(CancellationToken stoppingToken)
    {
        _lastStats = _clock.GetUtcNow();
        var consecutiveFailures = 0;
        using var timer = new PeriodicTimer(_options.PollInterval, _clock);
        do
        {
            try
            {
                await PollOnce(stoppingToken);
                consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _errors++;
                consecutiveFailures++;
                var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, consecutiveFailures)));
                // Metro Transit returns sporadic 404s while regenerating feeds; those don't need a stack trace.
                if (ex is HttpRequestException or TaskCanceledException)
                    _log.LogWarning("{Feed}: poll failed ({Failures} in a row): {Error}; backing off {Backoff}", _feed.Name, consecutiveFailures, ex.Message, backoff);
                else
                    _log.LogWarning(ex, "{Feed}: poll failed ({Failures} in a row), backing off {Backoff}", _feed.Name, consecutiveFailures, backoff);
                await Task.Delay(backoff, _clock, stoppingToken);
            }
            LogStatsIfDue();
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    async Task PollOnce(CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        var bytes = await _http.GetByteArrayAsync(_feed.Url, ct);
        var fetchedAt = _clock.GetUtcNow();
        var elapsed = _clock.GetElapsedTime(started);
        _fetched++;
        _fetchTime += elapsed;
        if (elapsed > _slowestFetch) _slowestFetch = elapsed;

        var headerTs = FeedHeader.ReadTimestamp(bytes)
            ?? throw new InvalidDataException($"{bytes.Length} byte response is not a GTFS-rt FeedMessage");
        if (headerTs != 0 && headerTs == _lastHeaderTimestamp)
        {
            _duplicates++;
            return;
        }
        _lastHeaderTimestamp = headerTs;

        var lag = fetchedAt - DateTimeOffset.FromUnixTimeSeconds((long)headerTs);
        if (headerTs != 0 && lag > _options.StaleAfter)
            _log.LogWarning("{Feed}: header timestamp is {Lag:F0}s old", _feed.Name, lag.TotalSeconds);

        _queue.Writer.TryWrite(new RawRecord(fetchedAt, bytes));
        _queued++;
    }

    async Task DrainQueue(RawArchiveWriter writer)
    {
        await foreach (var record in _queue.Reader.ReadAllAsync())
        {
            var started = _clock.GetTimestamp();
            try
            {
                writer.Append(record.FetchedAt, record.Payload);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeErrors);
                _log.LogError(ex, "{Feed}: archive write failed; snapshot from {FetchedAt:O} lost", _feed.Name, record.FetchedAt);
                continue;
            }
            var ticks = _clock.GetElapsedTime(started).Ticks;
            Interlocked.Increment(ref _archived);
            Interlocked.Add(ref _bytes, record.Payload.Length);
            if (ticks > Volatile.Read(ref _slowestAppendTicks))
                Volatile.Write(ref _slowestAppendTicks, ticks);
        }
    }

    void LogStatsIfDue()
    {
        var now = _clock.GetUtcNow();
        if (now - _lastStats < _options.StatsInterval) return;

        var archived = Interlocked.Exchange(ref _archived, 0);
        var bytes = Interlocked.Exchange(ref _bytes, 0);
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        var writeErrors = Interlocked.Exchange(ref _writeErrors, 0);
        var slowestAppend = TimeSpan.FromTicks(Interlocked.Exchange(ref _slowestAppendTicks, 0));
        var level = dropped > 0 || writeErrors > 0 ? LogLevel.Warning : LogLevel.Information;
        _log.Log(level,
            "{Feed}: {Fetched} fetched, {Queued} new, {Duplicates} duplicate, {Errors} fetch errors | {Archived} archived, {MB:F1} MB, {Dropped} dropped, {WriteErrors} write errors | last {Minutes:F1} min; fetch avg {AvgMs:F0} ms, max {MaxMs:F0} ms; append max {AppendMs:F0} ms; queue {Depth}",
            _feed.Name, _fetched, _queued, _duplicates, _errors, archived, bytes / 1e6, dropped, writeErrors, (now - _lastStats).TotalMinutes,
            _fetched == 0 ? 0 : _fetchTime.TotalMilliseconds / _fetched, _slowestFetch.TotalMilliseconds, slowestAppend.TotalMilliseconds, _queue.Reader.Count);
        _fetched = _queued = _duplicates = _errors = 0;
        _fetchTime = _slowestFetch = TimeSpan.Zero;
        _lastStats = now;
    }
}
