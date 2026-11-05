import type { Route, Stop } from './api'

type Place = { lat: number; lon: number }

export const NEARBY_METRES = 800
export const NEARBY_WIDER_METRES = 2000

const METRES_PER_DEGREE = 111_320

/** South, west, north and east edges of a box reaching this far from a point. */
export function boxAround(at: Place, metres: number): [south: number, west: number, north: number, east: number] {
  const dLat = metres / METRES_PER_DEGREE
  const dLon = dLat / Math.cos((at.lat * Math.PI) / 180)
  return [at.lat - dLat, at.lon - dLon, at.lat + dLat, at.lon + dLon]
}

/** Good enough over a few kilometres, and only used to compare. */
export function metresBetween(a: Place, b: Place) {
  const dLat = a.lat - b.lat
  const dLon = (a.lon - b.lon) * Math.cos((a.lat * Math.PI) / 180)
  return Math.hypot(dLat, dLon) * METRES_PER_DEGREE
}

/** Each route once, in order of its nearest stop. */
export function nearbyRoutes(stops: Stop[], at: Place): Route[] {
  const seen = new Map<string, Route>()
  for (const stop of [...stops].sort((a, b) => metresBetween(a, at) - metresBetween(b, at)))
    for (const route of stop.routes) if (!seen.has(route.id)) seen.set(route.id, route)
  return [...seen.values()]
}
