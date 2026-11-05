import type { Route, RouteDirection, Stop } from './api'
import { metresBetween } from './nearby'

export type Place = { lat: number; lon: number }

/** Where the search box is in picking a stop. */
export type Step =
  | { at: 'home' }
  | { at: 'directions'; route: Route }
  | { at: 'stops'; route: Route; direction: RouteDirection; near: Place | null }
  | { at: 'lines'; stop: Stop }

export function closestStop(stops: Stop[], at: Place): string | null {
  let best: Stop | null = null
  for (const stop of stops) if (!best || metresBetween(stop, at) < metresBetween(best, at)) best = stop
  return best?.id ?? null
}

const routeNumber = (r: Route) => {
  const n = parseInt(r.shortName, 10)
  return Number.isNaN(n) ? Number.MAX_SAFE_INTEGER : n
}

/** Routes for what's been typed: an exact number, then numbers starting with it, then anything in the name. */
export function matchRoutes(routes: Route[], query: string): Route[] {
  const q = query.trim().toLowerCase()
  if (!q) return []
  const rank = (r: Route) => {
    const short = r.shortName.toLowerCase()
    if (short === q) return 0
    if (short.startsWith(q)) return 1
    return r.longName.toLowerCase().includes(q) ? 2 : 3
  }
  return routes
    .map((route) => ({ route, rank: rank(route) }))
    .filter((x) => x.rank < 3)
    .sort((a, b) => a.rank - b.rank || routeNumber(a.route) - routeNumber(b.route) || a.route.shortName.localeCompare(b.route.shortName))
    .map((x) => x.route)
}
