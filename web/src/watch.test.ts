import { describe, expect, it } from 'vitest'
import type { Departure, Route, StopLine } from './api'
import { clock } from './components'
import { busIsStale, dataAge, edgePoint, findLine, minutesLabel, parseHash, planWatch, selectionHash, shouldFrameSecond } from './watch'

const NOW = 1000

function dep(over: Partial<Departure>): Departure {
  return {
    tripId: 't',
    routeId: 'R',
    route: 'R',
    color: '000000',
    textColor: 'FFFFFF',
    headsign: 'East',
    vehicleId: null,
    scheduled: null,
    metro: null,
    v0: null,
    v1: null,
    beBy: null,
    directionId: 0,
    dist: 100,
    shapeId: 'S',
    ...over,
  }
}

describe('planWatch', () => {
  it('shows two buses with no note', () => {
    const plan = planWatch([dep({ vehicleId: 'a', v1: 1240, dist: 10, shapeId: 'X' }), dep({ vehicleId: 'b', v1: 1960 })], NOW)
    expect(plan.first).toEqual({ vehicleId: 'a', at: 1240, dist: 10, shapeId: 'X' })
    expect(plan.second?.vehicleId).toBe('b')
    expect(plan.note).toBeNull()
  })

  it('prefers our estimate, then the older model, then Metro', () => {
    expect(planWatch([dep({ vehicleId: 'a', v1: 1, v0: 2, metro: 3, scheduled: 4 })], NOW).first?.at).toBe(1)
    expect(planWatch([dep({ vehicleId: 'a', v0: 2, metro: 3, scheduled: 4 })], NOW).first?.at).toBe(2)
    expect(planWatch([dep({ vehicleId: 'a', metro: 3, scheduled: 4 })], NOW).first?.at).toBe(3)
  })

  it('never puts a scheduled time on a bus', () => {
    const plan = planWatch([dep({ vehicleId: 'a', scheduled: 1240 })], NOW)
    expect(plan.first?.at).toBeNull()
    expect(plan.note).toBe('Bus on the way, no estimate yet')
  })

  it('warns about a trip with no bus ahead of the first bus, keeping the bus first', () => {
    const plan = planWatch([dep({ metro: 1180 }), dep({ vehicleId: 'a', v1: 1900 })], NOW)
    expect(plan.first?.vehicleId).toBe('a')
    expect(plan.note).toBe('Metro lists a trip in 3 min, no bus located yet')
  })

  it('names the timetable as the source when it has to', () => {
    const plan = planWatch([dep({ scheduled: 1180 }), dep({ vehicleId: 'a', v1: 1900 })], NOW)
    expect(plan.note).toBe('The timetable lists a trip in 3 min, no bus located yet')
  })

  it('says due now rather than a zero', () => {
    const plan = planWatch([dep({ metro: 1010 }), dep({ vehicleId: 'a', v1: 1900 })], NOW)
    expect(plan.note).toBe('Metro lists a trip due now, no bus located yet')
  })

  it('says what comes after a lone bus', () => {
    const timetable = planWatch([dep({ vehicleId: 'a', v1: 1240 }), dep({ scheduled: 2000 })], NOW)
    expect(timetable.second).toBeNull()
    expect(timetable.note).toBe(`After this one: ${clock(2000)}, timetable`)
    const metro = planWatch([dep({ vehicleId: 'a', v1: 1240 }), dep({ metro: 2000, scheduled: 1900 })], NOW)
    expect(metro.note).toBe(`After this one: ${clock(2000)}, Metro`)
  })

  it('says nothing about a trip with no bus between two buses', () => {
    const plan = planWatch([dep({ vehicleId: 'a', v1: 1240 }), dep({ metro: 1500 }), dep({ vehicleId: 'b', v1: 1960 })], NOW)
    expect(plan.note).toBeNull()
  })

  it('prefers the note about the earlier trip', () => {
    const both = planWatch([dep({ metro: 1180 }), dep({ vehicleId: 'a', v1: 1900 }), dep({ scheduled: 3000 })], NOW)
    expect(both.note).toContain('3 min')
    const noEstimate = planWatch([dep({ metro: 1180 }), dep({ vehicleId: 'a', scheduled: 1900 })], NOW)
    expect(noEstimate.note).toContain('3 min')
  })

  it('never shows a bubble with no bus on the road', () => {
    const t = 1500
    const plan = planWatch([dep({ scheduled: t })], NOW)
    expect(plan.first).toBeNull()
    expect(plan.second).toBeNull()
    expect(plan.note).toBe(`No bus on the road yet. Next: ${clock(t)}, timetable`)
  })

  it('says when nothing is due', () => {
    const plan = planWatch([], NOW)
    expect(plan.first).toBeNull()
    expect(plan.note).toBe('Nothing due here in the next 90 minutes')
  })
})

describe('minutesLabel', () => {
  it('matches the countdown wording', () => {
    expect(minutesLabel(NOW + 240, NOW)).toBe('4 min')
    expect(minutesLabel(NOW + 20, NOW)).toBe('Due')
    expect(minutesLabel(NOW - 30, NOW)).toBe('Due')
  })
})

describe('selectionHash and parseHash', () => {
  const watch = { kind: 'watch' as const, stopId: '56/7', routeId: '21', directionId: 0 }

  it('round-trips a watch, encoding ids that need it', () => {
    expect(selectionHash(watch)).toBe('#watch/56%2F7/21/0')
    expect(parseHash('#watch/56%2F7/21/0')).toEqual(watch)
  })

  it('still reads the existing kinds', () => {
    expect(parseHash('#stop/1')).toEqual({ kind: 'stop', id: '1' })
    expect(parseHash('#vehicle/2')).toEqual({ kind: 'vehicle', id: '2' })
    expect(selectionHash({ kind: 'stop', id: '1' })).toBe('#stop/1')
    expect(selectionHash(null)).toBe('')
  })

  it('rejects broken links', () => {
    expect(parseHash('#watch/1/21')).toBeNull()
    expect(parseHash('#watch/1/21/x')).toBeNull()
    expect(parseHash('#watch/%E0%A4%A/21/0')).toBeNull()
    expect(parseHash('')).toBeNull()
  })
})

describe('findLine', () => {
  const route: Route = { id: '21', shortName: '21', longName: 'Lake', color: '', textColor: '', type: 3 }
  const line: StopLine = { route, directionId: 1, label: 'West', shapeId: 'S' }
  const watch = { stopId: '1', routeId: '21', directionId: 1 }

  it('finds the watched route and direction', () => expect(findLine([line], watch)).toBe(line))
  it('is null when the stop no longer serves that direction', () => expect(findLine([line], { ...watch, directionId: 0 })).toBeNull())
  it('is null when the stop no longer serves that route', () => expect(findLine([line], { ...watch, routeId: '6' })).toBeNull())
})

describe('dataAge', () => {
  const NOW_MS = 1_000_000
  const secondsAgo = (s: number) => NOW_MS - s * 1000
  const fresh = { connected: true, lastMessageAt: secondsAgo(10), behindBy: 30 }

  it('is null while everything is fresh', () => expect(dataAge(fresh, NOW_MS, secondsAgo(10))).toBeNull())
  it('catches a stream that has gone quiet while still saying connected', () =>
    expect(dataAge({ ...fresh, lastMessageAt: secondsAgo(90) }, NOW_MS, secondsAgo(10))).toBe(90))
  it('catches a dropped connection', () => expect(dataAge({ ...fresh, connected: false, lastMessageAt: secondsAgo(40) }, NOW_MS, secondsAgo(10))).toBe(40))
  it('ignores a brief drop', () => expect(dataAge({ ...fresh, connected: false, lastMessageAt: secondsAgo(5) }, NOW_MS, secondsAgo(5))).toBeNull())
  it('catches a map running far behind', () => expect(dataAge({ ...fresh, behindBy: 200 }, NOW_MS, secondsAgo(10))).toBe(200))
  it('catches departures that stopped updating', () => expect(dataAge(fresh, NOW_MS, secondsAgo(90))).toBe(90))
  it('is null before anything has arrived', () => expect(dataAge({ connected: false, lastMessageAt: 0, behindBy: 15 }, NOW_MS, 0)).toBeNull())
})

describe('busIsStale', () => {
  it('allows two minutes without a ping', () => {
    expect(busIsStale(NOW - 100, NOW)).toBe(false)
    expect(busIsStale(NOW - 121, NOW)).toBe(true)
  })
})

describe('shouldFrameSecond', () => {
  it('frames the second bus unless that shrinks the view too far', () => {
    expect(shouldFrameSecond(14, 14.5)).toBe(true)
    expect(shouldFrameSecond(12.4, 15)).toBe(false)
    expect(shouldFrameSecond(12.4, 12.9)).toBe(true)
  })
})

describe('edgePoint', () => {
  const size = { width: 400, height: 300 }

  it('stops at the edge along the line toward the target', () => {
    expect(edgePoint({ x: 200, y: 150 }, { x: 800, y: 150 }, size, 40)).toEqual({ x: 360, y: 150 })
    const p = edgePoint({ x: 200, y: 150 }, { x: 600, y: 350 }, size, 40)
    expect(p.x).toBe(360)
    expect(p.y).toBeCloseTo(230)
  })

  it('leaves a target on screen alone', () => {
    expect(edgePoint({ x: 200, y: 150 }, { x: 300, y: 100 }, size, 40)).toEqual({ x: 300, y: 100 })
  })
})
