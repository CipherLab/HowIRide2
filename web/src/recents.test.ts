import { describe, expect, it } from 'vitest'
import { loadRecents, saveRecent, type Recent } from './recents'

function memory(initial: string | null = null) {
  let value = initial
  return { getItem: () => value, setItem: (_: string, v: string) => void (value = v) }
}

const recent = (stopId: string, directionId = 0): Recent => ({
  stopId,
  routeId: '21',
  directionId,
  stopName: `Stop ${stopId}`,
  route: '21',
  color: '0053A0',
  textColor: 'FFFFFF',
  label: 'Uptown',
})

describe('saveRecent', () => {
  it('puts the newest first', () => {
    const s = memory()
    saveRecent(recent('1'), s)
    expect(saveRecent(recent('2'), s).map((r) => r.stopId)).toEqual(['2', '1'])
    expect(loadRecents(s).map((r) => r.stopId)).toEqual(['2', '1'])
  })

  it('moves a repeat to the top with no duplicate', () => {
    const s = memory()
    saveRecent(recent('1'), s)
    saveRecent(recent('2'), s)
    expect(saveRecent(recent('1'), s).map((r) => r.stopId)).toEqual(['1', '2'])
  })

  it('keeps the same stop in another direction separately', () => {
    const s = memory()
    saveRecent(recent('1', 0), s)
    expect(saveRecent(recent('1', 1), s)).toHaveLength(2)
  })

  it('drops the oldest past five', () => {
    const s = memory()
    for (const id of ['1', '2', '3', '4', '5', '6']) saveRecent(recent(id), s)
    expect(loadRecents(s).map((r) => r.stopId)).toEqual(['6', '5', '4', '3', '2'])
  })
})

describe('loadRecents', () => {
  it('is empty with nothing stored', () => expect(loadRecents(memory())).toEqual([]))
  it('is empty for invalid JSON', () => expect(loadRecents(memory('{nope'))).toEqual([]))
  it('is empty for something other than a list', () => expect(loadRecents(memory('{"a":1}'))).toEqual([]))
  it('keeps only the valid entries', () => {
    const stored = JSON.stringify([recent('1'), { stopId: '2' }, null, 7])
    expect(loadRecents(memory(stored)).map((r) => r.stopId)).toEqual(['1'])
  })
})

describe('storage that refuses', () => {
  const refusing = {
    getItem: () => {
      throw new Error('denied')
    },
    setItem: () => {
      throw new Error('denied')
    },
  }

  it('reads as empty', () => expect(loadRecents(refusing)).toEqual([]))
  it('still returns the updated list', () => expect(saveRecent(recent('1'), refusing)).toEqual([recent('1')]))
})
