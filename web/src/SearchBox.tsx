import { useQuery } from '@tanstack/react-query'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { api, type Route, type Stop } from './api'
import { RouteBadge } from './components'
import { boxAround, NEARBY_METRES, NEARBY_WIDER_METRES, nearbyRoutes } from './nearby'
import { loadRecents, saveRecent, type Recent } from './recents'
import { closestStop, matchRoutes, type Place, type Step } from './searchSteps'
import type { WatchView } from './useWatch'
import type { Watch } from './watch'

const TYPING_MS = 200
const HOME: Step = { at: 'home' }

type Located = Place & { source: 'you' | 'map' }

/** Stops and routes to watch at the top right of the map, turning into the watched stop's title while watching. */
export function SearchBox({
  watching,
  onWatch,
  onStop,
  autoView,
  onAutoView,
  listOpen,
  onListOpen,
  mapCentre,
}: {
  watching: WatchView | null
  onWatch: (w: Watch) => void
  onStop: () => void
  /** The map is framing the stop and buses by itself; when false, offer to go back to that. */
  autoView: boolean
  onAutoView: () => void
  listOpen: boolean
  onListOpen: (open: boolean) => void
  mapCentre: () => Place
}) {
  const [text, setText] = useState('')
  const [typed, setTyped] = useState('')
  const [step, setStep] = useState<Step>(HOME)
  const [recents, setRecents] = useState(() => loadRecents())
  const [place, setPlace] = useState<Located | null>(null)
  const head = useRef<HTMLDivElement>(null)

  const isWatching = watching != null
  // The map's buttons and camera keep clear of however tall the box is right now.
  useLayoutEffect(() => {
    const el = head.current
    if (!el) return
    const root = document.documentElement.style
    const observer = new ResizeObserver(() => root.setProperty('--search-h', `${el.offsetHeight}px`))
    observer.observe(el)
    return () => {
      observer.disconnect()
      root.removeProperty('--search-h')
    }
    // The box is a different element while watching.
  }, [isWatching])

  useEffect(() => {
    const id = setTimeout(() => setTyped(text.trim()), TYPING_MS)
    return () => clearTimeout(id)
  }, [text])

  const start = (stop: { id: string; name: string }, route: Route, directionId: number, label: string) => {
    const entry: Recent = {
      stopId: stop.id,
      routeId: route.id,
      directionId,
      stopName: stop.name,
      route: route.shortName,
      color: route.color,
      textColor: route.textColor,
      label,
    }
    setRecents(saveRecent(entry))
    close()
    onWatch({ stopId: stop.id, routeId: route.id, directionId })
  }

  const close = () => {
    onListOpen(false)
    setStep(HOME)
    setText('')
    setTyped('')
  }

  // Asked for here, on tapping the box, and not when the page loads. Until (or unless) it's given, the map's middle stands in.
  const locate = () => {
    onListOpen(true)
    if (place?.source === 'you') return
    setPlace({ ...mapCentre(), source: 'map' })
    navigator.geolocation?.getCurrentPosition(
      (p) => setPlace({ lat: p.coords.latitude, lon: p.coords.longitude, source: 'you' }),
      () => {},
      { timeout: 8000, maximumAge: 60_000 },
    )
  }

  if (watching)
    return (
      <div className="search" ref={head} role="region" aria-label="Stop you're watching">
        <WatchTitle view={watching} recents={recents} onStop={onStop} autoView={autoView} onAutoView={onAutoView} />
      </div>
    )

  return (
    <div
      className="search"
      role="search"
      onKeyDown={(e) => {
        if (e.key === 'Escape' && (listOpen || text)) {
          e.stopPropagation()
          close()
        }
      }}
    >
      <div ref={head}>
        <input
          className="search-input"
          type="search"
          value={text}
          placeholder="Search stops and routes"
          aria-label="Search stops and routes"
          onFocus={locate}
          onClick={() => onListOpen(true)}
          onChange={(e) => {
            setText(e.target.value)
            setStep(HOME)
            onListOpen(true)
          }}
        />
      </div>
      {listOpen && (
        <div className="search-list">
          {step.at !== 'home' && (
            <button className="search-back" onClick={() => setStep(HOME)}>
              ← Back
            </button>
          )}
          {step.at === 'directions' ? (
            <Directions step={step} place={place} onPick={setStep} />
          ) : step.at === 'stops' ? (
            <StopsOfDirection step={step} source={place?.source ?? null} onPick={(stop) => start(stop, step.route, step.direction.directionId, step.direction.label)} />
          ) : step.at === 'lines' ? (
            <LinesAtStop stop={step.stop} onPick={start} />
          ) : typed ? (
            <Results query={typed} onRoute={(route) => setStep({ at: 'directions', route })} onStop={(stop) => setStep({ at: 'lines', stop })} />
          ) : (
            <>
              {recents.length > 0 && (
                <>
                  <h3 className="search-label">Recent</h3>
                  <ul>
                    {recents.map((r) => (
                      <li key={`${r.stopId}/${r.routeId}/${r.directionId}`}>
                        <button
                          className="search-row"
                          onClick={() => {
                            setRecents(saveRecent(r))
                            close()
                            onWatch({ stopId: r.stopId, routeId: r.routeId, directionId: r.directionId })
                          }}
                        >
                          <RouteBadge route={r.route} color={r.color} textColor={r.textColor} />
                          <span>
                            {r.stopName}
                            <small>{r.label}</small>
                          </span>
                        </button>
                      </li>
                    ))}
                  </ul>
                </>
              )}
              {place && <NearbyList place={place} onPick={(route) => setStep({ at: 'directions', route })} />}
            </>
          )}
        </div>
      )}
    </div>
  )
}

function WatchTitle({
  view,
  recents,
  onStop,
  autoView,
  onAutoView,
}: {
  view: WatchView
  recents: Recent[]
  onStop: () => void
  autoView: boolean
  onAutoView: () => void
}) {
  const { watch, line, stop, plan, dataAge, failed } = view
  // A recent knows the names before the first request comes back, so reopening doesn't flash empty.
  const saved = recents.find((r) => r.stopId === watch.stopId && r.routeId === watch.routeId && r.directionId === watch.directionId)
  const route = line?.route.shortName ?? saved?.route
  const stopButton = (
    <button className="search-stop" onClick={onStop} aria-label="Stop watching" title="Stop watching">
      <span aria-hidden="true">×</span>
    </button>
  )
  if (failed)
    return (
      <div className="watch-title">
        <p className="watch-note">{failed === 'load' ? "Couldn't load this stop." : "This stop no longer has that route."}</p>
        {stopButton}
      </div>
    )
  return (
    <>
      <div className="watch-title">
        {route && <RouteBadge route={route} color={line?.route.color ?? saved?.color} textColor={line?.route.textColor ?? saved?.textColor} />}
        <div className="watch-names">
          <strong>{stop?.name ?? saved?.stopName ?? 'Loading…'}</strong>
          <small>{line?.label ?? saved?.label}</small>
        </div>
        {stopButton}
      </div>
      {dataAge != null && <p className="watch-note stale">Live data is {Math.max(1, Math.round(dataAge / 60))} min old</p>}
      {plan?.note && <p className="watch-note">{plan.note}</p>}
      {!autoView && (
        <button className="watch-recentre" onClick={onAutoView}>
          Recentre
        </button>
      )}
    </>
  )
}

function NearbyList({ place, onPick }: { place: Located; onPick: (route: Route) => void }) {
  // Rounded so small GPS changes don't refetch: about 100 m.
  const key = [place.lat.toFixed(3), place.lon.toFixed(3)]
  const nearby = useQuery({
    queryKey: ['nearby', ...key],
    queryFn: async () => {
      const close = await api.stops(...boxAround(place, NEARBY_METRES))
      return close.length > 0 ? close : api.stops(...boxAround(place, NEARBY_WIDER_METRES))
    },
    staleTime: 3_600_000,
  })
  const routes = nearby.data ? nearbyRoutes(nearby.data, place) : []
  return (
    <>
      <h3 className="search-label">{place.source === 'you' ? 'Routes near you' : 'Routes near the middle of the map'}</h3>
      {nearby.isPending ? (
        <p className="search-empty">Looking…</p>
      ) : nearby.isError ? (
        <p className="search-empty">Couldn't load nearby routes.</p>
      ) : routes.length === 0 ? (
        <p className="search-empty">No routes close by. Try typing a route or stop.</p>
      ) : (
        <RouteList routes={routes.slice(0, 12)} onPick={onPick} />
      )}
    </>
  )
}

function RouteList({ routes, onPick }: { routes: Route[]; onPick: (route: Route) => void }) {
  return (
    <ul>
      {routes.map((r) => (
        <li key={r.id}>
          <button className="search-row" onClick={() => onPick(r)}>
            <RouteBadge route={r.shortName} color={r.color} textColor={r.textColor} />
            <span>{r.longName || `Route ${r.shortName}`}</span>
          </button>
        </li>
      ))}
    </ul>
  )
}

function Results({ query, onRoute, onStop }: { query: string; onRoute: (route: Route) => void; onStop: (stop: Stop) => void }) {
  const routes = useQuery({ queryKey: ['routes'], queryFn: api.routes, staleTime: 3_600_000 })
  const stops = useQuery({ queryKey: ['stop-search', query], queryFn: () => api.searchStops(query) })
  const matching = routes.data ? matchRoutes(routes.data, query).slice(0, 6) : []
  if (routes.isPending && stops.isPending) return <p className="search-empty">Searching…</p>
  if (matching.length === 0 && stops.data?.length === 0) return <p className="search-empty">Nothing matches “{query}”.</p>
  return (
    <>
      {matching.length > 0 && (
        <>
          <h3 className="search-label">Routes</h3>
          <RouteList routes={matching} onPick={onRoute} />
        </>
      )}
      {stops.isError && <p className="search-empty">Couldn't search stops.</p>}
      {stops.data && stops.data.length > 0 && (
        <>
          <h3 className="search-label">Stops</h3>
          <ul>
            {stops.data.map((s) => (
              <li key={s.id}>
                <button className="search-row" onClick={() => onStop(s)}>
                  <span>
                    {s.name}
                    <small>Stop #{s.id}</small>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
    </>
  )
}

function Directions({ step, place, onPick }: { step: Extract<Step, { at: 'directions' }>; place: Place | null; onPick: (step: Step) => void }) {
  const directions = useQuery({ queryKey: ['directions', step.route.id], queryFn: () => api.directions(step.route.id), staleTime: 3_600_000 })
  // Nothing to choose between on a route that only runs one way.
  const only = directions.data?.length === 1 ? directions.data[0] : null
  useEffect(() => {
    if (only) onPick({ at: 'stops', route: step.route, direction: only, near: place })
  }, [only, onPick, step.route, place])
  if (directions.isPending) return <p className="search-empty">Loading…</p>
  if (directions.isError) return <p className="search-empty">Couldn't load this route.</p>
  return (
    <>
      <h3 className="search-label">
        <RouteBadge route={step.route.shortName} color={step.route.color} textColor={step.route.textColor} /> Which direction?
      </h3>
      <ul>
        {directions.data.map((d) => (
          <li key={d.directionId}>
            <button className="search-row" onClick={() => onPick({ at: 'stops', route: step.route, direction: d, near: place })}>
              <span>{d.label}</span>
            </button>
          </li>
        ))}
      </ul>
    </>
  )
}

function StopsOfDirection({
  step,
  source,
  onPick,
}: {
  step: Extract<Step, { at: 'stops' }>
  source: Located['source'] | null
  onPick: (stop: Stop) => void
}) {
  const closest = step.near ? closestStop(step.direction.stops, step.near) : null
  const closestRow = useRef<HTMLLIElement>(null)
  // Routes can have over a hundred stops; start at the one that's probably theirs.
  useEffect(() => {
    closestRow.current?.scrollIntoView({ block: 'center' })
  }, [])
  return (
    <>
      <h3 className="search-label">
        <RouteBadge route={step.route.shortName} color={step.route.color} textColor={step.route.textColor} /> {step.direction.label}
      </h3>
      <ul>
        {step.direction.stops.map((s) => (
          <li key={s.id} ref={s.id === closest ? closestRow : undefined}>
            <button className={s.id === closest ? 'search-row closest' : 'search-row'} onClick={() => onPick(s)}>
              <span>
                {s.name}
                {s.id === closest && <small>{source === 'you' ? 'Closest to you' : 'Closest to the middle of the map'}</small>}
              </span>
            </button>
          </li>
        ))}
      </ul>
    </>
  )
}

function LinesAtStop({ stop, onPick }: { stop: Stop; onPick: (stop: Stop, route: Route, directionId: number, label: string) => void }) {
  const lines = useQuery({ queryKey: ['lines', stop.id], queryFn: () => api.lines(stop.id), staleTime: 3_600_000 })
  const only = lines.data?.length === 1 ? lines.data[0] : null
  useEffect(() => {
    if (only) onPick(stop, only.route, only.directionId, only.label)
    // Only when the answer arrives: onPick changes every render, and picking twice would add nothing.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [only, stop])
  if (lines.isPending || only) return <p className="search-empty">Loading…</p>
  if (lines.isError) return <p className="search-empty">Couldn't load this stop.</p>
  if (lines.data.length === 0) return <p className="search-empty">Nothing can be watched from this stop.</p>
  return (
    <>
      <h3 className="search-label">{stop.name}: which route?</h3>
      <ul>
        {lines.data.map((l) => (
          <li key={`${l.route.id}/${l.directionId}`}>
            <button className="search-row" onClick={() => onPick(stop, l.route, l.directionId, l.label)}>
              <RouteBadge route={l.route.shortName} color={l.route.color} textColor={l.route.textColor} />
              <span>{l.label}</span>
            </button>
          </li>
        ))}
      </ul>
    </>
  )
}
