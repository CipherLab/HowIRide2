import { useEffect, useState } from 'react'

export function useNow(intervalMs = 5000) {
  const [now, setNow] = useState(() => Date.now() / 1000)
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now() / 1000), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])
  return now
}

export function RouteBadge({ route, color, textColor }: { route: string; color?: string | null; textColor?: string | null }) {
  return (
    <span className="badge" style={{ background: `#${color ?? '4F5B66'}`, color: `#${textColor ?? 'FFFFFF'}` }}>
      {route}
    </span>
  )
}

/** Minutes until a departure, in departure-board type. */
export function Countdown({ at, now }: { at: number; now: number }) {
  const minutes = Math.round((at - now) / 60)
  if (minutes <= 0) return <span className="countdown due">Due</span>
  return (
    <span className="countdown">
      {minutes}
      <small>min</small>
    </span>
  )
}

/** Plain-language difference from the timetable. */
export function VsSchedule({ at, scheduled }: { at: number; scheduled: number | null }) {
  if (scheduled == null) return null
  const minutes = Math.round((at - scheduled) / 60)
  if (minutes >= 1) return <span className="late">{minutes} min late</span>
  if (minutes <= -1) return <span className="early">{-minutes} min early</span>
  return <span>on time</span>
}

export function clock(t: number | null) {
  if (t == null) return '–'
  return new Date(t * 1000).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
}

const MARK = {
  schedule: { color: '#7A8A96' },
  metro: { color: '#14212B' },
  ours: { color: '#F2A900' },
}

type Source = 'ours' | 'metro' | 'schedule'

/** The mark each source has on the timeline, small enough to sit in a line of text. */
export function Mark({ kind }: { kind: Source }) {
  if (kind === 'schedule')
    return (
      <svg className="mark" width="4" height="14" aria-hidden="true">
        <line x1="2" x2="2" y1="0" y2="14" stroke={MARK.schedule.color} strokeWidth="2" />
      </svg>
    )
  return (
    <svg className="mark" width="12" height="14" aria-hidden="true">
      {kind === 'ours' ? (
        <circle cx="6" cy="7" r="5" fill={MARK.ours.color} stroke="#B37D00" />
      ) : (
        <circle cx="6" cy="7" r="5" fill="#F3F6F7" stroke={MARK.metro.color} strokeWidth="2" />
      )}
    </svg>
  )
}

/** Clock time without AM/PM: three of them share a line, and they're all within the next hour or so. */
function shortClock(t: number) {
  return new Date(t * 1000)
    .toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
    .replace(/\s?[AaPp]\.?[Mm]\.?$/, '')
}

/** Our estimate, Metro Transit's and the timetable's, each behind its timeline mark, then how ours compares to the timetable. */
export function Times({ ours, metro, scheduled }: { ours: number | null; metro: number | null; scheduled: number | null }) {
  const label = [
    ours != null && `Ours ${clock(ours)}`,
    metro != null && `Metro Transit ${clock(metro)}`,
    scheduled != null && `Timetable ${clock(scheduled)}`,
  ]
    .filter(Boolean)
    .join(', ')
  return (
    <span className="times" aria-label={label}>
      {ours != null && (
        <span className="t ours" title={`Our estimate ${clock(ours)}`}>
          <Mark kind="ours" />
          {shortClock(ours)}
        </span>
      )}
      {metro != null && (
        <span className="t" title={`Metro Transit's estimate ${clock(metro)}`}>
          <Mark kind="metro" />
          {shortClock(metro)}
        </span>
      )}
      {scheduled != null && (
        <span className="t" title={`Timetable ${clock(scheduled)}`}>
          <Mark kind="schedule" />
          {shortClock(scheduled)}
        </span>
      )}
      {ours != null && <VsSchedule at={ours} scheduled={scheduled} />}
    </span>
  )
}

/**
 * The signature: where each predictor puts this departure on a shared minutes-from-now axis.
 * Timetable tick, Metro Transit's hollow ring, our amber dot. Agreement is a cluster; disagreement is visible.
 */
export function Timeline({
  now,
  span,
  scheduled,
  metro,
  ours,
}: {
  now: number
  span: number
  scheduled: number | null
  metro: number | null
  ours: number | null
}) {
  const x = (t: number) => Math.max(0, Math.min(1, (t - now) / span)) * 100
  const ticks = []
  const step = span > 1800 ? 600 : 300
  for (let s = step; s < span; s += step) ticks.push(s)
  const label = [
    ours != null && `Ours ${clock(ours)}`,
    metro != null && `Metro Transit ${clock(metro)}`,
    scheduled != null && `Timetable ${clock(scheduled)}`,
  ]
    .filter(Boolean)
    .join(' · ')
  return (
    <svg className="strip" width="100%" height="18" role="img" aria-label={label}>
      <title>{label}</title>
      <line x1="0" x2="100%" y1="9" y2="9" stroke="#D9E1E6" strokeWidth="2" />
      {ticks.map((s) => (
        <line key={s} x1={`${(s / span) * 100}%`} x2={`${(s / span) * 100}%`} y1="6" y2="12" stroke="#D9E1E6" strokeWidth="1" />
      ))}
      {scheduled != null && (
        <line x1={`${x(scheduled)}%`} x2={`${x(scheduled)}%`} y1="2" y2="16" stroke={MARK.schedule.color} strokeWidth="2" />
      )}
      {metro != null && <circle cx={`${x(metro)}%`} cy="9" r="5" fill="#F3F6F7" stroke={MARK.metro.color} strokeWidth="2" />}
      {ours != null && <circle cx={`${x(ours)}%`} cy="9" r="5" fill={MARK.ours.color} stroke="#B37D00" strokeWidth="1" />}
    </svg>
  )
}

export function TimelineLegend({ span }: { span: number }) {
  return (
    <div className="legend">
      <span>
        <Mark kind="ours" />
        Ours
      </span>
      <span>
        <Mark kind="metro" />
        Metro Transit
      </span>
      <span>
        <Mark kind="schedule" />
        Timetable
      </span>
      <span>Next {Math.round(span / 60)} min</span>
    </div>
  )
}

/** Axis length for a set of times: the next round number of minutes that fits them all. */
export function spanFor(now: number, times: (number | null)[]) {
  const furthest = Math.max(0, ...times.filter((t): t is number => t != null).map((t) => t - now))
  for (const minutes of [10, 20, 30, 45, 60, 90]) if (furthest <= minutes * 60) return minutes * 60
  return 90 * 60
}

/** Describe today's pace in words a rider would use. */
export function paceWords(pace: number | null) {
  if (pace == null) return null
  const pct = Math.round(Math.abs(pace - 1) * 100)
  if (pct < 8) return 'Moving at a normal pace for this time of day'
  return pace > 1 ? `Running ${pct}% slower than usual on the road ahead` : `Running ${pct}% faster than usual on the road ahead`
}
