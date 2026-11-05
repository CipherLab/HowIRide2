// What the watching screen shows, kept apart from the map and React so the rules can be tested.

import type { Departure, StopLine } from './api'
import { clock } from './components'

export interface Watch {
  stopId: string
  routeId: string
  directionId: number
}

export type Selection = { kind: 'vehicle'; id: string } | { kind: 'stop'; id: string } | ({ kind: 'watch' } & Watch) | null

export interface WatchBus {
  vehicleId: string
  /** When it leaves the stop by a live estimate; null when we only have the timetable, which never goes on a bus. */
  at: number | null
  /** Metres along the bus's shape to the stop. */
  dist: number
  shapeId: string
}

export interface WatchPlan {
  first: WatchBus | null
  second: WatchBus | null
  /** The one line under the search box, when there's something to say beyond the bubbles. */
  note: string | null
}

export interface Point {
  x: number
  y: number
}

/** A connection that drops for less than this is a blip, not stale data. */
const DROPPED_S = 30
const QUIET_S = 60
const MAP_BEHIND_S = 120
const BUS_SILENT_S = 120

const BUS_MIN_ZOOM = 12.5
const WORTH_ZOOMING_OUT = 1

const toBus = (d: Departure): WatchBus => ({ vehicleId: d.vehicleId!, at: d.v1 ?? d.v0 ?? d.metro, dist: d.dist, shapeId: d.shapeId })

/** A trip with no bus attached has only these to go on, so they can be named in the text. */
function listed(d: Departure) {
  if (d.metro != null) return { at: d.metro, words: 'Metro', sentence: 'Metro' }
  if (d.scheduled != null) return { at: d.scheduled, words: 'timetable', sentence: 'The timetable' }
  return null
}

export function planWatch(departures: Departure[], now: number): WatchPlan {
  const buses = departures.filter((d) => d.vehicleId != null)
  const noBus = (from: number, to: number) => departures.slice(from, to).filter((d) => d.vehicleId == null && listed(d))[0]
  if (buses.length === 0) {
    if (departures.length === 0) return { first: null, second: null, note: 'Nothing due here in the next 90 minutes' }
    const next = noBus(0, departures.length)
    const trip = next && listed(next)
    return { first: null, second: null, note: trip ? `No bus on the road yet. Next: ${clock(trip.at)}, ${trip.words}` : 'No bus on the road yet' }
  }

  const first = toBus(buses[0])
  const second = buses[1] ? toBus(buses[1]) : null
  const firstIndex = departures.indexOf(buses[0])
  const ahead = noBus(0, firstIndex)
  if (ahead) {
    const trip = listed(ahead)!
    const minutes = Math.round((trip.at - now) / 60)
    return { first, second, note: `${trip.sentence} lists a trip ${minutes <= 0 ? 'due now' : `in ${minutes} min`}, no bus located yet` }
  }
  if (first.at == null) return { first, second, note: 'Bus on the way, no estimate yet' }
  const after = second ? null : noBus(firstIndex + 1, departures.length)
  if (after) {
    const trip = listed(after)!
    return { first, second, note: `After this one: ${clock(trip.at)}, ${trip.words}` }
  }
  return { first, second, note: null }
}

/** The same words as the Countdown component. */
export function minutesLabel(at: number, now: number) {
  const minutes = Math.round((at - now) / 60)
  return minutes <= 0 ? 'Due' : `${minutes} min`
}

export function selectionHash(s: Selection) {
  if (!s) return ''
  if (s.kind === 'watch') return `#watch/${encodeURIComponent(s.stopId)}/${encodeURIComponent(s.routeId)}/${s.directionId}`
  return `#${s.kind}/${encodeURIComponent(s.id)}`
}

export function parseHash(hash: string): Selection {
  const parts = hash.slice(1).split('/')
  try {
    if (parts[0] === 'watch') {
      if (parts.length !== 4 || !parts[1] || !parts[2] || !/^\d+$/.test(parts[3])) return null
      return { kind: 'watch', stopId: decodeURIComponent(parts[1]), routeId: decodeURIComponent(parts[2]), directionId: Number(parts[3]) }
    }
    return (parts[0] === 'stop' || parts[0] === 'vehicle') && parts[1] ? { kind: parts[0], id: decodeURIComponent(parts[1]) } : null
  } catch {
    return null // A link with broken escapes is no link at all.
  }
}

export function findLine(lines: StopLine[], w: Watch) {
  return lines.find((l) => l.route.id === w.routeId && l.directionId === w.directionId) ?? null
}

/** How old the live data is in seconds, once it's old enough to say so; null while it's fresh. */
export function dataAge(feed: { connected: boolean; lastMessageAt: number; behindBy: number }, nowMs: number, departuresUpdatedAt: number) {
  const ages: number[] = []
  if (feed.lastMessageAt > 0) {
    const quiet = (nowMs - feed.lastMessageAt) / 1000
    if (quiet > QUIET_S || (!feed.connected && quiet > DROPPED_S)) ages.push(quiet)
    if (feed.behindBy > MAP_BEHIND_S) ages.push(feed.behindBy)
  }
  if (departuresUpdatedAt > 0) {
    const quiet = (nowMs - departuresUpdatedAt) / 1000
    if (quiet > QUIET_S) ages.push(quiet)
  }
  return ages.length > 0 ? Math.round(Math.max(...ages)) : null
}

export function busIsStale(pingAt: number, now: number) {
  return now - pingAt > BUS_SILENT_S
}

/** Whether to fit the second bus too: not when that would zoom out far past readable to do it. */
export function shouldFrameSecond(zoomAll: number, zoomFirst: number) {
  return zoomAll >= BUS_MIN_ZOOM || zoomFirst - zoomAll < WORTH_ZOOMING_OUT
}

/** Where a label for an off-screen target sits: on the way there from `from`, kept `inset` inside the edges. */
export function edgePoint(from: Point, to: Point, size: { width: number; height: number }, inset = 40): Point {
  const fit = (start: number, end: number, max: number) => {
    if (end > max - inset) return (max - inset - start) / (end - start)
    if (end < inset) return (inset - start) / (end - start)
    return 1
  }
  const t = Math.min(fit(from.x, to.x, size.width), fit(from.y, to.y, size.height))
  return t >= 1 ? to : { x: from.x + (to.x - from.x) * t, y: from.y + (to.y - from.y) * t }
}
