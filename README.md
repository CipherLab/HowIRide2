# HowIRide2

Segment-based arrival predictions for Metro Transit (Minneapolis–St. Paul), learned from live GPS and recorded history rather than the timetable.

It records the agency's realtime feeds, works out when each vehicle actually passed each point on its route, predicts departures from that history, and scores every prediction against what the bus then did.
Riders get a live map, per-stop departures, and a "be there by" time with a measured risk of missing the bus.

The code calls itself `transit-eta`: that's the solution, the `Transit` library, and the directory the deploy unit expects.

<!-- TOC -->
- [How it works](#how-it-works)
- [Repository layout](#repository-layout)
- [Getting started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Run the stack](#run-the-stack)
  - [What works when](#what-works-when)
- [Configuration](#configuration)
- [Ingest](#ingest)
- [Crossings](#crossings)
- [Prediction models](#prediction-models)
  - [Trips that haven't started](#trips-that-havent-started)
  - [Be there by](#be-there-by)
- [Backtest](#backtest)
  - [Scoring rules](#scoring-rules)
  - [Results so far](#results-so-far)
  - [Nightly scoring](#nightly-scoring)
  - [What riders actually saw](#what-riders-actually-saw)
- [Live map and API](#live-map-and-api)
- [Development](#development)
- [Deployment notes](#deployment-notes)
<!-- /TOC -->

## How it works

```mermaid
flowchart LR
    feeds[Metro Transit<br>GTFS and GTFS-realtime] --> ingest
    ingest --> archive[(data/<br>raw snapshots,<br>static GTFS)]
    archive --> detect
    detect --> crossings[(derived/<br>crossings.db)]
    archive --> api
    crossings --> api
    api --> web[Map, stops,<br>scorecard]
    api --> log[(derived/<br>live-predictions)]
    archive --> backtest
    crossings --> backtest
    log --> backtest
    backtest --> scores[(derived/<br>backtest.db)]
    backtest --> margins[(derived/<br>be-by-margins.json)]
    scores --> api
    margins --> api
```

There is no public archive of realtime history, so recording it comes first.
Everything else is derived from that recording and can be rebuilt from it.

| Component | Runs as | Job |
|---|---|---|
| `ingest` | Always-on service | Archives the feeds byte for byte. Nothing else may slow it down or take it out. |
| `detect` | Always-on service | Turns vehicle pings into checkpoint crossings. Mounts `data/` read-only, so it can't damage the recording. |
| `api` | Always-on service, port 8080 | Serves live positions, predictions and the web app, and logs what it predicted. |
| `backtest` | Nightly timer or on demand | Scores predictors against observed departures and relearns the "be there by" margins. |
| `detect-grid` | Off by default | Experiment: the same detection on a shared map grid. A week in, it scored worse than the default layout. |
| `replay` | Local tool | Replays recorded hours into a CSV for inspection. |

## Repository layout

| Path | Contents |
|---|---|
| [`src/Ingest`](src/Ingest) | Feed poller and static GTFS watcher |
| [`src/Detect`](src/Detect) | Worker that writes `crossings.db` |
| [`src/Api`](src/Api) | ASP.NET Core API, and host for the web app |
| [`src/Backtest`](src/Backtest) | Scoring command line |
| [`src/Replay`](src/Replay) | Archive-to-CSV command line |
| [`src/Transit`](src/Transit) | Shared library: archive format, GTFS, map matching, crossing detection, prediction models, scoring |
| [`web`](web) | React, MapLibre and Vite front end |
| [`tests`](tests) | xUnit suites for `Ingest` and `Transit` |
| [`deploy`](deploy) | systemd unit and timer for the nightly backtest |
| [`proto`](proto) | The upstream GTFS-realtime schema, under its own Apache 2.0 licence |
| [`docs/superpowers`](docs/superpowers) | Design notes and plans for individual features |

`data/` and `derived/` are created at run time and are not in the repository.

## Getting started

### Prerequisites

- Docker with Compose, to run the stack.
- The .NET 10 SDK and Node 22, to develop or run tools outside Docker.
- `zstd`, only to inspect closed archive hours from the shell.
- An SSD for `data/`: see [the expected volume](#what-to-expect).

The feeds are public and need no API key.

### Run the stack

```sh
git clone https://github.com/CipherLab/HowIRide2 transit-eta
cd transit-eta

docker compose up -d --build ingest    # start recording and fetch the static GTFS
ls data/static/metro-gtfs              # wait until a .zip appears
docker compose up -d --build           # add detect and api
```

Then open `http://<node>:8080`.
`detect` and `api` read the static GTFS that `ingest` downloads, which is why `ingest` goes first.

`docker compose run --rm -T backtest` scores yesterday on demand.
`docker compose --profile experiments up -d detect-grid` starts the grid experiment.

### What works when

The project learns from its own recording, so a new install starts with little to show.

| After | You get |
|---|---|
| Minutes | Live vehicles on the map, and Metro Transit's predictions and the timetable at each stop. |
| Days | The first useful estimates: a day type and 15-minute slot needs 3 samples before its median is trusted, and sparser cells fall back to broader ones. |
| A first night's backtest | The scorecard and charts. |
| A night's backtest with a live prediction log | "Be there by" times. None are offered until `derived/be-by-margins.json` exists. |

History builds up per day type, so Saturdays and Sundays take weeks to catch up with weekdays.

## Configuration

Each service reads its section of `appsettings.json`, overridable by environment variable (`Ingest__DataDir=/mnt/ssd/transit`) or, under `dotnet run`, by argument (`--Api:DbPath=<path>`).

| Setting | Default | Meaning |
|---|---|---|
| `Ingest:DataDir` | `data` | Root of the recording |
| `Ingest:PollInterval` | 5 s | How often each realtime feed is fetched |
| `Ingest:StaticCheckInterval` | 6 h | How often static GTFS is checked for a new version |
| `Ingest:Realtime`, `Ingest:Static` | Metro Transit's feeds | Feed names and URLs; the name is also the directory on disk |
| `Detect:DataDir` | `data` | Recording to follow, read-only |
| `Detect:DbPath` | `derived/crossings.db` | Database it writes |
| `Detect:PollInterval` | 2 s | How often it looks for new records |
| `Api:DataDir`, `Api:DbPath`, `Api:BacktestDbPath` | Under `data` and `derived` | Inputs |
| `Api:HistoryDays` | 14 | Days of crossings the live models learn from |
| `Api:PredictionLogDir` | Set in `docker-compose.yml` | Where to log what riders were shown |
| `Api:BeByMarginsPath` | Set in `docker-compose.yml` | Margins learned by the nightly backtest |
| `TRANSIT_CHECKPOINTS` | `spaced` | Checkpoint layout, `spaced` or `grid`. Every process reading a crossings database must use the mode that wrote it. |

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

To run it alone, `dotnet run --project src/Ingest` writes to `./src/Ingest/data`.

### What to expect

Measured 2026-10-27, midday weekday:

| Feed | New snapshot every | Size per snapshot | Compressed per day (est.) |
|---|---|---|---|
| tripupdates | ~10 s | ~900 KB | ~0.5 GB |
| vehiclepositions | ~10 s | ~67 KB | ~0.07 GB |
| alerts | ~50 s | ~120 KB | small |

The current hour is uncompressed (~330 MB for tripupdates) before it rolls over.
Put `data/` on an SSD rather than a Raspberry Pi's SD card.
Nothing prunes the archive, so it keeps growing until you remove old hours yourself.

Metro Transit returns sporadic 404s while regenerating feeds; the poller logs them and backs off.
`next/gtfs.zip` also returns 404 for most of the week, which is expected.

## Crossings

`src/Transit` turns vehicle pings into checkpoint crossings, the history every model is trained on.

1. **Checkpoints** per (shape, stop list) pattern: every stop, plus a synthetic point every ~300 m where stops are more than 600 m apart.
   Stop distances come from `stop_times.shape_dist_traveled` (meters in Metro Transit's feed).
2. **Map matching** projects each ping onto its trip's shape, searching only a window around the last position so loops and parallel streets can't steal the match.
   Within 35 m of the shape moves the vehicle; 35-75 m is ignored as noise; beyond 75 m is off-route and flags the next crossing as a detour.
3. **Crossing times** are interpolated where consecutive pings straddle a boundary.
   Each stop has a ±30 m zone: entering it is arrival, leaving it is departure, the difference is dwell.
   Segment travel is departure from one zone to arrival at the next.

### Live: the `detect` service

`detect` follows the VehiclePositions archive (closed hours, then the live file), runs the detection, and writes `derived/crossings.db` (SQLite, WAL, safe to read while it runs).

- **Resume:** a cursor is stored with each snapshot's rows in one transaction. On restart it replays the previous 90 minutes without writing, so trips already under way keep their tracking state.
- **Static GTFS:** the newest `gtfs.zip` and `next/gtfs.zip` are both loaded and checked hourly, so trips from an upcoming pick resolve as soon as it's published.
- **Rebuild:** delete `derived/crossings.db` and restart; it re-derives everything from the archive.

Segment travel times, the raw material for the models:

```sql
SELECT c.prev_checkpoint_id, c.checkpoint_id, c.arrive - p.depart AS seconds
FROM crossings c JOIN crossings p
  ON p.service_date = c.service_date AND p.trip_id = c.trip_id AND p.checkpoint_index = c.checkpoint_index - 1
WHERE c.detour = 0 AND c.arrive IS NOT NULL AND p.depart IS NOT NULL;
```

### Offline: replay to CSV

```sh
dotnet run --project src/Replay -c Release -- <data-dir> 2026-10-27T16 2026-10-27T18 crossings.csv
REPLAY_TRACE=10,902 dotnet run ...   # also writes every ping on those routes to crossings.csv.trace.csv
```

One row per (trip, checkpoint): `arrive_unix` is empty at a trip's first stop, `depart_unix` at its last.
`*_gap_s` is the gap between the two pings a time was interpolated from; weight long gaps down.
The replay prints matcher health (off-route, ambiguous, and *lost* pings, which should stay near zero) and implied segment speeds.

## Prediction models

Both models predict a departure from the same walk along the trip, in [`SegmentEtaPredictor`](src/Transit/Eta/SegmentEtaPredictor.cs).
They start at the bus's last known departure and add, for each segment still ahead, its travel time plus the dwell at each stop, including the target stop's own dwell.

- **`v0-medians`** uses historical medians by segment, day type and 15-minute slot.
  A sparse cell falls back to nearby slots, then the whole day type, then all days.
  A segment with no history at all assumes 7 m/s and 8 s of dwell.
  Holidays aren't distinguished yet and count as their weekday.
- **`v1-pace`** scales those medians by [today's pace](src/Transit/Eta/RecentPace.cs): how traversals finished in the last 30 minutes compare with their own medians, on that segment and its neighbours within 1.5 km.
  Snow, a crash or a game letting out show up here without being modeled.
  With little evidence it assumes a normal day, and the effect fades with distance ahead of the bus.
  It also applies the rules below for trips that haven't started.

The live API serves v1, relearning its tables hourly from the last 14 days of crossings.
A crossing counts only from when an app could first have known it, one ping interval plus the feed's lag after it happened, so a backtest can't see the future.

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

The nightly run prints how yesterday's margins did on the day before relearning them, to `journalctl -u transit-backtest.service`, and saves `derived/be-by-margins.json`.
The API reloads it hourly and offers no time until it exists.

## Backtest

Scores predictors against observed departures (the moment a bus leaves a stop's zone), by how far ahead they were predicting: 2, 5, 10, 15, 20 and 30 minutes.

```sh
dotnet run --project src/Backtest -c Release -- --data <data-dir> --db <crossings.db> --test 20261027
```

| Option | Effect |
|---|---|
| `--test <from>[..<to>]` | Service dates as `yyyyMMdd`, or an ISO instant to test everything after it |
| `--yesterday` | Yesterday's service date in the agency's time zone, instead of `--test` |
| `--train-days <n>` | Days of history before the test window to learn from (default 14) |
| `--store <path>` | Cache of replayed predictions and score history (default `backtest.db` beside `--db`) |
| `--routes-csv <path>` | Also write every score broken down by route |
| `--learning-curve` | Also score v1 with tables from 25% and 50% of the training trips |
| `--corrections` | Also fit and score `+cap` and `metro+median`. This predicts every training day and takes hours on a Pi, so run it elsewhere. |
| `--live-log <dir>` | Also score what the live API predicted |
| `--fit-margins <path>` | With `--live-log`, relearn the "be there by" margins |
| `--checkpoint-report` | Just print how many road segments each checkpoint layout shares between routes |

### Scoring rules

- **Target is departure**, because that's what Metro Transit's TripUpdates publish (arrival times appear on ~2% of stop updates) and what a rider boards on. First and last stops and detoured crossings are excluded.
- **Only the latest snapshot counts.** At each horizon a predictor's answer is what the newest TripUpdates snapshot said; if the trip had dropped out of it, there is no prediction. A latest snapshot more than 60 s old is treated as an archive gap.
- **Contenders:** the printed `schedule`, `metro-tripupdates`, `v0-medians`, `v1-pace`, and `live-v1` with `--live-log`. New models implement `IPredictor` and are scored the same way.
- **Metrics:** coverage, median and p90 absolute error, bias (mean of predicted minus actual), share within ±1 and ±2 minutes, and the *early* rate: the bus left more than 60 s before the predicted time.

### Results so far

These are early numbers from about a week of history, as of 2026-11-05.

Median absolute error on the first full live days, buses:

| Ahead | v1, live | v1, replay | Metro Transit |
|---|---|---|---|
| 10 min | 66-67 s | 64-65 s | 68-69 s |
| 30 min | ~114 s | ~114 s | 133-137 s |

Better medians are not the whole story, because v1 is more likely to send the bus off before the time it shows.
In a replay of Monday 2 November, by the lead time shown to the rider:

| Shown | v1 early rate | Metro Transit early rate |
|---|---|---|
| ~10 min | 12% | 5% |
| ~15 min | 18% | 6% |
| ~30 min | 25% | 7% |

The "be there by" time is the answer to that: on a held-out weekday the bus was gone first for 2.7% of views, against 32.6% for the estimate alone.
Its margins were fitted on weekdays only, so weekends are untested.

Be careful reading a trend into these.
Much of the first week's gain arrived in a single step, when weekday cells reached their 3 samples, rather than from steady learning.
To compare fairly, hold the test day and hours fixed, use Metro Transit as the control for how hard the day was, and report the early rate and p90 alongside the median.

### Nightly scoring

`deploy/transit-backtest.timer` scores yesterday at 04:30 Central and appends to `derived/backtest.db`, which feeds the scorecard and charts.

```sh
sudo cp deploy/transit-backtest.* /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now transit-backtest.timer
journalctl -u transit-backtest.service     # last night's scores
```

The unit's `WorkingDirectory` is `/home/admin/transit-eta`; change it if the checkout lives elsewhere.

The container is limited to two cores and a 3 GB heap, and is the first thing killed if the node runs out of memory, so it can't cost a recording.

### What riders actually saw

The API writes what v1 is predicting to `derived/live-predictions` once a minute, in the raw archive format above: every stop of every trip due within 45 minutes, including trips that haven't started.
With `--live-log <dir>` the backtest scores that log as `live-v1`, by the same latest-sample rule as Metro's feed: a sample more than 90 s old at the query time counts as no prediction.

`v1-pace` is a replay that learns only from earlier days; `live-v1` is what was on screen, from tables that also include today so far.
A gap between them means the replay isn't telling the truth about production.

## Live map and API

The API follows the archive like `detect` does (its own detector instance for live positions, the TripUpdates feed for Metro Transit's predictions) and reads `crossings.db` hourly to rebuild the v0/v1 tables.
It serves a React app (`web/`) built into its `wwwroot`.

- **Live map:** every vehicle placed by distance along its real shape, replayed ~15 s behind the newest ping so it glides between GPS reports. Dot colour is today's pace on the road ahead; the ring is the route colour.
- **Stop:** zoom in and tap a stop, or open `#stop/<id>`. Each departure shows our estimate (amber dot), Metro Transit's (ring) and the timetable (tick) on one minutes-ahead strip.
- **Vehicle:** tap a bus or open `#vehicle/<id>` for its route line and next stops.
- **Watching a stop:** the search box at the top right finds a stop by route, name or number, with your recent picks and nearby routes. It then shows that stop and the next two buses on one route and direction, each with its time riding on it, framed automatically until you drag or zoom. Open `#watch/<stop>/<route>/<direction>` directly to save one.
- **Scorecard:** `#scorecard`, the latest backtest by horizon plus live matcher health.
- **Charts:** `#charts`, score history from the nightly backtest, overall and route by route.

| Endpoint | Returns |
|---|---|
| `GET /api/health` | Live matcher and feed health |
| `GET /api/vehicles`, `/api/vehicles/{id}` | Current vehicles, or one with its next stops |
| `GET /api/stream` | Server-sent events, one snapshot per feed update |
| `GET /api/routes`, `/api/routes/{id}/directions` | Routes and their directions |
| `GET /api/shapes/{id}` | A route shape |
| `GET /api/stops?bbox=s,w,n,e` | Stops in a bounding box |
| `GET /api/stops/search?q=` | Stops by route, name or number |
| `GET /api/stops/{id}` | Departures; add `?route=<id>&direction=<n>` to filter |
| `GET /api/stops/{id}/lines` | The route and direction pairs serving a stop |
| `GET /api/scores`, `/api/scores/routes` | Backtest scores, overall and by route |

## Development

Develop the UI with hot reload against a local API:

```sh
dotnet run --project src/Api -- --Api:DataDir=<data> --Api:DbPath=<crossings.db> --Urls=http://127.0.0.1:5199
cd web && npm install && npm run dev
```

Vite proxies `/api` to port 5199; set `API_URL` to point it elsewhere.

| Command | Checks |
|---|---|
| `dotnet test TransitEta.slnx` | Archive format, map matching, crossing detection, models, scoring and API state |
| `npm test` in `web/` | The rules for what the screen shows |
| `npm run lint` in `web/` | oxlint |
| `npm run build` in `web/` | Type-checks and builds into `src/Api/wwwroot` |

New predictors implement `IPredictor` in [`src/Transit/Backtest`](src/Transit/Backtest) and are added in [`src/Backtest/Program.cs`](src/Backtest/Program.cs).

## Deployment notes

The stack is built to run on a small always-on node, and runs on a Raspberry Pi CM4.
The Dockerfiles build natively on arm64, or with `docker buildx build --platform linux/arm64`.

- **Update without risking the recording:** run `docker compose build api`, then `docker compose up -d --no-deps api`. `ingest` and `detect` keep running, and a failed build leaves the old version up.
- **Memory limits:** a Pi that boots with `cgroup_disable=memory` ignores Docker's memory limits, so the backtest's heap is capped by .NET itself (`DOTNET_GCHeapHardLimit`) and `oom_score_adj` makes it the first thing killed.
- **Reading live databases:** copy `crossings.db` with the SQLite backup API rather than as a file, because it's in WAL mode.
- **Exposure:** the API has no authentication and serves plain HTTP on port 8080, so keep it on a LAN or behind a reverse proxy. TLS isn't part of this repository.
