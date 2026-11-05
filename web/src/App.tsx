import { useQuery } from '@tanstack/react-query'
import { Component, useCallback, useEffect, useState, type ReactNode } from 'react'
import { api } from './api'
import { RouteBadge } from './components'
import { LiveVehicles } from './live'
import { MapView, type Selection } from './MapView'
import { Charts } from './charts'
import { LiveStatus, Overview, Scorecard, StopPanel, useLive, VehiclePanel } from './panels'

type View = 'map' | 'scores' | 'charts'

const VIEW_HASH: Partial<Record<View, string>> = { scores: '#scorecard', charts: '#charts' }

const COLLAPSED_KEY = 'panel-collapsed'

function loadCollapsed() {
  try {
    return localStorage.getItem(COLLAPSED_KEY) === '1'
  } catch {
    return false
  }
}

function fromHash(): Selection {
  const [kind, id] = location.hash.slice(1).split('/')
  return (kind === 'stop' || kind === 'vehicle') && id ? { kind, id: decodeURIComponent(id) } : null
}

/** Without WebGL the map throws as it starts. That shouldn't take the panel down with it, since most of the panel doesn't need a map. */
class MapBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  render() {
    if (!this.state.failed) return this.props.children
    return (
      <div className="map map-failed" role="status">
        <p>The map can't start here because this browser has WebGL turned off. Everything in the panel still works.</p>
      </div>
    )
  }
}

export default function App() {
  const [live] = useState(() => new LiveVehicles())
  const [selection, setSelection] = useState<Selection>(fromHash)
  const [view, setView] = useState<View>(
    () => (Object.entries(VIEW_HASH).find(([, h]) => h === location.hash)?.[0] as View | undefined) ?? 'map',
  )
  // Picking a vehicle follows it until the user drags the map away.
  const [follow, setFollow] = useState(true)
  // The panel folds into a tab on the left (a bar along the bottom on phones) to give the map the whole screen.
  const [collapsed, setCollapsed] = useState(loadCollapsed)
  useEffect(() => {
    try {
      localStorage.setItem(COLLAPSED_KEY, collapsed ? '1' : '0')
    } catch {
      // Private windows can refuse storage; the panel just opens expanded next time.
    }
  }, [collapsed])
  const open = (v: View) => {
    setView(v)
    setCollapsed(false)
  }
  const select = useCallback((s: Selection) => {
    setSelection(s)
    setView('map')
    if (s) setCollapsed(false)
    if (s?.kind === 'vehicle') setFollow(true)
  }, [])
  const following = follow && selection?.kind === 'vehicle'

  // The selection lives in the URL (#stop/123, #vehicle/456, #scorecard, #charts) so a view can be bookmarked or shared.
  useEffect(() => {
    const hash = VIEW_HASH[view] ?? (selection ? `#${selection.kind}/${selection.id}` : '')
    if (location.hash !== hash) history.replaceState(null, '', hash || location.pathname)
  }, [selection, view])

  useEffect(() => {
    live.start()
    // Debugging hook: inspect playback from devtools (transitLive.playbackTime(), transitLive.behindBy()).
    ;(window as unknown as { transitLive?: LiveVehicles }).transitLive = live
    return () => live.stop()
  }, [live])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setSelection(null)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <div className="app">
      <MapBoundary>
        <MapView live={live} selection={selection} onSelect={select} follow={following} onFollowChange={setFollow} />
      </MapBoundary>
      {collapsed ? (
        <nav className="rail" aria-label="Details panel, hidden">
          <button className="rail-open" onClick={() => setCollapsed(false)} aria-label="Show panel" title="Show panel">
            <span aria-hidden="true">›</span>
          </button>
          <button className="rail-tab" aria-current={view === 'map' ? 'page' : undefined} onClick={() => open('map')}>
            Live
          </button>
          <button className="rail-tab" aria-current={view === 'scores' ? 'page' : undefined} onClick={() => open('scores')}>
            Scorecard
          </button>
          <button className="rail-tab" aria-current={view === 'charts' ? 'page' : undefined} onClick={() => open('charts')}>
            Charts
          </button>
          <span className="rail-mark" aria-hidden="true">
            TC ETA
          </span>
        </nav>
      ) : (
        <aside className={view === 'charts' ? 'panel wide' : 'panel'} aria-label="Details">
          <header className="panel-head">
            <div className="wordmark">
              <h1>TWIN CITIES ETA</h1>
              <nav className="nav">
                <button aria-current={view === 'map' ? 'page' : undefined} onClick={() => setView('map')}>
                  Live
                </button>
                <button aria-current={view === 'scores' ? 'page' : undefined} onClick={() => setView('scores')}>
                  Scorecard
                </button>
                <button aria-current={view === 'charts' ? 'page' : undefined} onClick={() => setView('charts')}>
                  Charts
                </button>
                <button className="collapse" onClick={() => setCollapsed(true)} aria-label="Hide panel" title="Hide panel">
                  <span aria-hidden="true">‹</span>
                </button>
              </nav>
            </div>
            <LiveStatus live={live} />
            {view === 'map' && (
              <Heading selection={selection} live={live} onBack={() => setSelection(null)} following={following} onFollowChange={setFollow} />
            )}
            {view === 'scores' && <h2 className="title">How accurate are the predictions?</h2>}
            {view === 'charts' && <h2 className="title">How our estimates compare with Metro Transit’s</h2>}
          </header>
          <div className="panel-body">
            {view === 'scores' ? (
              <Scorecard />
            ) : view === 'charts' ? (
              <Charts />
            ) : selection?.kind === 'vehicle' ? (
              <VehiclePanel id={selection.id} live={live} onSelect={select} />
            ) : selection?.kind === 'stop' ? (
              <StopPanel id={selection.id} onSelect={select} />
            ) : (
              <Overview live={live} />
            )}
          </div>
        </aside>
      )}
    </div>
  )
}

function Heading({
  selection,
  live,
  onBack,
  following,
  onFollowChange,
}: {
  selection: Selection
  live: LiveVehicles
  onBack: () => void
  following: boolean
  onFollowChange: (follow: boolean) => void
}) {
  useLive(live)
  const stop = useQuery({
    queryKey: ['stop', selection?.kind === 'stop' ? selection.id : ''],
    queryFn: () => api.stop(selection!.id),
    enabled: selection?.kind === 'stop',
    refetchInterval: 15_000,
  })
  if (!selection) return <h2 className="title">Where every bus and train is, and when it will actually leave</h2>
  const back = (
    <button className="back" onClick={onBack} aria-label="Close and show overview">
      ← All vehicles
    </button>
  )
  if (selection.kind === 'stop')
    return (
      <>
        {back}
        <h2 className="title">{stop.data?.stop.name ?? 'Stop'}</h2>
        <p className="subtitle">Stop #{selection.id}</p>
        {stop.data && stop.data.stop.routes.length > 0 && (
          <p className="served" aria-label="Routes serving this stop">
            {stop.data.stop.routes.map((r) => (
              <RouteBadge key={r.id} route={r.shortName} color={r.color} textColor={r.textColor} />
            ))}
          </p>
        )}
      </>
    )
  const v = live.vehicle(selection.id)
  return (
    <>
      <div className="head-actions">
        {back}
        <button className="follow" aria-pressed={following} onClick={() => onFollowChange(!following)}>
          {following ? 'Following' : 'Follow on map'}
        </button>
      </div>
      <h2 className="title">
        {v?.route ? <RouteBadge route={v.route} color={v.color} textColor={v.textColor} /> : null} {v?.headsign ?? `Vehicle ${selection.id}`}
      </h2>
      <p className="subtitle">Vehicle {selection.id}</p>
    </>
  )
}
