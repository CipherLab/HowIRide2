import { useQuery } from '@tanstack/react-query'
import { useSyncExternalStore } from 'react'
import { api, bestEstimate, type Score } from './api'
import {
  clock,
  Countdown,
  paceWords,
  RouteBadge,
  spanFor,
  Timeline,
  TimelineLegend,
  useNow,
  VsSchedule,
} from './components'
import type { LiveVehicles } from './live'
import { ARROW_PATH, ARROW_VIEWBOX, STOP_PATH, STOP_VIEWBOX } from './vehicleIcon'
import type { Selection } from './MapView'

const SOURCE_LABEL = { ours: 'our estimate', metro: "Metro Transit's estimate", schedule: 'timetable' }

export function useLive(live: LiveVehicles) {
  return useSyncExternalStore(
    (fn) => live.subscribe(fn),
    () => `${live.connected}:${live.count}:${live.lastMessageAt}`,
  )
}

export function LiveStatus({ live }: { live: LiveVehicles }) {
  useLive(live)
  useNow(1000)
  const stale = Date.now() - live.lastMessageAt > 30_000
  if (!live.connected || live.lastMessageAt === 0)
    return (
      <p className="status">
        <span className="dot off" />
        Connecting to the live feed…
      </p>
    )
  return (
    <p className="status">
      <span className={stale ? 'dot off' : 'dot'} />
      {live.count} vehicles · map shows {Math.round(live.behindBy())} s ago
    </p>
  )
}

export function Overview({ live }: { live: LiveVehicles }) {
  useLive(live)
  return (
    <>
      <div className="section">
        <p className="hint">Tap a bus or train to follow it, or zoom in and tap a stop to see what's coming.</p>
      </div>
      <div className="section">
        <h2>Vehicles, in their route's colour</h2>
        <div className="pace-key">
          <span className="arrow-row">
            <Arrow fill="#00539F" />
            <Arrow fill="#008144" />
            <Arrow fill="#F68B1F" />
          </span>
          <span>Blue, Green, Gold, Orange and Red lines</span>
          <span className="arrow-row">
            <Arrow fill="#7B2D8E" />
            <Arrow fill="#0E9AA7" />
            <Arrow fill="#D6338A" />
          </span>
          <span>Every other route has its own colour, different from the routes it shares streets with</span>
          <span className="swatch" style={{ background: '#A9B5BE', opacity: 0.6 }} />
          <span>Not in service</span>
        </div>
        <p className="hint" style={{ marginTop: 8 }}>
          Arrows point the way each vehicle is heading, with the route number on them when you zoom in. Tap one to follow it
          and see whether it's running slower than usual.
        </p>
      </div>
      <div className="section">
        <h2>Stops (zoom in to see them)</h2>
        <div className="pace-key">
          <StopSquare fill="#7B2D8E" size={12} />
          <span>Bus stop, in its route's colour</span>
          <StopSquare fill="#0053A0" size={16} />
          <span>Rail or rapid-line station, in the line's colour</span>
        </div>
        <p className="hint" style={{ marginTop: 8 }}>Zoom in further to see which routes stop at each one.</p>
      </div>
      <div className="section">
        <h2>How the map moves</h2>
        <p className="hint">
          Buses glide along their real route between GPS reports instead of jumping, so the map runs about a minute behind
          live. Times in the panels are computed for right now.
        </p>
      </div>
    </>
  )
}

function StopSquare({ fill, size }: { fill: string; size: number }) {
  return (
    <svg viewBox={STOP_VIEWBOX} width={size + 6} height={size + 6} aria-hidden="true">
      <path d={STOP_PATH} fill={fill} stroke={fill} strokeWidth="4" strokeLinejoin="round" />
    </svg>
  )
}

function Arrow({ fill }: { fill: string }) {
  return (
    <svg className="arrow-swatch" viewBox={ARROW_VIEWBOX} width="14" height="19" aria-hidden="true" style={{ transform: 'rotate(90deg)' }}>
      <path d={ARROW_PATH} fill={fill} stroke="#FFFFFF" strokeWidth="2" strokeLinejoin="round" />
    </svg>
  )
}

export function VehiclePanel({ id, live, onSelect }: { id: string; live: LiveVehicles; onSelect: (s: Selection) => void }) {
  const now = useNow()
  const q = useQuery({ queryKey: ['vehicle', id], queryFn: () => api.vehicle(id), refetchInterval: 10_000 })
  if (q.isPending) return <p className="empty">Loading vehicle {id}…</p>
  if (q.isError) return <p className="empty">Vehicle {id} is no longer reporting. Pick another on the map.</p>
  const { vehicle: v, nextStops } = q.data
  const span = spanFor(now, nextStops.flatMap((s) => [s.v1, s.metro, s.scheduled]))
  return (
    <>
      <div className="section">
        {v.route ? (
          <p className="hint">{paceWords(v.pace) ?? 'Waiting for enough recent trips to judge pace'}</p>
        ) : (
          <p className="hint">Vehicle {v.id} isn't assigned to a trip right now.</p>
        )}
      </div>
      {nextStops.length > 0 && (
        <>
          <div className="section">
            <h2>Next stops</h2>
            <TimelineLegend span={span} />
          </div>
          <ul className="rows">
            {nextStops.map((s) => {
              const best = bestEstimate(s)
              return (
                <li key={s.sequence}>
                  <button className="row" onClick={() => onSelect({ kind: 'stop', id: s.stopId })}>
                    <span className="what" style={{ gridColumn: '1 / 3' }}>
                      <strong>{s.name}</strong>
                      <small>
                        {best ? clock(best.time) : '–'} {best && <VsSchedule at={best.time} scheduled={s.scheduled} />}
                      </small>
                    </span>
                    <span className="when">{best && <Countdown at={best.time} now={now} />}</span>
                    <Timeline now={now} span={span} scheduled={s.scheduled} metro={s.metro} ours={s.v1 ?? s.v0} />
                  </button>
                </li>
              )
            })}
          </ul>
        </>
      )}
      <LiveFooter live={live} />
    </>
  )
}

export function StopPanel({ id, onSelect }: { id: string; onSelect: (s: Selection) => void }) {
  const now = useNow()
  const q = useQuery({ queryKey: ['stop', id], queryFn: () => api.stop(id), refetchInterval: 15_000 })
  if (q.isPending) return <p className="empty">Loading stop {id}…</p>
  if (q.isError) return <p className="empty">Couldn't load stop {id}. It may not be served today.</p>
  const { departures } = q.data
  const span = spanFor(now, departures.flatMap((d) => [d.v1, d.metro, d.scheduled]))
  return (
    <>
      <div className="section">
        <h2>Departing</h2>
        {departures.length > 0 ? <TimelineLegend span={span} /> : <p className="hint">Nothing due here in the next 90 minutes.</p>}
      </div>
      <ul className="rows">
        {departures.map((d) => {
          const best = bestEstimate(d)
          if (!best) return null
          return (
            <li key={d.tripId}>
              <button className="row" disabled={!d.vehicleId} onClick={() => d.vehicleId && onSelect({ kind: 'vehicle', id: d.vehicleId })}>
                <RouteBadge route={d.route} color={d.color} textColor={d.textColor} />
                <span className="what">
                  <strong>{d.headsign}</strong>
                  <small>
                    {clock(best.time)} · <VsSchedule at={best.time} scheduled={d.scheduled} />
                    {best.source !== 'ours' && ` · ${SOURCE_LABEL[best.source]}`}
                  </small>
                </span>
                <span className="when">
                  <Countdown at={best.time} now={now} />
                </span>
                <Timeline now={now} span={span} scheduled={d.scheduled} metro={d.metro} ours={d.v1 ?? d.v0} />
              </button>
            </li>
          )
        })}
      </ul>
    </>
  )
}

function LiveFooter({ live }: { live: LiveVehicles }) {
  useLive(live)
  return null
}

/** What the app shows (OURS) against what Metro's own app shows (METRO), with the timetable for scale. */
const OURS = 'v1-pace'
const METRO = 'metro-tripupdates'
const SCHEDULE = 'schedule'

const PREDICTOR_LABEL: Record<string, string> = {
  [OURS]: 'Ours (in the app)',
  [METRO]: 'Metro Transit',
  [SCHEDULE]: 'Timetable',
  'v0-medians': 'Ours, older model',
  'v1-pace+cap': 'Ours, shifted*',
  'v0-medians+cap': 'Older, shifted*',
  'metro-tripupdates+cap': 'Metro, shifted*',
}

/** Differences smaller than this are noise between test days; call them even. */
const TIE_S = 5

export function Scorecard() {
  const scores = useQuery({ queryKey: ['scores'], queryFn: api.scores, refetchInterval: 300_000 })
  const health = useQuery({ queryKey: ['health'], queryFn: api.health, refetchInterval: 10_000 })
  const runs = scores.data?.runs ?? []
  const latest = runs.length ? runs[runs.length - 1].runAt : null
  const rows = runs.filter((r) => r.runAt === latest && r.group === 'bus')
  const horizons = [...new Set(rows.map((r) => r.horizon))].sort((a, b) => a - b)
  const predictors = Object.keys(PREDICTOR_LABEL).filter((p) => rows.some((r) => r.predictor === p))
  const cell = (p: string, h: number) => rows.find((r) => r.predictor === p && r.horizon === h)
  const h = health.data
  return (
    <>
      <div className="section">
        <h2>Ours vs Metro Transit, buses</h2>
        {rows.length === 0 ? (
          <p className="hint">No scores yet. The nightly backtest writes them after each service day.</p>
        ) : (
          <HeadToHead rows={rows} horizons={horizons} />
        )}
      </div>
      {rows.length > 0 && (
        <div className="section">
          <details className="more">
            <summary>Every predictor we test</summary>
            <p className="hint">
              Typical miss in minutes. Highlighted: closest in each column. “Left early” is how often the bus left more than a
              minute before the predicted time, 10 minutes out. *Shifted: nudged by the typical miss on earlier days; still
              being tuned.
            </p>
            <table className="scores">
              <thead>
                <tr>
                  <th>Predictor</th>
                  {horizons.map((h) => (
                    <th key={h}>{h} min</th>
                  ))}
                  <th>Left early</th>
                </tr>
              </thead>
              <tbody>
                {predictors.map((p) => (
                  <tr key={p} className={p === OURS || p === METRO ? 'headline' : undefined}>
                    <td>{PREDICTOR_LABEL[p]}</td>
                    {horizons.map((h) => {
                      const s = cell(p, h)
                      const best = Math.min(...predictors.map((q) => cell(q, h)?.medianAbs ?? Infinity))
                      return (
                        <td key={h} className={s?.medianAbs === best ? 'best' : undefined}>
                          {s?.medianAbs != null ? (s.medianAbs / 60).toFixed(1) : '–'}
                        </td>
                      )
                    })}
                    <td>{fmtPct(cell(p, 10)?.early)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </details>
        </div>
      )}
      <div className="section">
        <h2>Right now</h2>
        {h ? (
          <div className="stats">
            <div className="stat">
              <b>{h.vehiclesOnTrip}</b>
              <span>vehicles tracked on a trip, of {h.vehicles}</span>
            </div>
            <div className="stat">
              <b>{h.vehicleLagSeconds != null ? `${Math.round(h.vehicleLagSeconds)} s` : '–'}</b>
              <span>since the last position update</span>
            </div>
            <div className="stat">
              <b>{h.model.segments.toLocaleString()}</b>
              <span>road segments with history</span>
            </div>
            <div className="stat">
              <b>{h.detector ? `${((100 * h.detector.offRoute) / Math.max(1, h.detector.used)).toFixed(1)}%` : '–'}</b>
              <span>of GPS reports off route (detours, layovers)</span>
            </div>
          </div>
        ) : (
          <p className="hint">Loading…</p>
        )}
      </div>
    </>
  )
}

function fmtPct(v: number | null | undefined) {
  return v == null ? '–' : `${(v * 100).toFixed(0)}%`
}

function fmtMinutes(seconds: number) {
  return `${(seconds / 60).toFixed(1)} min`
}

/** The backtest records service dates (yyyyMMdd, inclusive) or, for a split run, the instant the test began. */
function testWindow(from: string, to: string) {
  const fmt = (v: string) =>
    /^\d{8}$/.test(v)
      ? new Date(+v.slice(0, 4), +v.slice(4, 6) - 1, +v.slice(6, 8)).toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' })
      : new Date(v).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
  if (from === to) return `on ${fmt(from)}`
  return /^\d{8}$/.test(from) ? `${fmt(from)} through ${fmt(to)}` : `from ${fmt(from)} through the end of that service day`
}

function listMinutes(hs: number[]) {
  return hs.length > 1 ? `${hs.slice(0, -1).join(', ')} and ${hs[hs.length - 1]} min` : `${hs[0]} min`
}

/**
 * One row per horizon: how far off our estimate and Metro's typically were, as dots in two lanes on a shared
 * minute scale (so a tie shows as one dot above the other), with the timetable's miss as a tick for reference.
 */
function HeadToHead({ rows, horizons }: { rows: Score[]; horizons: number[] }) {
  const at = (p: string, h: number) => rows.find((r) => r.predictor === p && r.horizon === h)?.medianAbs ?? null
  const pairs = horizons
    .map((h) => ({ h, ours: at(OURS, h), metro: at(METRO, h), schedule: at(SCHEDULE, h) }))
    .filter((r): r is { h: number; ours: number; metro: number; schedule: number | null } => r.ours != null && r.metro != null)
  if (pairs.length === 0) return <p className="hint">This run has no scores for our estimate or Metro’s.</p>

  const maxMinutes = Math.ceil(Math.max(...pairs.flatMap((p) => [p.ours, p.metro, p.schedule ?? 0])) / 60)
  const pos = (seconds: number) => `${(100 * seconds) / (maxMinutes * 60)}%`
  const oursCloser = pairs.filter((p) => p.ours < p.metro - TIE_S).map((p) => p.h)
  const metroCloser = pairs.filter((p) => p.metro < p.ours - TIE_S).map((p) => p.h)
  const verdict =
    oursCloser.length === 0 && metroCloser.length === 0
      ? `Even. At every distance, ours and Metro’s typical miss are within ${TIE_S} seconds of each other.`
      : [
          oursCloser.length ? `Ours is closer ${listMinutes(oursCloser)} out.` : '',
          metroCloser.length ? `Metro’s is closer ${listMinutes(metroCloser)} out.` : '',
          oursCloser.length + metroCloser.length < pairs.length ? `Elsewhere they’re within ${TIE_S} seconds.` : '',
        ]
          .filter(Boolean)
          .join(' ')

  const early = (p: string) => rows.find((r) => r.predictor === p && r.horizon === 10)?.early ?? null
  const oursEarly = early(OURS)
  const metroEarly = early(METRO)
  const sample = rows.find((r) => r.predictor === OURS)

  return (
    <>
      <p className="verdict">{verdict}</p>
      <div className="legend h2h-legend">
        <span>
          <i className="dot ours" /> Ours
        </span>
        <span>
          <i className="dot metro" /> Metro Transit
        </span>
        <span>
          <i className="tick" /> Timetable
        </span>
      </div>
      <div className="h2h" role="table" aria-label="Typical miss by how far ahead the estimate was made">
        <div className="h2h-row h2h-head" role="row">
          <span role="columnheader">Ahead</span>
          <span role="columnheader">Typical miss</span>
          <span role="columnheader">Ours</span>
          <span role="columnheader">Metro</span>
        </div>
        {pairs.map((p) => {
          const diff = p.ours - p.metro
          const even = Math.abs(diff) <= TIE_S
          return (
            <div className="h2h-row" role="row" key={p.h}>
              <span role="rowheader">{p.h} min</span>
              <span
                className="track"
                role="cell"
                aria-label={`Ours ${fmtMinutes(p.ours)}, Metro ${fmtMinutes(p.metro)}${p.schedule != null ? `, timetable ${fmtMinutes(p.schedule)}` : ''}`}
              >
                {p.schedule != null && <i className="tick" style={{ left: pos(p.schedule) }} />}
                <i className="dot metro" style={{ left: pos(p.metro) }} />
                <i className="dot ours" style={{ left: pos(p.ours) }} />
              </span>
              <span role="cell" className={!even && diff < 0 ? 'better' : undefined}>
                {(p.ours / 60).toFixed(1)}
              </span>
              <span role="cell" className={!even && diff > 0 ? 'better' : undefined}>
                {(p.metro / 60).toFixed(1)}
              </span>
            </div>
          )
        })}
        <div className="h2h-row h2h-axis" aria-hidden="true">
          <span />
          <span className="track">
            {Array.from({ length: maxMinutes + 1 }, (_, m) => (
              <small key={m} style={{ left: pos(m * 60) }}>
                {m}
              </small>
            ))}
          </span>
          <span className="axis-unit">min off</span>
        </div>
      </div>
      {oursEarly != null && metroEarly != null && (
        <>
          <h3 className="sub">Bus gone before the time shown</h3>
          <p className="hint">
            The miss that strands you: how often the bus left more than a minute before the predicted time, 10 minutes out.
          </p>
          <div className="early-bars">
            {[
              { label: 'Ours', v: oursEarly, cls: 'ours' },
              { label: 'Metro Transit', v: metroEarly, cls: 'metro' },
            ].map((b) => (
              <div key={b.cls} className="early-row">
                <span>{b.label}</span>
                <span className="bar-track">
                  <i className={`bar ${b.cls}`} style={{ width: `${(100 * b.v) / Math.max(oursEarly, metroEarly, 0.01)}%` }} />
                </span>
                <b>{fmtPct(b.v)}</b>
              </div>
            ))}
          </div>
          {oursEarly > metroEarly + 0.02 && (
            <p className="hint">Ours tells people they have longer than they do more often than Metro’s does. That’s the next thing to fix.</p>
          )}
        </>
      )}
      {sample && (
        <p className="fineprint">
          Typical miss is the median: half the estimates were closer than this. Tested on {sample.predicted.toLocaleString()} bus
          predictions at each distance, {testWindow(sample.testFrom, sample.testTo)}.
        </p>
      )}
    </>
  )
}
