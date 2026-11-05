// The stops a rider has watched before, kept in this browser. They can come back malformed or be refused
// outright (a private window, an older version of this page), and the search box must still open.

import type { Watch } from './watch'

export interface Recent extends Watch {
  stopName: string
  route: string
  color: string
  textColor: string
  label: string
}

const KEY = 'recent-stops'
const MAX = 5

function browserStorage() {
  try {
    return localStorage
  } catch {
    return null
  }
}

function isRecent(r: unknown): r is Recent {
  if (typeof r !== 'object' || r === null) return false
  const x = r as Record<string, unknown>
  return (
    typeof x.directionId === 'number' &&
    [x.stopId, x.routeId, x.stopName, x.route, x.color, x.textColor, x.label].every((v) => typeof v === 'string')
  )
}

export function loadRecents(storage: Pick<Storage, 'getItem'> | null = browserStorage()): Recent[] {
  try {
    const stored: unknown = JSON.parse(storage?.getItem(KEY) ?? '[]')
    return Array.isArray(stored) ? stored.filter(isRecent).slice(0, MAX) : []
  } catch {
    return []
  }
}

export function saveRecent(entry: Recent, storage: Pick<Storage, 'getItem' | 'setItem'> | null = browserStorage()): Recent[] {
  const same = (r: Recent) => r.stopId === entry.stopId && r.routeId === entry.routeId && r.directionId === entry.directionId
  const list = [entry, ...loadRecents(storage).filter((r) => !same(r))].slice(0, MAX)
  try {
    storage?.setItem(KEY, JSON.stringify(list))
  } catch {
    // Refused; the rider just won't see this one next time.
  }
  return list
}
