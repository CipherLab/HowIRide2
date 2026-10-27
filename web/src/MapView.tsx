import { GeolocateControl, Map as MapLibre, NavigationControl, setWorkerUrl, type ExpressionSpecification, type GeoJSONSource, type MapLayerMouseEvent } from 'maplibre-gl'
import mapWorkerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?worker&url'
import type { FeatureCollection } from 'geojson'
import { useEffect, useRef } from 'react'
import { api, isRapid } from './api'
import { PACE_COLORS } from './components'
import type { LiveVehicles } from './live'
import { ARROW_ICON, arrowIcon, STOP_ICON, stopIcon } from './vehicleIcon'

// MapLibre 6 runs tile work in a separate worker file; bundlers must ship it and say where it is.
setWorkerUrl(mapWorkerUrl)

export type Selection = { kind: 'vehicle'; id: string } | { kind: 'stop'; id: string } | null

const STOPS_MIN_ZOOM = 14.5
const FRAME_MS = 100
/** Keep the side panel (or bottom sheet) from covering what we fly to. */
function panelPadding() {
  return window.innerWidth > 720 ? { left: 420, top: 0, right: 0, bottom: 0 } : { left: 0, top: 0, right: 0, bottom: window.innerHeight * 0.58 }
}

const EMPTY: FeatureCollection = { type: 'FeatureCollection', features: [] }

/**
 * Fill is today's pace on the road ahead (amber slower than usual, blue faster); most Metro Transit bus
 * routes share one brand colour, so pace is the more informative fill. The route colour is kept as the outline.
 */
const paceFill: ExpressionSpecification = [
  'case',
  ['!', ['get', 'onTrip']], '#A9B5BE',
  ['==', ['get', 'pace'], null], PACE_COLORS.normal,
  ['>=', ['get', 'pace'], 1.15], PACE_COLORS.slow,
  ['<=', ['get', 'pace'], 0.9], PACE_COLORS.fast,
  PACE_COLORS.normal,
]

export function MapView({ live, selection, onSelect }: { live: LiveVehicles; selection: Selection; onSelect: (s: Selection) => void }) {
  const container = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibre | null>(null)
  const selectionRef = useRef(selection)
  selectionRef.current = selection

  useEffect(() => {
    const map = new MapLibre({
      container: container.current!,
      style: 'https://tiles.openfreemap.org/styles/positron',
      center: [-93.235, 44.965],
      zoom: 11.3,
      attributionControl: { customAttribution: 'Transit data: Metro Transit' },
    })
    mapRef.current = map
    map.addControl(new NavigationControl({ showCompass: false }), 'top-right')
    map.addControl(new GeolocateControl({ trackUserLocation: false }), 'top-right')

    map.on('load', () => {
      map.addSource('route', { type: 'geojson', data: EMPTY })
      map.addSource('stops', { type: 'geojson', data: EMPTY })
      map.addSource('vehicles', { type: 'geojson', data: EMPTY })

      map.addLayer({
        id: 'route-casing',
        type: 'line',
        source: 'route',
        paint: { 'line-color': '#FFFFFF', 'line-width': 9, 'line-opacity': 0.9 },
        layout: { 'line-cap': 'round', 'line-join': 'round' },
      })
      map.addLayer({
        id: 'route-line',
        type: 'line',
        source: 'route',
        paint: { 'line-color': ['get', 'color'], 'line-width': 5 },
        layout: { 'line-cap': 'round', 'line-join': 'round' },
      })
      // Stops are squares, so they can't be mistaken for vehicles or for Metro Transit's hollow ring in the
      // timelines. Coloured by the first route serving them: a rail or rapid-line colour if any, else the bus colour.
      map.addImage(STOP_ICON, stopIcon(), { sdf: true })
      map.addLayer({
        id: 'stops',
        type: 'symbol',
        source: 'stops',
        minzoom: STOPS_MIN_ZOOM,
        layout: {
          'icon-image': STOP_ICON,
          'icon-size': ['case', ['get', 'selected'], 1.2, ['get', 'rapid'], 1, 0.7],
          'icon-allow-overlap': true,
          'icon-ignore-placement': true,
          'text-field': ['step', ['zoom'], '', 16, ['get', 'label']],
          'text-font': ['Noto Sans Bold'],
          'text-size': 10,
          'text-anchor': 'left',
          'text-offset': [0.9, 0],
          'text-optional': true,
        },
        paint: {
          'icon-color': ['get', 'color'],
          'icon-halo-color': ['case', ['get', 'selected'], '#F2A900', '#FFFFFF'],
          'icon-halo-width': ['case', ['get', 'selected'], 3, 1.5],
          'text-color': '#14212B',
          'text-halo-color': '#FFFFFF',
          'text-halo-width': 1.5,
        },
      })
      // Vehicles with no direction (not on a trip, or shape still loading) stay small dots.
      map.addLayer({
        id: 'vehicle-idle',
        type: 'circle',
        source: 'vehicles',
        filter: ['!', ['has', 'bearing']],
        paint: {
          'circle-radius': ['interpolate', ['linear'], ['zoom'], 10, ['case', ['get', 'onTrip'], 4, 2.5], 14, ['case', ['get', 'onTrip'], 8, 4]],
          'circle-color': paceFill,
          'circle-opacity': ['case', ['get', 'onTrip'], 1, 0.6],
          'circle-stroke-color': ['case', ['get', 'selected'], '#14212B', ['get', 'color']],
          'circle-stroke-width': ['case', ['get', 'selected'], 3, ['case', ['get', 'onTrip'], 2, 0]],
        },
      })
      // Everything on a trip is a boxy arrow pointing along its route.
      map.addImage(ARROW_ICON, arrowIcon(), { sdf: true })
      map.addLayer({
        id: 'vehicle-arrow',
        type: 'symbol',
        source: 'vehicles',
        filter: ['has', 'bearing'],
        layout: {
          'icon-image': ARROW_ICON,
          'icon-rotate': ['get', 'bearing'],
          'icon-rotation-alignment': 'map',
          'icon-size': ['interpolate', ['linear'], ['zoom'], 10, ['case', ['get', 'selected'], 0.45, 0.3], 13, ['case', ['get', 'selected'], 0.7, 0.5], 16, ['case', ['get', 'selected'], 1, 0.85]],
          'icon-allow-overlap': true,
          'icon-ignore-placement': true,
          'symbol-sort-key': ['case', ['get', 'selected'], 1, 0],
        },
        paint: {
          'icon-color': paceFill,
          'icon-halo-color': ['case', ['get', 'selected'], '#14212B', ['get', 'color']],
          'icon-halo-width': ['case', ['get', 'selected'], 3, 2],
        },
      })
      map.addLayer({
        id: 'vehicle-label',
        type: 'symbol',
        source: 'vehicles',
        minzoom: 12.5,
        layout: {
          'text-field': ['get', 'route'],
          'text-font': ['Noto Sans Bold'],
          'text-size': ['case', ['>', ['length', ['get', 'route']], 3], 7, 10],
          'text-allow-overlap': true,
          'text-ignore-placement': true,
        },
        filter: ['get', 'onTrip'],
        paint: { 'text-color': ['case', ['>=', ['coalesce', ['get', 'pace'], 1], 1.15], '#14212B', '#FFFFFF'] },
      })

      for (const layer of ['vehicle-arrow', 'vehicle-idle', 'stops']) {
        map.on('mouseenter', layer, () => (map.getCanvas().style.cursor = 'pointer'))
        map.on('mouseleave', layer, () => (map.getCanvas().style.cursor = ''))
      }
      const pickVehicle = (e: MapLayerMouseEvent) => {
        const id = e.features?.[0]?.properties?.id
        if (id) onSelect({ kind: 'vehicle', id: String(id) })
      }
      map.on('click', 'vehicle-arrow', pickVehicle)
      map.on('click', 'vehicle-idle', pickVehicle)
      map.on('click', 'stops', (e: MapLayerMouseEvent) => {
        if (map.queryRenderedFeatures(e.point, { layers: ['vehicle-arrow', 'vehicle-idle'] }).length > 0) return
        const id = e.features?.[0]?.properties?.id
        if (id) onSelect({ kind: 'stop', id: String(id) })
      })

      const loadStops = () => {
        if (map.getZoom() < STOPS_MIN_ZOOM) return
        const b = map.getBounds()
        api.stops(b.getSouth(), b.getWest(), b.getNorth(), b.getEast()).then((stops) => {
          const selected = selectionRef.current?.kind === 'stop' ? selectionRef.current.id : null
          ;(map.getSource('stops') as GeoJSONSource).setData({
            type: 'FeatureCollection',
            features: stops.map((s) => ({
              type: 'Feature',
              geometry: { type: 'Point', coordinates: [s.lon, s.lat] },
              properties: {
                id: s.id,
                name: s.name,
                selected: s.id === selected,
                color: `#${s.routes[0]?.color ?? '7A8A96'}`,
                rapid: s.routes.some(isRapid),
                label: s.routes.length > 6 ? `${s.routes.slice(0, 6).map((r) => r.shortName).join(' · ')} +${s.routes.length - 6}` : s.routes.map((r) => r.shortName).join(' · '),
              },
            })),
          })
        })
      }
      map.on('moveend', loadStops)
      loadStops()
    })

    // Place every bus at playback time ~10 times a second.
    const timer = setInterval(() => {
      const source = map.getSource('vehicles') as GeoJSONSource | undefined
      if (!source) return
      const selected = selectionRef.current?.kind === 'vehicle' ? selectionRef.current.id : null
      source.setData({
        type: 'FeatureCollection',
        features: live.place().map((p) => ({
          type: 'Feature',
          geometry: { type: 'Point', coordinates: [p.lon, p.lat] },
          properties: {
            id: p.vehicle.id,
            route: p.vehicle.route ?? '',
            color: `#${p.vehicle.color ?? '4F5B66'}`,
            textColor: `#${p.vehicle.textColor ?? 'FFFFFF'}`,
            pace: p.vehicle.pace,
            onTrip: p.vehicle.dist != null,
            selected: p.vehicle.id === selected,
            // Direction of travel along the route shape; absent for vehicles not on a trip.
            ...(p.bearing != null && p.vehicle.dist != null ? { bearing: Math.round(p.bearing) } : {}),
          },
        })),
      })
    }, FRAME_MS)

    return () => {
      clearInterval(timer)
      map.remove()
    }
  }, [live, onSelect])

  // Draw the selected vehicle's route, and re-mark the selected stop.
  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    const apply = () => {
      const route = map.getSource('route') as GeoJSONSource | undefined
      if (!route) return
      const vehicle = selection?.kind === 'vehicle' ? live.vehicle(selection.id) : null
      const shape = vehicle ? live.shape(vehicle.shapeId) : null
      route.setData(
        shape
          ? {
              type: 'Feature',
              geometry: { type: 'LineString', coordinates: shape.lon.map((lon, i) => [lon, shape.lat[i]]) },
              properties: { color: `#${vehicle?.color ?? '4F5B66'}` },
            }
          : EMPTY,
      )
      map.fire('moveend')
    }
    if (map.isStyleLoaded()) apply()
    else map.once('load', apply)
  }, [selection, live])

  // Bring a newly selected stop or vehicle into view (also when arriving from a shared link).
  const selectedKey = selection ? `${selection.kind}/${selection.id}` : null
  useEffect(() => {
    const map = mapRef.current
    if (!map || !selection) return
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const go = (lon: number, lat: number) => {
      const zoom = Math.max(map.getZoom(), selection.kind === 'stop' ? 16 : 14)
      if (!map.getBounds().contains([lon, lat]) || map.getZoom() < zoom - 0.5)
        map.flyTo({ center: [lon, lat], zoom, essential: false, animate: !reduce, padding: panelPadding() })
    }
    if (selection.kind === 'stop') api.stop(selection.id).then((r) => go(r.stop.lon, r.stop.lat), () => {})
    else {
      const tryVehicle = (attempt: number) => {
        const v = live.vehicle(selection.id)
        if (v) go(v.lon, v.lat)
        else if (attempt < 20) setTimeout(() => tryVehicle(attempt + 1), 500)
      }
      tryVehicle(0)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedKey, live])

  return <div ref={container} className="map" aria-label="Live map of buses and trains" role="application" />
}
