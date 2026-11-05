import { describe, expect, it } from 'vitest'
import type { Route, Stop } from './api'
import { closestStop, matchRoutes } from './searchSteps'

const stop = (id: string, lat: number): Stop => ({ id, name: id, lat, lon: -93, routes: [] })
const route = (shortName: string, longName = ''): Route => ({ id: shortName, shortName, longName, color: '', textColor: '', type: 3 })

describe('closestStop', () => {
  it('picks the nearest', () => expect(closestStop([stop('far', 45.1), stop('near', 45.01)], { lat: 45, lon: -93 })).toBe('near'))
  it('is null with no stops', () => expect(closestStop([], { lat: 45, lon: -93 })).toBeNull())
})

describe('matchRoutes', () => {
  const routes = [route('210'), route('21A'), route('21'), route('5', 'Lake Street'), route('Blue', 'Blue Line')]

  it('ranks an exact number first, then those starting with it, in number order', () =>
    expect(matchRoutes(routes, '21').map((r) => r.shortName)).toEqual(['21', '21A', '210']))
  it('matches the long name ignoring case', () => expect(matchRoutes(routes, 'lake').map((r) => r.shortName)).toEqual(['5']))
  it('matches a short name ignoring case', () => expect(matchRoutes(routes, 'blue').map((r) => r.shortName)).toEqual(['Blue']))
  it('finds nothing for a blank query', () => {
    expect(matchRoutes(routes, '')).toEqual([])
    expect(matchRoutes(routes, '   ')).toEqual([])
  })
})
