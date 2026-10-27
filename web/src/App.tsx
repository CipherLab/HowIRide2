import { useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { RouteBadge } from './components'
import { LiveVehicles } from './live'
import { MapView, type Selection } from './MapView'
import { LiveStatus, Overview, Scorecard, StopPanel, useLive, VehiclePanel } from './panels'

type View = 'map' | 'scores'

function fromHash(): Selection {
  const [kind, id] = location.hash.slice(1).split('/')
  return (kind === 'stop' || kind === 'vehicle') && id ? { kind, id: decodeURIComponent(id) } : null
}

export default function App() {
  const [live] = useState(() => new LiveVehicles())
  const [selection, setSelection] = useState<Selection>(fromHash)
  const [view, setView] = useState<View>(() => (location.hash === '#scorecard' ? 'scores' : 'map'))
  const select = useCallback((s: Selection) => {
    setSelection(s)
    setView('map')
  }, [])

  // The selection lives in the URL (#stop/123, #vehicle/456, #scorecard) so a view can be bookmarked or shared.
  useEffect(() => {
    const hash = view === 'scores' ? '#scorecard' : selection ? `#${selection.kind}/${selection.id}` : ''
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
      <MapView live={live} selection={selection} onSelect={select} />
      <aside className="panel" aria-label="Details">
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
            </nav>
          </div>
          <LiveStatus live={live} />
          {view === 'map' && <Heading selection={selection} live={live} onBack={() => setSelection(null)} />}
          {view === 'scores' && <h2 className="title">How accurate are the predictions?</h2>}
        </header>
        <div className="panel-body">
          {view === 'scores' ? (
            <Scorecard />
          ) : selection?.kind === 'vehicle' ? (
            <VehiclePanel id={selection.id} live={live} onSelect={select} />
          ) : selection?.kind === 'stop' ? (
            <StopPanel id={selection.id} onSelect={select} />
          ) : (
            <Overview live={live} />
          )}
        </div>
      </aside>
    </div>
  )
}

function Heading({ selection, live, onBack }: { selection: Selection; live: LiveVehicles; onBack: () => void }) {
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
      {back}
      <h2 className="title">
        {v?.route ? <RouteBadge route={v.route} color={v.color} textColor={v.textColor} /> : null} {v?.headsign ?? `Vehicle ${selection.id}`}
      </h2>
      <p className="subtitle">Vehicle {selection.id}</p>
    </>
  )
}
