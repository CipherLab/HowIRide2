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

## Crossings

`src/Transit` turns vehicle pings into checkpoint crossings, the history every ETA model is trained on.

1. **Checkpoints** per (shape, stop list) pattern: every stop, plus a synthetic point every ~300 m where stops are more than 600 m apart.
   Stop distances come from `stop_times.shape_dist_traveled` (meters in Metro Transit's feed).
2. **Map matching** projects each ping onto its trip's shape, searching only a window around the last position so loops and parallel streets can't steal the match.
   Within 35 m of the shape moves the vehicle; 35-75 m is ignored as noise; beyond 75 m is off-route and flags the next crossing as a detour.
3. **Crossing times** are interpolated where consecutive pings straddle a boundary.
   Each stop has a ±30 m zone: entering it is arrival, leaving it is departure, the difference is dwell.
   Segment travel is departure from one zone to arrival at the next.

### Live: the `detect` service

`docker compose up -d` runs `detect` next to `ingest`.
It follows the VehiclePositions archive (closed hours, then the live file), runs the same detection, and writes `derived/crossings.db` (SQLite, WAL, safe to read while it runs).
It mounts `data/` read-only, so nothing it does can affect the recording.

- **Resume:** a cursor is stored with each snapshot's rows in one transaction. On restart it replays the previous 90 minutes without writing, so trips already under way keep their tracking state.
- **Static GTFS:** the newest `gtfs.zip` and `next/gtfs.zip` are both loaded and checked hourly, so trips from an upcoming pick resolve as soon as it's published.
- **Rebuild:** delete `derived/crossings.db` and restart; it re-derives everything from the archive.

Segment travel times, the raw material for the v0 model:

```sql
SELECT c.prev_checkpoint_id, c.checkpoint_id, c.arrive - p.depart AS seconds
FROM crossings c JOIN crossings p
  ON p.service_date = c.service_date AND p.trip_id = c.trip_id AND p.checkpoint_index = c.checkpoint_index - 1
WHERE c.detour = 0 AND c.arrive IS NOT NULL AND p.depart IS NOT NULL;
```

### Offline: replay to CSV

Replay recorded hours into a CSV:

```sh
dotnet run --project src/Replay -c Release -- <data-dir> 2026-10-27T16 2026-10-27T18 crossings.csv
REPLAY_TRACE=10,902 dotnet run ...   # also writes every ping on those routes to crossings.csv.trace.csv
```

One row per (trip, checkpoint): `arrive_unix` is empty at a trip's first stop, `depart_unix` at its last.
`*_gap_s` is the gap between the two pings a time was interpolated from; weight long gaps down.
The replay prints matcher health (off-route, ambiguous, and *lost* pings, which should stay near zero) and implied segment speeds.

## Backtest

Scores predictors against observed departures (the moment a bus leaves a stop's zone), by how far ahead they were predicting: 2, 5, 10, 15, 20 and 30 minutes.

```sh
dotnet run --project src/Backtest -c Release -- <data-dir> <crossings.db> 20261027 20261027 [per-route.csv]
```

- **Target is departure**, because that's what Metro Transit's TripUpdates publish (arrival times appear on ~2% of stop updates) and what a rider boards on. First and last stops and detoured crossings are excluded.
- **Only the latest snapshot counts.** At each horizon a predictor's answer is what the newest TripUpdates snapshot said; if the trip had dropped out of it, there is no prediction. A latest snapshot more than 60 s old is treated as an archive gap.
- **Contenders today:** the printed `schedule` and `metro-tripupdates`. New models implement `IPredictor` and are scored the same way.
- **Metrics:** coverage, median and p90 absolute error, bias (mean of predicted minus actual), share within ±1 and ±2 minutes, and the *early* rate: the bus left more than 60 s before the predicted time.

### Trips that haven't started

Most of the error on a trip that hasn't started is the guess of when it will, so that's learned rather than read off the timetable:

- **Usual start:** the median delay from the timetable at that first stop, by day type and 15-minute slot, from history.
- **Bus still out:** it can't start before the previous trip in its block finishes, plus a minute's layover.
- **Previous trip never seen,** 15 minutes past its start: cancelled or run by another bus, so the next trip stops waiting for it.
- **Overdue and unseen,** 5 minutes past the usual start: predicted as if it left on time, because tracking often begins a few stops in.

Only crossings knowable at the query time count as seen; a replay's store also holds later ones.

### Be there by

An estimate is a best guess, so about a third of buses leave before it.
Departures also show a time to be at the stop by, the estimate less a margin learned from `derived/live-predictions`: what riders were shown against what the bus then did, from the last 7 days.

- **By** whether the trip had started and the time shown. Bus-gone-first risk is 5% in each cell.
- **Routes** with enough samples can raise their margin but never lower it, because one good day isn't evidence a route is safer than the rest. So overall risk runs below 5%, at about 3%.
- **Risk here is strict:** the bus had left at all, with no minute's grace. Views after it had already gone don't count; nobody could have caught it.

The nightly run prints how yesterday's margins did on the day before relearning them, to `journalctl -u transit-backtest.service`, and saves `derived/be-by-margins.json`. The API reloads it hourly and offers no time until it exists.

### Nightly scoring

`deploy/transit-backtest.timer` scores yesterday at 04:30 and appends to `derived/backtest.db`, which feeds the Charts tab.
The container is limited to two cores and a 3 GB heap, and is the first thing killed if the node runs out of memory, so it can't cost a recording.
The heap cap is .NET's own: the Pi boots with `cgroup_disable=memory`, so Docker's memory limits don't apply there.

```sh
sudo cp deploy/transit-backtest.* /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now transit-backtest.timer
journalctl -u transit-backtest.service     # last night's scores
```

`--corrections` also scores `+cap` and `metro+median`, which means predicting every training day. It takes hours on the Pi, so run it elsewhere.

### What riders actually saw

The API writes what v1 is predicting to `derived/live-predictions` once a minute, in the raw archive format above: every stop of every trip due within 45 minutes, including trips that haven't started.
With `--live-log <dir>` the backtest scores that log as `live-v1`, by the same latest-sample rule as Metro's feed: a sample more than 90 s old at the query time counts as no prediction.

`v1-pace` is a replay that learns only from earlier days; `live-v1` is what was on screen, from tables that also include today so far. A gap between them means the replay isn't telling the truth about production.

## Live map and API

`docker compose up -d` also runs `api` on port 8080: open `http://<node>:8080` on the LAN.

The API follows the archive like `detect` does (its own detector instance for live positions, the TripUpdates feed for Metro Transit's predictions) and reads `crossings.db` hourly to rebuild the v0/v1 tables.
It serves a React app (`web/`) built into its `wwwroot`.

- **Live map:** every vehicle placed by distance along its real shape, replayed ~15 s behind the newest ping so it glides between GPS reports. Dot colour is today's pace on the road ahead; the ring is the route colour.
- **Stop:** zoom in and tap a stop, or open `#stop/<id>`. Each departure shows our estimate (amber dot), Metro Transit's (ring) and the timetable (tick) on one minutes-ahead strip.
- **Vehicle:** tap a bus or open `#vehicle/<id>` for its route line and next stops.
- **Scorecard:** `#scorecard`, the latest backtest by horizon plus live matcher health.

Endpoints: `/api/health`, `/api/vehicles`, `/api/vehicles/{id}`, `/api/stream` (server-sent events, one snapshot per feed update), `/api/shapes/{id}`, `/api/stops?bbox=s,w,n,e`, `/api/stops/{id}`, `/api/routes`, `/api/scores`.

Develop the UI with hot reload against a local API:

```sh
dotnet run --project src/Api -- --Api:DataDir=<data> --Api:DbPath=<crossings.db> --Urls=http://127.0.0.1:5199
cd web && npm install && npm run dev
```
