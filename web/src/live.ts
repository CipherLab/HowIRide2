// Live vehicle playback. The server sends each bus as (shape, distance along it, time). We replay a little
// behind the newest data and interpolate distance, then look the point up on the shape, so buses follow the
// real streets around corners instead of jumping between raw GPS fixes.

import { api, type Shape, type Snapshot, type Vehicle } from './api'

/** Playback runs this far behind the newest ping, so there's nearly always a later sample to interpolate toward. */
export const PLAYBACK_DELAY_S = 15

/** If the next sample is late, keep moving at the last speed for this long, then hold. */
const DEAD_RECKON_S = 15

const MAX_SAMPLES = 8

interface Track {
  vehicle: Vehicle
  samples: { t: number; d: number }[]
}

export interface Placed {
  vehicle: Vehicle
  lon: number
  lat: number
  /** Heading in degrees clockwise from north, when known from the shape. */
  bearing: number | null
}

export class LiveVehicles {
  private tracks = new Map<string, Track>()
  private shapes = new Map<string, Shape | 'loading'>()
  /** Seconds to add to the local clock to get server time. */
  private offset = 0
  private newestPing = 0
  /** Server time minus the newest ping, measured when each snapshot arrives; the feed's own delay. */
  private lag = 0
  private source: EventSource | null = null
  private listeners = new Set<() => void>()
  connected = false
  lastMessageAt = 0

  start() {
    this.source = new EventSource('/api/stream')
    this.source.onmessage = (e) => this.ingest(JSON.parse(e.data) as Snapshot)
    this.source.onopen = () => this.setConnected(true)
    this.source.onerror = () => this.setConnected(false)
  }

  stop() {
    this.source?.close()
  }

  subscribe(fn: () => void) {
    this.listeners.add(fn)
    return () => this.listeners.delete(fn)
  }

  private setConnected(value: boolean) {
    this.connected = value
    this.listeners.forEach((fn) => fn())
  }

  private ingest(snapshot: Snapshot) {
    this.offset = snapshot.serverTime - Date.now() / 1000
    this.lastMessageAt = Date.now()
    const seen = new Set<string>()
    for (const v of snapshot.vehicles) {
      seen.add(v.id)
      let track = this.tracks.get(v.id)
      if (!track || track.vehicle.tripId !== v.tripId) {
        track = { vehicle: v, samples: [] }
        this.tracks.set(v.id, track)
      }
      track.vehicle = v
      if (v.dist != null && v.distAt != null) {
        const last = track.samples[track.samples.length - 1]
        if (!last || v.distAt > last.t) track.samples.push({ t: v.distAt, d: Math.max(v.dist, last?.d ?? 0) })
        if (track.samples.length > MAX_SAMPLES) track.samples.shift()
        this.newestPing = Math.max(this.newestPing, v.distAt)
      }
      if (v.shapeId && !this.shapes.has(v.shapeId)) this.loadShape(v.shapeId)
    }
    for (const id of this.tracks.keys()) if (!seen.has(id)) this.tracks.delete(id)
    if (this.newestPing > 0) this.lag = Math.max(0, snapshot.serverTime - this.newestPing)
    this.connected = true
    this.listeners.forEach((fn) => fn())
  }

  private loadShape(id: string) {
    this.shapes.set(id, 'loading')
    api.shape(id).then(
      (shape) => this.shapes.set(id, shape),
      () => this.shapes.delete(id),
    )
  }

  shape(id: string | null): Shape | null {
    const s = id ? this.shapes.get(id) : undefined
    return s && s !== 'loading' ? s : null
  }

  get count() {
    return this.tracks.size
  }

  /** The playback clock in server seconds: real time, minus the feed's delay, minus a small buffer. */
  playbackTime() {
    return Date.now() / 1000 + this.offset - this.lag - PLAYBACK_DELAY_S
  }

  /** How far behind real time the map is showing, in seconds. */
  behindBy() {
    return this.lag + PLAYBACK_DELAY_S
  }

  vehicle(id: string) {
    return this.tracks.get(id)?.vehicle ?? null
  }

  /** Every vehicle placed at playback time. */
  place(): Placed[] {
    const t = this.playbackTime()
    const out: Placed[] = []
    for (const track of this.tracks.values()) {
      const shape = this.shape(track.vehicle.shapeId)
      const d = shape ? distanceAt(track.samples, t) : null
      if (shape && d != null) {
        const p = pointAt(shape, d)
        out.push({ vehicle: track.vehicle, lon: p.lon, lat: p.lat, bearing: p.bearing })
      } else {
        out.push({ vehicle: track.vehicle, lon: track.vehicle.lon, lat: track.vehicle.lat, bearing: null })
      }
    }
    return out
  }
}

function distanceAt(samples: { t: number; d: number }[], t: number): number | null {
  if (samples.length === 0) return null
  if (t <= samples[0].t) return samples[0].d
  for (let i = 1; i < samples.length; i++) {
    const a = samples[i - 1]
    const b = samples[i]
    if (t <= b.t) return a.d + ((b.d - a.d) * (t - a.t)) / (b.t - a.t)
  }
  const last = samples[samples.length - 1]
  if (samples.length < 2) return last.d
  const prev = samples[samples.length - 2]
  const speed = (last.d - prev.d) / (last.t - prev.t)
  return last.d + speed * Math.min(t - last.t, DEAD_RECKON_S)
}

function pointAt(shape: Shape, d: number) {
  const { dist, lat, lon } = shape
  let lo = 0
  let hi = dist.length - 1
  if (d <= dist[0]) return { lat: lat[0], lon: lon[0], bearing: bearingOf(shape, 0) }
  if (d >= dist[hi]) return { lat: lat[hi], lon: lon[hi], bearing: bearingOf(shape, hi - 1) }
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1
    if (dist[mid] <= d) lo = mid
    else hi = mid
  }
  const f = dist[hi] > dist[lo] ? (d - dist[lo]) / (dist[hi] - dist[lo]) : 0
  return { lat: lat[lo] + f * (lat[hi] - lat[lo]), lon: lon[lo] + f * (lon[hi] - lon[lo]), bearing: bearingOf(shape, lo) }
}

function bearingOf(shape: Shape, i: number) {
  const j = Math.min(i + 1, shape.lat.length - 1)
  const dLon = (shape.lon[j] - shape.lon[i]) * Math.cos((shape.lat[i] * Math.PI) / 180)
  const dLat = shape.lat[j] - shape.lat[i]
  if (dLon === 0 && dLat === 0) return null
  return ((Math.atan2(dLon, dLat) * 180) / Math.PI + 360) % 360
}
