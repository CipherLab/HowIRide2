import { useQuery } from '@tanstack/react-query'
import { useLayoutEffect, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { api, type Route, type RouteScore, type Score } from './api'
import { Mark, RouteBadge } from './components'

/** Same predictors as the scorecard: what the app shows, what Metro's app shows, and the timetable for scale. */
const OURS = 'v1-pace'
const METRO = 'metro-tripupdates'
const SCHEDULE = 'schedule'

type Key = 'ours' | 'metro' | 'schedule'
const PREDICTOR: Record<Key, string> = { ours: OURS, metro: METRO, schedule: SCHEDULE }
const LABEL: Record<Key, string> = { ours: 'Ours', metro: 'Metro Transit', schedule: 'Timetable' }
/** Validated pair (CVD and contrast on the panel surface); the timetable is a grey reference, not a competitor. */
const COLOR: Record<Key, string> = { ours: '#B37D00', metro: '#2B6CB0', schedule: '#A3B0BA' }

/** Route scores need this many predictions from both of us before a route is drawn. */
const MIN_ROUTE_PREDICTIONS = 100
const ROUTE_HORIZON = 10
const TIE_S = 5
/** Until expanded, the route chart shows this many routes from each end: the biggest wins and the biggest losses. */
const ROUTE_ENDS = 10

type Mode = 'bus' | 'rail'

export function Charts() {
  const [mode, setMode] = useState<Mode>('bus')
  const scores = useQuery({ queryKey: ['scores'], queryFn: api.scores, refetchInterval: 300_000 })
  const runs = scores.data?.runs ?? []
  const latest = runs.length ? runs[runs.length - 1].runAt : null
  const rows = runs.filter((r) => r.runAt === latest && r.group === mode)
  const sample = rows.find((r) => r.predictor === OURS)

  if (scores.isPending) return <p className="empty">Loading scores…</p>
  if (!latest) return <p className="empty">No scores yet. The backtest writes them after each service day.</p>

  const series = (key: Key, value: (s: Score) => number | null) => ({
    key,
    points: rows
      .filter((r) => r.predictor === PREDICTOR[key])
      .map((r) => ({ x: r.horizon, y: value(r) }))
      .filter((p): p is { x: number; y: number } => p.y != null)
      .sort((a, b) => a.x - b.x),
  })
  const all = (value: (s: Score) => number | null) => (['ours', 'metro', 'schedule'] as Key[]).map((k) => series(k, value))
  const horizons = [...new Set(rows.map((r) => r.horizon))].sort((a, b) => a - b)
  const minutes = (s: number) => `${(s / 60).toFixed(1)} min`
  const minuteTick = (s: number) => `${Math.round((s / 60) * 10) / 10}`
  const inMinutes = { y: minutes, tick: minuteTick, unit: 60 }
  const ahead = { ticks: horizons, format: (x: number) => `${x}` }

  return (
    <>
      <div className="section charts-head">
        <div className="seg" role="radiogroup" aria-label="Vehicles">
          {(['bus', 'rail'] as Mode[]).map((m) => (
            <button key={m} role="radio" aria-checked={mode === m} onClick={() => setMode(m)}>
              {m === 'bus' ? 'Buses' : 'Trains'}
            </button>
          ))}
        </div>
        <div className="legend" aria-label="Legend">
          {(['ours', 'metro', 'schedule'] as Key[]).map((k) => (
            <span key={k}>
              <Mark kind={k} />
              {LABEL[k]}
            </span>
          ))}
        </div>
      </div>

      <div className="section">
        <p className="hint">Each chart runs from estimates made 2 minutes before the departure to 30 minutes before.</p>
        <div className="multiples">
          <LineChart
            title="Typical miss"
            sub="Half of estimates were closer than this, in minutes"
            series={all((s) => s.medianAbs)}
            x={ahead}
            {...inMinutes}
          />
          <LineChart
            title="Bad misses"
            sub="1 in 10 estimates was further off than this, in minutes"
            series={all((s) => s.p90Abs)}
            x={ahead}
            {...inMinutes}
          />
          <LineChart
            title="Gone before the time shown"
            sub="How often it left more than a minute before the estimate"
            series={all((s) => s.early)}
            x={ahead}
            y={(v) => `${Math.round(v * 100)}%`}
            unit={0.01}
            floorZero
          />
          <LineChart
            title="Early or late, on average"
            sub="Minutes; above 0, the estimate was later than it left"
            series={all((s) => s.bias)}
            x={ahead}
            y={(s) => `${s > 0 ? '+' : ''}${(s / 60).toFixed(1)} min`}
            tick={(s) => `${s > 0 ? '+' : ''}${Math.round((s / 60) * 10) / 10}`}
            unit={60}
            zeroLine
          />
        </div>
        <p className="axis-note">Minutes ahead the estimate was made</p>
      </div>

      <RouteChart mode={mode} />

      <RunHistory runs={runs} mode={mode} />

      {sample && (
        <div className="section">
          <p className="fineprint">
            Latest run: {sample.predicted.toLocaleString()} {mode === 'bus' ? 'bus' : 'train'} predictions at each distance, tested{' '}
            {testWindow(sample.testFrom, sample.testTo)}. The numbers behind these charts are in the Scorecard tab’s full table.
          </p>
        </div>
      )}
    </>
  )
}

function testWindow(from: string, to: string) {
  const date = (s: string) =>
    /^\d{8}$/.test(s)
      ? new Date(+s.slice(0, 4), +s.slice(4, 6) - 1, +s.slice(6, 8)).toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' })
      : new Date(s).toLocaleString([], { weekday: 'short', hour: 'numeric', minute: '2-digit' })
  return date(from) === date(to) ? `on ${date(from)}` : `from ${date(from)} to ${date(to)}`
}

/**
 * Width of an element, kept current, so SVG text stays at its real size instead of scaling with a viewBox.
 * A callback ref, so it also works for an element that only appears once its data has loaded.
 */
function useWidth<T extends HTMLElement>() {
  const [el, setEl] = useState<T | null>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    if (!el) return
    const ro = new ResizeObserver(([e]) => setWidth(Math.floor(e.contentRect.width)))
    ro.observe(el)
    return () => ro.disconnect()
  }, [el])
  return [setEl, width] as const
}

/** Round step for about `count` ticks across a span. */
function niceStep(span: number, count: number) {
  const raw = span / Math.max(1, count)
  const mag = 10 ** Math.floor(Math.log10(raw || 1))
  return [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= raw) ?? 10 * mag
}

interface Series {
  key: Key
  points: { x: number; y: number }[]
}

/**
 * Lines on a shared x, with a crosshair that snaps to the nearest x and one tooltip listing every series.
 * Arrow keys move the crosshair when the chart has focus.
 */
function LineChart({
  title,
  sub,
  series,
  x,
  y,
  tick = y,
  unit = 1,
  zeroLine = false,
  floorZero = false,
  height = 150,
}: {
  title: string
  sub: string
  series: Series[]
  x: { ticks: number[]; format: (x: number) => string; labels?: string[] }
  /** Values in the tooltip and the screen-reader summary. */
  y: (v: number) => string
  /** Axis tick labels; the unit goes in the subtitle. */
  tick?: (v: number) => string
  /** Ticks fall on round numbers of this unit (60 for minutes from seconds, 0.01 for percent from a fraction). */
  unit?: number
  zeroLine?: boolean
  floorZero?: boolean
  height?: number
}) {
  const [box, width] = useWidth<HTMLElement>()
  const [hover, setHover] = useState<number | null>(null)
  const drawn = series.filter((s) => s.points.length > 0)
  const xs = x.ticks
  const margin = { left: 34, right: 12, top: 10, bottom: 22 }
  const w = Math.max(0, width - margin.left - margin.right)
  const h = height - margin.top - margin.bottom

  const values = drawn.flatMap((s) => s.points.map((p) => p.y))
  let lo = Math.min(0, ...values)
  let hi = Math.max(...values, 0)
  if (floorZero) lo = 0
  const step = niceStep((hi - lo) / unit || 1, 4) * unit
  lo = Math.floor(lo / step) * step
  hi = Math.ceil(hi / step) * step || step
  const yTicks: number[] = []
  for (let v = lo; v <= hi + step / 2; v += step) yTicks.push(Math.round(v * 1e6) / 1e6)

  const x0 = xs[0] ?? 0
  const x1 = xs[xs.length - 1] ?? 1
  const px = (v: number) => (x1 === x0 ? w / 2 : ((v - x0) / (x1 - x0)) * w)
  const py = (v: number) => h - ((v - lo) / (hi - lo)) * h

  const nearest = (clientX: number, el: Element) => {
    const left = el.getBoundingClientRect().left + margin.left
    const at = clientX - left
    let best = 0
    xs.forEach((v, i) => Math.abs(px(v) - at) < Math.abs(px(xs[best]) - at) && (best = i))
    return best
  }
  const onMove = (e: PointerEvent<SVGRectElement>) => setHover(nearest(e.clientX, e.currentTarget.ownerSVGElement!))
  const onKey = (e: KeyboardEvent) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
    e.preventDefault()
    setHover((i) => Math.max(0, Math.min(xs.length - 1, (i ?? -1) + (e.key === 'ArrowRight' ? 1 : -1))))
  }
  const hx = hover != null ? xs[hover] : null
  const label = (v: number, i: number) => x.labels?.[i] ?? x.format(v)

  return (
    <figure className="chart" ref={box}>
      <figcaption>
        <strong>{title}</strong>
        <span>{sub}</span>
      </figcaption>
      {width > 0 && drawn.length > 0 && (
        <div
          className="plot"
          tabIndex={0}
          onKeyDown={onKey}
          onBlur={() => setHover(null)}
          aria-label={`${title}. ${drawn
            .map((s) => `${LABEL[s.key]}: ${s.points.map((p, i) => `${label(p.x, i)} ${y(p.y)}`).join(', ')}`)
            .join('. ')}`}
        >
          <svg width={width} height={height} aria-hidden="true">
            <g transform={`translate(${margin.left},${margin.top})`}>
              {yTicks.map((v) => (
                <g key={v}>
                  <line x1={0} x2={w} y1={py(v)} y2={py(v)} className={v === 0 && zeroLine ? 'zero' : 'grid'} />
                  <text x={-6} y={py(v)} className="tick" textAnchor="end" dominantBaseline="middle">
                    {tick(v)}
                  </text>
                </g>
              ))}
              {xs.map((v, i) => (
                <text key={v} x={px(v)} y={h + 15} className="tick" textAnchor={xs.length > 1 && i === xs.length - 1 ? 'end' : i === 0 && x.labels ? 'start' : 'middle'}>
                  {label(v, i)}
                </text>
              ))}
              {hx != null && <line className="crosshair" x1={px(hx)} x2={px(hx)} y1={0} y2={h} />}
              {drawn.map((s) => (
                <g key={s.key}>
                  <polyline
                    points={s.points.map((p) => `${px(p.x)},${py(p.y)}`).join(' ')}
                    fill="none"
                    stroke={COLOR[s.key]}
                    strokeWidth={s.key === 'schedule' ? 1.5 : 2}
                    strokeLinejoin="round"
                    strokeLinecap="round"
                  />
                  {s.key !== 'schedule' &&
                    s.points.map((p) => (
                      <circle
                        key={p.x}
                        cx={px(p.x)}
                        cy={py(p.y)}
                        r={p.x === hx ? 5.5 : 4}
                        fill={s.key === 'ours' ? '#F2A900' : '#F3F6F7'}
                        stroke={COLOR[s.key]}
                        strokeWidth={2}
                        paintOrder="stroke"
                      />
                    ))}
                </g>
              ))}
              <rect
                width={w}
                height={h}
                fill="transparent"
                onPointerMove={onMove}
                onPointerLeave={() => setHover(null)}
              />
            </g>
          </svg>
          {hover != null && hx != null && (
            <div
              className="tooltip"
              style={{
                left: Math.min(margin.left + px(hx) + 10, width - 150),
                top: margin.top,
              }}
            >
              <div className="tt-head">{x.labels?.[hover] ?? `${x.format(hx)} min ahead`}</div>
              {drawn.map((s) => {
                const p = s.points.find((q) => q.x === hx)
                return (
                  <div key={s.key} className="tt-row">
                    <i style={{ background: COLOR[s.key] }} />
                    <b>{p ? y(p.y) : '–'}</b>
                    <span>{LABEL[s.key]}</span>
                  </div>
                )
              })}
            </div>
          )}
        </div>
      )}
    </figure>
  )
}

/**
 * Every route as a dumbbell at 10 minutes out: our typical miss (dot) and Metro's (ring), sorted from where we're
 * furthest ahead to where we're furthest behind.
 */
function RouteChart({ mode }: { mode: Mode }) {
  const scores = useQuery({ queryKey: ['route-scores'], queryFn: api.routeScores, refetchInterval: 300_000 })
  const routes = useQuery({ queryKey: ['routes'], queryFn: api.routes, staleTime: Infinity })
  const [box, width] = useWidth<HTMLDivElement>()
  const [expanded, setExpanded] = useState(false)
  if (!scores.data || !routes.data) return null

  const byId = new Map<string, Route>(routes.data.map((r) => [r.id, r]))
  const isRail = (r: Route | undefined) => r != null && r.type !== 3
  const at = (route: string, predictor: string): RouteScore | undefined =>
    scores.data.rows.find((s) => s.route === route && s.predictor === predictor && s.horizon === ROUTE_HORIZON)
  const ids = [...new Set(scores.data.rows.map((s) => s.route))].filter((id) => isRail(byId.get(id)) === (mode === 'rail'))
  const pairs = ids
    .map((id) => ({ id, route: byId.get(id), ours: at(id, OURS), metro: at(id, METRO) }))
    .filter((p) => p.ours?.medianAbs != null && p.metro?.medianAbs != null)
  const shown = pairs
    .filter((p) => p.ours!.predicted >= MIN_ROUTE_PREDICTIONS && p.metro!.predicted >= MIN_ROUTE_PREDICTIONS)
    .map((p) => ({ ...p, o: p.ours!.medianAbs!, m: p.metro!.medianAbs!, diff: p.ours!.medianAbs! - p.metro!.medianAbs! }))
    .sort((a, b) => a.diff - b.diff)
  if (shown.length === 0) return null

  const wins = shown.filter((p) => p.diff < -TIE_S).length
  const losses = shown.filter((p) => p.diff > TIE_S).length
  const max = Math.ceil(Math.max(...shown.flatMap((p) => [p.o, p.m])) / 60) * 60
  const track = Math.max(0, width - 48 - 96)
  const pos = (s: number) => (s / max) * track
  const name = (p: (typeof shown)[number]) => p.route?.shortName ?? p.id
  const hidden = expanded ? 0 : Math.max(0, shown.length - 2 * ROUTE_ENDS)
  const visible = hidden ? [...shown.slice(0, ROUTE_ENDS), null, ...shown.slice(-ROUTE_ENDS)] : shown

  return (
    <div className="section">
      <h2>Route by route, {ROUTE_HORIZON} minutes ahead</h2>
      <p className="hint">
        Ours is closer on <b>{wins}</b> of {shown.length} routes, Metro’s on <b>{losses}</b>, and they’re within {TIE_S} seconds on the
        rest. Sorted from where ours is furthest ahead to where it’s furthest behind.
        {pairs.length > shown.length && ` ${pairs.length - shown.length} routes with fewer than ${MIN_ROUTE_PREDICTIONS} predictions are left out.`}
      </p>
      <div className="dumbbells" ref={box} role="table" aria-label={`Typical miss by route, ${ROUTE_HORIZON} minutes ahead`}>
        <div className="db-row db-head" role="row">
          <span role="columnheader">Route</span>
          <span role="columnheader">Typical miss</span>
          <span role="columnheader">Ours / Metro</span>
        </div>
        {width > 0 &&
          visible.map((p) => {
            if (!p)
              return (
                <div key="more" className="db-more">
                  <span />
                  <button onClick={() => setExpanded(true)}>Show the {hidden} routes in between</button>
                </div>
              )
            const lo = Math.min(p.o, p.m)
            const hi = Math.max(p.o, p.m)
            const verdict = Math.abs(p.diff) <= TIE_S ? 'even' : p.diff < 0 ? 'win' : 'loss'
            return (
              <div
                key={p.id}
                className={`db-row ${verdict}`}
                role="row"
                title={`${p.route?.longName ?? name(p)}: ours ${Math.round(p.o)} s, Metro ${Math.round(p.m)} s, from ${p.ours!.predicted.toLocaleString()} predictions`}
              >
                <span role="rowheader">
                  <RouteBadge route={name(p)} color={p.route?.color} textColor={p.route?.textColor} />
                </span>
                <svg width={track} height={18} role="cell" aria-label={`Ours ${Math.round(p.o)} seconds, Metro ${Math.round(p.m)} seconds`}>
                  <line x1={0} x2={track} y1={9} y2={9} className="grid" />
                  <line x1={pos(lo)} x2={pos(hi)} y1={9} y2={9} className="db-gap" />
                  <circle cx={pos(p.m)} cy={9} r={4.5} fill="#F3F6F7" stroke={COLOR.metro} strokeWidth={2} />
                  <circle cx={pos(p.o)} cy={9} r={4.5} fill="#F2A900" stroke={COLOR.ours} strokeWidth={1.5} />
                </svg>
                <span role="cell" className="db-values">
                  <b>{Math.round(p.o)}</b> / {Math.round(p.m)} s
                </span>
              </div>
            )
          })}
        <div className="db-row db-axis" aria-hidden="true">
          <span />
          <svg width={track} height={16}>
            {Array.from({ length: max / 60 + 1 }, (_, i) => (
              <text key={i} x={pos(i * 60)} y={11} className="tick" textAnchor={i === 0 ? 'start' : i === max / 60 ? 'end' : 'middle'}>
                {i}
              </text>
            ))}
          </svg>
          <span className="tick-unit">min off</span>
        </div>
      </div>
    </div>
  )
}

/** Typical miss 10 minutes ahead in each scoring run, oldest first. */
function RunHistory({ runs, mode }: { runs: Score[]; mode: Mode }) {
  // One point per service day, from its latest run; runs by hand over part of a day or several days don't compare.
  const at = runs.filter((r) => r.group === mode && r.horizon === ROUTE_HORIZON && r.testFrom === r.testTo && /^\d{8}$/.test(r.testFrom))
  const latest = new Map<string, string>()
  for (const r of at) if (r.runAt > (latest.get(r.testFrom) ?? '')) latest.set(r.testFrom, r.runAt)
  const days = [...latest.keys()].sort()
  const times = days.map((d) => latest.get(d)!)
  if (times.length < 2) return null
  const series = (['ours', 'metro'] as Key[]).map((key) => ({
    key,
    points: times
      .map((t, i) => ({ x: i, y: at.find((r) => r.runAt === t && r.predictor === PREDICTOR[key])?.medianAbs ?? null }))
      .filter((p): p is { x: number; y: number } => p.y != null),
  }))
  const labels = days.map((d) => `${Number(d.slice(4, 6))}/${Number(d.slice(6, 8))}`)
  return (
    <div className="section">
      <h2>Each service day, {ROUTE_HORIZON} minutes ahead</h2>
      <p className="hint">
        One point per day, added by the nightly run. Each day is scored on tables learned only from the days before it.
        Saturdays and Sundays are easier for Metro and have less history for us, so compare weekdays with weekdays.
      </p>
      <LineChart
        title="Typical miss"
        sub="Half of estimates were closer than this, in minutes"
        series={series}
        x={{ ticks: times.map((_, i) => i), format: (i) => labels[i] ?? '', labels }}
        y={(s) => `${(s / 60).toFixed(1)} min`}
        tick={(s) => `${Math.round((s / 60) * 10) / 10}`}
        unit={60}
        height={170}
      />
    </div>
  )
}
