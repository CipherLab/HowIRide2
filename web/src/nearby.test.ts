import { describe, expect, it } from 'vitest'
import type { Route, Stop } from './api'
import { boxAround, nearbyRoutes } from './nearby'

const route = (id: string): Route => ({ id, shortName: id, longName: id, color: '', textColor: '', type: 3 })
const stop = (id: string, lat: number, routes: Route[]): Stop => ({ id, name: id, lat, lon: -93, routes })
const at = { lat: 45, lon: -93 }

describe('nearbyRoutes', () => {
  it('orders routes by their nearest stop and lists each once', () => {
    const stops = [stop('far', 45.01, [route('6'), route('21')]), stop('near', 45.001, [route('21')])]
    expect(nearbyRoutes(stops, at).map((r) => r.id)).toEqual(['21', '6'])
  })

  it('includes all of a stop\'s routes', () => {
    expect(nearbyRoutes([stop('s', 45, [route('A'), route('B')])], at).map((r) => r.id)).toEqual(['A', 'B'])
  })

  it('is empty with no stops', () => expect(nearbyRoutes([], at)).toEqual([]))
})

describe('boxAround', () => {
  it('reaches about the given distance, wider in longitude at this latitude', () => {
    const [south, west, north, east] = boxAround(at, 800)
    expect(north - 45).toBeCloseTo(0.0072, 3)
    expect(45 - south).toBeCloseTo(0.0072, 3)
    expect(east + 93).toBeCloseTo(0.0102, 3)
    expect(-93 - west).toBeCloseTo(0.0102, 3)
  })
})
