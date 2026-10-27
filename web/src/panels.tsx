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

const PREDICTOR_LABEL: Record<string, string> = {
  schedule: 'Timetable',
  'metro-tripupdates': 'Metro Transit',
  'metro-tripupdates+cap': 'Metro, corrected',
  'metro+median': 'Metro, median shift',
  'v0-medians': 'v0 medians',
  'v0-medians+cap': 'v0, capped',
  'v1-pace': 'v1 pace',
  'v1-pace+cap': 'v1, capped',
}

const CHART_COLORS: Record<string, string> = {
  schedule: '#7A8A96',
  'metro-tripupdates': '#14212B',
  'v1-pace': '#F2A900',
  'v0-medians': '#2E8BC0',
}

export function Scorecard() {
  const scores = useQuery({ queryKey: ['scores'], queryFn: api.scores, refetchInterval: 300_000 })
  const health = useQuery({ queryKey: ['health'], queryFn: api.health, refetchInterval: 10_000 })
  const runs = scores.data?.runs ?? []
  const latest = runs.length ? runs[runs.length - 1].runAt : null
  const rows = runs.filter((r) => r.runAt === latest && r.group === 'bus')
  const horizons = [...new Set(rows.map((r) => r.horizon))].sort((a, b) => a - b)
  const predictors = [...new Set(rows.map((r) => r.predictor))].filter((p) => p !== 'metro+median')
  const cell = (p: string, h: number) => rows.find((r) => r.predictor === p && r.horizon === h)
  const h = health.data
  return (
    <>
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
      <div className="section">
        <h2>Typical error by how far ahead, buses</h2>
        {rows.length === 0 ? (
          <p className="hint">No scores yet. The nightly backtest writes them after each service day.</p>
        ) : (
          <>
            <p className="hint">
              Median minutes off, latest run (test {rows[0].testFrom.slice(0, 16)}). Highlighted: best in each column. Early is the
              share of buses that left more than a minute before the predicted time.
            </p>
            <ErrorChart rows={rows} horizons={horizons} />
            <table className="scores">
              <thead>
                <tr>
                  <th>Predictor</th>
                  {horizons.map((h) => (
                    <th key={h}>{h} min</th>
                  ))}
                  <th>Early at 10 min</th>
                </tr>
              </thead>
              <tbody>
                {predictors.map((p) => (
                  <tr key={p}>
                    <td>{PREDICTOR_LABEL[p] ?? p}</td>
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
          </>
        )}
      </div>
    </>
  )
}

function fmtPct(v: number | null | undefined) {
  return v == null ? '–' : `${(v * 100).toFixed(0)}%`
}

function ErrorChart({ rows, horizons }: { rows: Score[]; horizons: number[] }) {
  const W = 360
  const H = 170
  const pad = { l: 30, r: 10, t: 10, b: 22 }
  const charted = rows.filter((r) => r.predictor in CHART_COLORS && r.medianAbs != null)
  const maxMinutes = Math.max(1, Math.ceil(Math.max(...charted.map((r) => r.medianAbs!)) / 60))
  const x = (h: number) => pad.l + ((h - horizons[0]) / (horizons[horizons.length - 1] - horizons[0] || 1)) * (W - pad.l - pad.r)
  const y = (seconds: number) => H - pad.b - (seconds / (maxMinutes * 60)) * (H - pad.t - pad.b)
  return (
    <>
      <svg className="chart" viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label="Median error in minutes by how far ahead the prediction was made">
        {Array.from({ length: maxMinutes + 1 }, (_, m) => (
          <g key={m}>
            <line x1={pad.l} x2={W - pad.r} y1={y(m * 60)} y2={y(m * 60)} stroke="#D9E1E6" />
            <text x={pad.l - 6} y={y(m * 60) + 4} textAnchor="end">
              {m}m
            </text>
          </g>
        ))}
        {horizons.map((h) => (
          <text key={h} x={x(h)} y={H - 6} textAnchor="middle">
            {h}
          </text>
        ))}
        {Object.entries(CHART_COLORS).map(([p, color]) => {
          const pts = horizons
            .map((h) => charted.find((r) => r.predictor === p && r.horizon === h))
            .filter((r): r is Score => r != null)
            .map((r) => `${x(r.horizon)},${y(r.medianAbs!)}`)
          return pts.length ? <polyline key={p} points={pts.join(' ')} fill="none" stroke={color} strokeWidth={p === 'v1-pace' ? 3 : 2} /> : null
        })}
      </svg>
      <div className="legend" style={{ margin: '4px 0 10px' }}>
        {Object.entries(CHART_COLORS).map(([p, color]) => (
          <span key={p}>
            <svg width="14" height="4">
              <line x1="0" x2="14" y1="2" y2="2" stroke={color} strokeWidth="3" />
            </svg>
            {PREDICTOR_LABEL[p]}
          </span>
        ))}
        <span>Minutes ahead →</span>
      </div>
    </>
  )
}
