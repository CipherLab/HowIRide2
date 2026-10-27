# transit-eta

Segment-based bus ETAs for Metro Transit, predicted from live GPS and recorded history rather than the timetable.

There is no public archive of realtime history, so step one is recording it. That's all `src/Ingest` does today.

## Ingest

Polls the three GTFS-realtime feeds every 5 s and archives every snapshot whose header timestamp is new.
Also keeps a versioned copy of every static `gtfs.zip` and `next/gtfs.zip` it sees.

The bytes are stored as received, not parsed, so a bug in crossing detection is fixed by replaying the archive rather than lost.

```
data/
  raw/{feed}/{yyyy-MM-dd}/{HH}.pbs       current UTC hour, appended live
  raw/{feed}/{yyyy-MM-dd}/{HH}.pbs.zst   closed hours, zstd level 19, 128 MB long window
  static/{feed}/{Last-Modified}.zip      one file per published GTFS version
```

Record format: `[int64 BE fetched-at unix ms][int32 BE length][GTFS-rt FeedMessage bytes]`, repeated.
`RawArchiveReader.Read(path)` reads either form and stops cleanly at a record torn by a crash.
From the shell: `zstd -d --long=27 -c 16.pbs.zst > 16.pbs`.

### Run

```sh
dotnet run --project src/Ingest                  # writes ./src/Ingest/data
docker compose up -d --build                      # on the Pi; writes ./data
```

Settings live in `src/Ingest/appsettings.json` and can be overridden by env vars (`Ingest__DataDir=/mnt/ssd/transit`).

### What to expect

Measured 2026-10-27, midday weekday:

| Feed | New snapshot every | Size per snapshot | Compressed per day (est.) |
|---|---|---|---|
| tripupdates | ~10 s | ~900 KB | ~0.5 GB |
| vehiclepositions | ~10 s | ~67 KB | ~0.07 GB |
| alerts | ~50 s | ~120 KB | small |

The current hour is uncompressed (~330 MB for tripupdates) before it rolls over.
Put `data/` on an SSD rather than the Pi's SD card.

Metro Transit returns sporadic 404s while regenerating feeds; the poller logs them and backs off.
