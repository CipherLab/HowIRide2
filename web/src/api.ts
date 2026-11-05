import type { Watch } from './watch'

// Shapes of the Api's JSON. Times are unix seconds.

export interface Vehicle {
  id: string
  tripId: string | null
  serviceDate: string | null
  routeId: string | null
  route: string | null
  color: string | null
  textColor: string | null
  headsign: string | null
  shapeId: string | null
  dist: number | null
  distAt: number | null
  lat: number
  lon: number
  pingAt: number
  /** Today's pace on the road just ahead: >1 slower than usual, <1 faster. */
  pace: number | null
}

export interface Snapshot {
  serverTime: number
  vehicles: Vehicle[]
}

export interface Shape {
  id: string
  lat: number[]
  lon: number[]
  dist: number[]
}

export interface Route {
  id: string
  shortName: string
  longName: string
  color: string
  textColor: string
  /** GTFS route_type: 0 light rail, 2 commuter rail, 3 bus. */
  type: number
}

export interface Stop {
  id: string
  name: string
  lat: number
  lon: number
  /** Routes serving the stop: rail and rapid lines first, then buses by number. */
  routes: Route[]
}

/** Rail, and the lettered or coloured rapid bus lines (A, C, D, Orange...). */
export const isRapid = (r: Route) => r.type !== 3 || !/^\d+$/.test(r.shortName)

/** One stop on a trip, with every predictor's departure time. */
export interface StopTime {
  stopId: string
  name: string
  sequence: number
  /** Metres along the trip's shape, on the same scale as a vehicle's dist. */
  dist: number
  scheduled: number | null
  metro: number | null
  v0: number | null
  v1: number | null
  beBy: number | null
}

export interface Departure {
  tripId: string
  routeId: string
  route: string
  color: string
  textColor: string
  headsign: string
  vehicleId: string | null
  scheduled: number | null
  metro: number | null
  v0: number | null
  v1: number | null
  /** When to be at the stop so that about 19 buses in 20 are still to come, though less on some routes. */
  beBy: number | null
  directionId: number
  /** Metres along the trip's shape to this stop, on the same scale as a vehicle's dist. */
  dist: number
  shapeId: string
}

export interface RouteDirection {
  directionId: number
  label: string
  /** In route order. */
  stops: Stop[]
}

/** A route and direction a rider can catch at a stop. */
export interface StopLine {
  route: Route
  directionId: number
  label: string
  shapeId: string
}

export interface Score {
  runAt: string
  testFrom: string
  testTo: string
  predictor: string
  group: string
  horizon: number
  predicted: number
  coverage: number | null
  medianAbs: number | null
  bias: number | null
  within1m: number | null
  early: number | null
  /** Nine in ten estimates were closer than this. */
  p90Abs: number | null
}

/** One route's score in the latest run. */
export interface RouteScore {
  route: string
  predictor: string
  horizon: number
  predicted: number
  medianAbs: number | null
  early: number | null
}

export interface Health {
  serverTime: number
  feeds: { version: string; trips: number; patterns: number }[]
  vehicleLagSeconds: number | null
  tripUpdatesLagSeconds: number | null
  vehicles: number
  vehiclesOnTrip: number
  activeTrips: number
  metroTrips: number
  model: { builtAt: string | null; segments: number; stops: number }
  detector: Record<string, number> | null
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`/api${path}`)
  if (!res.ok) throw new Error(`${path}: ${res.status} ${res.statusText}`)
  return res.json() as Promise<T>
}

export const api = {
  health: () => get<Health>('/health'),
  vehicle: (id: string) => get<{ vehicle: Vehicle; nextStops: StopTime[] }>(`/vehicles/${encodeURIComponent(id)}`),
  shape: (id: string) => get<Shape>(`/shapes/${encodeURIComponent(id)}`),
  stops: (south: number, west: number, north: number, east: number) =>
    get<Stop[]>(`/stops?bbox=${[south, west, north, east].map((v) => v.toFixed(5)).join(',')}`),
  stop: (id: string) => get<{ stop: Stop; departures: Departure[] }>(`/stops/${encodeURIComponent(id)}`),
  watchStop: (w: Watch) =>
    get<{ stop: Stop; departures: Departure[] }>(`/stops/${encodeURIComponent(w.stopId)}?route=${encodeURIComponent(w.routeId)}&direction=${w.directionId}`),
  directions: (routeId: string) => get<RouteDirection[]>(`/routes/${encodeURIComponent(routeId)}/directions`),
  lines: (stopId: string) => get<StopLine[]>(`/stops/${encodeURIComponent(stopId)}/lines`),
  searchStops: (q: string) => get<Stop[]>(`/stops/search?q=${encodeURIComponent(q)}`),
  scores: () => get<{ runs: Score[] }>('/scores'),
  routeScores: () => get<{ runAt: string | null; rows: RouteScore[] }>('/scores/routes'),
  routes: () => get<Route[]>('/routes'),
}

/** The single time to headline: ours if we have it, then Metro's, then the timetable. */
export function bestEstimate(t: { v1: number | null; v0: number | null; metro: number | null; scheduled: number | null }) {
  if (t.v1 != null) return { time: t.v1, source: 'ours' as const }
  if (t.v0 != null) return { time: t.v0, source: 'ours' as const }
  if (t.metro != null) return { time: t.metro, source: 'metro' as const }
  if (t.scheduled != null) return { time: t.scheduled, source: 'schedule' as const }
  return null
}
