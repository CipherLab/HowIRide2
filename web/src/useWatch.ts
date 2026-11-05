import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { api, type Stop, type StopLine } from './api'
import { useNow } from './components'
import type { LiveVehicles } from './live'
import { dataAge, findLine, planWatch, type Watch, type WatchPlan } from './watch'

export interface WatchView {
  watch: Watch
  stop: Stop | null
  line: StopLine | null
  plan: WatchPlan | null
  /** Seconds, once the live data is old enough to say so. */
  dataAge: number | null
  /** The stop couldn't be loaded, or a saved link names a route that no longer stops here. */
  failed: 'load' | 'no-line' | null
}

const REFRESH_MS = 10_000

/** Everything the watching screen needs about one stop, route and direction. */
export function useWatch(watch: Watch | null, live: LiveVehicles): WatchView | null {
  const now = useNow()
  const departures = useQuery({
    queryKey: ['watch', watch?.stopId, watch?.routeId, watch?.directionId],
    queryFn: () => api.watchStop(watch!),
    enabled: watch != null,
    refetchInterval: REFRESH_MS,
  })
  const lines = useQuery({
    queryKey: ['lines', watch?.stopId],
    queryFn: () => api.lines(watch!.stopId),
    enabled: watch != null,
    staleTime: 3_600_000,
  })
  const data = watch ? departures.data : undefined
  const plan = useMemo(() => (data ? planWatch(data.departures, now) : null), [data, now])
  if (!watch) return null

  const line = lines.data ? findLine(lines.data, watch) : null
  const failed = departures.isError && !data ? 'load' : lines.data && !line ? 'no-line' : null
  return {
    watch,
    stop: data?.stop ?? null,
    line,
    plan,
    dataAge: dataAge({ connected: live.connected, lastMessageAt: live.lastMessageAt, behindBy: live.behindBy() }, now * 1000, departures.dataUpdatedAt),
    failed,
  }
}
