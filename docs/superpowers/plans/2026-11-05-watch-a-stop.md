# Watch a Stop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A search box at the top right that takes a rider to a "watching" screen: their stop and the next two buses coming toward it, each with its time riding on it.

**Architecture:** The server gains route, direction and stop lookups (pure code in `src/Transit`, exposed through `LiveState`) and departures filtered by route and direction. The browser gets pure, unit-tested logic in `watch.ts`, `recents.ts` and `nearby.ts`, a `useWatch` hook that fetches, a `SearchBox` component, and a watching mode in `MapView`. Watching is a new `Selection` kind, replaced like any other selection.

**Tech Stack:** .NET 10, xunit, React 19, TanStack Query, MapLibre GL 6, Vite 8, Vitest 5 (new dev dependency).

**Spec:** `docs/superpowers/specs/2026-11-05-watch-a-stop-design.md`

## Global Constraints

- No bubble on the map ever shows a scheduled time. Bubbles use `v1 ?? v0 ?? metro` only.
- Trips with no bus attached only ever appear as one line of text, naming their source (Metro or timetable).
- Bubbles and the camera only use departures with a `vehicleId`.
- Watching is one route and one direction at one stop.
- Departures must be filtered on the server before the limit of 20 is applied.
- Whole minutes, with the wording of `Countdown` in `web/src/components.tsx`: `Math.round((at - now) / 60)`, `<= 0` is `Due`, otherwise `N min`.
- Recents: `localStorage`, at most 5, no duplicates, every read and write in `try`/`catch`.
- Existing map taps, stop panel and the Live, Scorecard and Charts tabs keep working as they do today outside watching.
- Don't overwrite the uncommitted work on `ui` (see Task 0).

## Review Focus

1. A loop route that visits a stop twice in one pattern: `/stops/{id}` must answer, not return a 500. Today `entries.ToDictionary(e => e.Pattern, ...)` in `LiveState.Departures` throws on the duplicate key. Test in Task 1.
2. A phone waking with the tab already open: the screen must not show old positions as current. Test in Task 3 (`dataAge`).
3. A single bus whose GPS has stopped reporting while the feed is healthy: its bubble must look stale. Test in Task 3 (`busIsStale`).
4. A saved link to a stop or route that a new timetable pick dropped: show a plain message and a way out, not a blank map. Test in Task 3 (`findLine`).
5. Old or malformed `localStorage` recents: the search box must still open. Test in Task 4 (`loadRecents`).

---

### Task 0: Get the working tree ready

Mat decides how the uncommitted work is handled, because the later commits touch the same files.

- [ ] **Step 1: Ask Mat to commit or approve committing the existing uncommitted work on `ui`**, as its own commit, separate from this feature. Without this, `git add` on `App.tsx`, `LiveState.cs` and others would mix that work into feature commits.
- [ ] **Step 2: Add `.superpowers/` to `.gitignore`**, and commit it with the spec and this plan.
- [ ] **Step 3: Record the starting state.** Run `dotnet test` from the repo root and `npm run build` in `web/`. Expected: both pass. Report any existing failure before starting.

### Task 1: Filtered departures

**Files:**
- Modify: `src/Api/LiveState.cs` (`Departure`, `Departures`), `src/Api/Program.cs` (`/stops/{id}`)
- Modify: `tests/Transit.Tests/Transit.Tests.csproj` (add a `ProjectReference` to `src/Api/Api.csproj`), `tests/Transit.Tests/TestGtfs.cs`
- Test: `tests/Transit.Tests/LiveStateDepartureTests.cs`

**Interfaces:**
- Produces: `Departure` gains `int DirectionId`, `double Dist` and `string ShapeId`, appended after `BeBy`.
- Produces: `Departures(string stopId, int limit = 20, string? routeId = null, int? directionId = null)`.
- Produces: `GET /api/stops/{id}?route=<id>&direction=<n>` returns the same `{ stop, departures }`, filtered when both are present.

- [ ] **Step 1: Add `TestGtfs.WriteBusyStop(string path)`**: route `R` with trips `R0a`, `R0b` in direction 0 and `R1a` in direction 1 (reversed shape), route `OTHER` with 25 trips in direction 0, all serving stop `B`. Add route `LOOP` with a pattern visiting `A, B, C, B`. Include `trip_headsign`. Leave `Write` unchanged.
- [ ] **Step 2: Write the failing tests.** Set up a temp `DataDir` with the feed at `static/metro-gtfs/20260101T000000Z.zip`, a small `TimeProvider` subclass with a fixed time, `state.LoadStatic()`, and trip updates through `state.OnTripUpdates(...)`.

```csharp
[Fact] void Filter_returns_route_and_direction_even_when_other_routes_fill_the_limit()
// 25 OTHER trips due before R0a: Departures("B", routeId: "R", directionId: 0) has R0a and R0b, and no R1a or OTHER.
[Fact] void Unfiltered_call_behaves_as_before()
// Departures("B") has 20 rows, sorted by time.
[Fact] void Trips_with_no_vehicle_are_kept()
// R0a in trip updates with no vehicle: returned with VehicleId == null and Metro set.
[Fact] void Departure_carries_direction_dist_and_shape()
// R0a: DirectionId == 0, Dist == 157.2, ShapeId == the trip's shape.
[Fact] void Stop_visited_twice_in_a_pattern_does_not_throw()
// A LOOP trip with no vehicle: Departures("B") returns it once, using its first visit.
```

- [ ] **Step 3: Run:** `dotnet test --filter LiveStateDepartureTests`. Expected: FAIL, with the loop test throwing `ArgumentException` and the rest not compiling or failing on the missing parameters.
- [ ] **Step 4: Implement.** Skip non-matching trips before the time filter, then apply the limit as now. For a stop visited twice, keep every checkpoint per pattern; choose the first with `Index >= max(1, vehicle's NextCheckpoint)`, or the first with `Index > 0` when there's no vehicle, and treat "none left" as already gone. Read `route` and `direction` from the query in `Program.cs`.
- [ ] **Step 5: Run:** `dotnet test`. Expected: all pass.
- [ ] **Step 6: Commit** the listed files: `feat(api): filter a stop's departures by route and direction`.

### Task 2: Route directions, stop lines and stop search

**Files:**
- Create: `src/Transit/Gtfs/RouteDirections.cs`, `src/Transit/Gtfs/StopSearch.cs`
- Modify: `src/Api/LiveState.cs` (`IndexStops`, new lookups), `src/Api/Program.cs`
- Test: `tests/Transit.Tests/RouteDirectionsTests.cs`, `tests/Transit.Tests/StopSearchTests.cs`

**Interfaces:**
- Produces, in `Transit.Gtfs`:
  - `record RouteDirection(int DirectionId, string Label, StopInfo[] Stops)`
  - `record StopLine(string RouteId, int DirectionId, string Label, string ShapeId)`
  - `RouteDirections.Build(IEnumerable<StaticFeed> feeds) -> IReadOnlyDictionary<string, RouteDirection[]>`
  - `RouteDirections.LinesByStop(IEnumerable<StaticFeed> feeds) -> IReadOnlyDictionary<string, StopLine[]>`
  - `StopSearch.Find(IEnumerable<StopInfo> stops, string query, int limit = 20) -> StopInfo[]`
- Produces, in `Api`: `record DirectionView(int DirectionId, string Label, StopView[] Stops)` and `record StopLineView(RouteInfo Route, int DirectionId, string Label, string ShapeId)`.
- Produces endpoints: `GET /api/routes/{id}/directions` (404 for an unknown route), `GET /api/stops/{id}/lines`, `GET /api/stops/search?q=`.

- [ ] **Step 1: Add `TestGtfs.WriteBranches(string path)`**: route `R`, direction 0 with a common pattern `A, B, C` (headsign `East`, 3 trips) and a branch `A, B, D, C` (headsign `East via D`, 1 trip), and direction 1 with `C, B, A` (headsign `West`).
- [ ] **Step 2: Write the failing tests.**

```csharp
// RouteDirections
Directions_are_labelled_with_the_most_common_headsign   // direction 0 is "East", direction 1 is "West"
Branch_stops_are_merged_in_route_order                  // direction 0 stops: B, D, C
Stop_that_is_only_ever_first_is_left_out                // A isn't in direction 0's stops, because Departures skips first stops
Stop_first_in_one_pattern_but_not_another_is_kept       // kept when any pattern reaches it later
Lines_by_stop_give_each_direction_serving_it            // B: (R, 0, "East"), (R, 1, "West")
Line_shape_is_the_most_common_pattern_through_that_stop // D's line uses the branch's shape, B's the common pattern's

// StopSearch
Empty_or_blank_query_finds_nothing
Exact_stop_id_comes_first                                // "B" ranks stop id B ahead of a name containing "b"
All_words_must_match_in_any_order_ignoring_case          // "hennepin lake" finds "Lake St & Hennepin Ave"
Punctuation_in_the_query_is_harmless                     // "lake & (hennepin" finds it, and doesn't throw
Results_stop_at_the_limit
```

- [ ] **Step 3: Run:** `dotnet test --filter "RouteDirectionsTests|StopSearchTests"`. Expected: FAIL, types not defined.
- [ ] **Step 4: Implement `RouteDirections` and `StopSearch`.** Merge: start from the pattern with the most trips; for each other pattern, most trips first, insert each unseen stop right after its predecessor in that pattern. Group by `(RouteId, DirectionId)` across all feeds, as `IndexStops` does.
- [ ] **Step 5: Wire up `LiveState`.** Build both indexes in `IndexStops` so they follow feed refreshes. Add `Directions(string routeId)`, `Lines(string stopId)` and `SearchStops(string q)`. `SearchStops` searches only `_stopPatterns.Keys`, so unserved stops don't appear. Add the endpoints to `Program.cs`.
- [ ] **Step 6: Run:** `dotnet test`. Expected: all pass.
- [ ] **Step 7: Commit** the listed files: `feat(api): route directions, a stop's lines and stop search`.

### Task 3: Watching logic in the browser

**Files:**
- Create: `web/src/watch.ts`, `web/src/watch.test.ts`
- Modify: `web/package.json` (`vitest` dev dependency, `"test": "vitest run"`), `web/src/api.ts`

**Interfaces:**
- Produces, in `api.ts`:
  - `Departure` gains `directionId: number`, `dist: number`, `shapeId: string`
  - `interface RouteDirection { directionId: number; label: string; stops: Stop[] }`
  - `interface StopLine { route: Route; directionId: number; label: string; shapeId: string }`
  - `api.watchStop(w: Watch)`, `api.directions(routeId)`, `api.lines(stopId)`, `api.searchStops(q)`
- Produces, in `watch.ts`:
  - `interface Watch { stopId: string; routeId: string; directionId: number }`
  - `type Selection = { kind: 'vehicle'; id: string } | { kind: 'stop'; id: string } | ({ kind: 'watch' } & Watch) | null`, re-exported from `MapView.tsx` so existing imports keep working
  - `interface WatchBus { vehicleId: string; at: number | null; dist: number; shapeId: string }`
  - `interface WatchPlan { first: WatchBus | null; second: WatchBus | null; note: string | null }`
  - `planWatch(departures: Departure[], now: number): WatchPlan`
  - `minutesLabel(at: number, now: number): string`
  - `selectionHash(s: Selection): string` and `parseHash(hash: string): Selection`
  - `findLine(lines: StopLine[], w: Watch): StopLine | null`
  - `dataAge(feed: { connected: boolean; lastMessageAt: number; behindBy: number }, nowMs: number, departuresUpdatedAt: number): number | null`
  - `busIsStale(pingAt: number, now: number): boolean`
  - `shouldFrameSecond(zoomAll: number, zoomFirst: number): boolean`
  - `type Point = { x: number; y: number }` and `edgePoint(from: Point, to: Point, size: { width: number; height: number }, inset?: number): Point`

- [ ] **Step 1: Install and write the failing tests.** `npm i -D vitest`. Times below use `now = 1000`, and `clock` is the existing helper.

```ts
// planWatch
two buses                     -> first and second set, note null
v1 preferred, then v0, metro  -> first.at follows that order
bus with only a scheduled time-> first.at null, note 'Bus on the way, no estimate yet'
no-vehicle trip ahead, metro  -> note 'Metro lists a trip in 3 min, no bus located yet', first is still the bus
no-vehicle trip ahead, schedule -> note 'The timetable lists a trip in 3 min, no bus located yet'
no-vehicle trip ahead, now    -> note 'Metro lists a trip due now, no bus located yet'
one bus, later no-vehicle trip-> second null, note `After this one: ${clock(t)}, timetable` (or `, Metro`)
two buses, no-vehicle between -> note null
ahead and after both apply    -> the ahead note
no-estimate and ahead apply   -> the ahead note
no buses, a trip due          -> first null, note `No bus on the road yet. Next: ${clock(t)}, timetable`
no departures                 -> first null, note 'Nothing due here in the next 90 minutes'

// minutesLabel
at = now + 240 -> '4 min'; at = now + 20 -> 'Due'; at = now - 30 -> 'Due'

// selectionHash and parseHash
watch round-trips through '#watch/56%2F7/21/0', including ids that need encoding
'#stop/1' and '#vehicle/2' still parse as today
'#watch/1/21' and '#watch/1/21/x' -> null

// findLine
returns the line matching route and direction; null when the stop no longer serves that route or direction

// dataAge (seconds, or null when fresh)
message 10 s ago, connected, behindBy 30, departures 10 s ago -> null
last message 90 s ago, even if connected is still true        -> 90
disconnected                                                  -> seconds since last message
behindBy 200                                                  -> 200
departures last updated 90 s ago                              -> 90

// busIsStale
pingAt 100 s before now -> false; 121 s before now -> true

// shouldFrameSecond
zoomAll 14, zoomFirst 14.5 -> true; zoomAll 12.4, zoomFirst 15 -> false; zoomAll 12.4, zoomFirst 12.9 -> true

// edgePoint
a target off the right side -> x is width - inset, y on the line from `from` to `to`
a target on screen          -> the target itself
```

- [ ] **Step 2: Run:** `npm test`. Expected: FAIL, `watch.ts` doesn't exist.
- [ ] **Step 3: Implement `watch.ts` and the `api.ts` additions.** Values: stale after 60 s without a message or departures update, or `behindBy` over 120 s; a bus is stale after 120 s without a ping. `shouldFrameSecond` is `zoomAll >= 12.5 || zoomFirst - zoomAll < 1`. Notes for a no-vehicle trip use `metro ?? scheduled`, and such a trip with neither is ignored. `metro`-sourced text says `Metro`, `scheduled` says `timetable`.
- [ ] **Step 4: Run:** `npm test && npm run build`. Expected: all pass.
- [ ] **Step 5: Commit** the listed files and `package-lock.json`: `feat(web): watching logic and API client`.

### Task 4: Recents and nearby routes

**Files:**
- Create: `web/src/recents.ts`, `web/src/recents.test.ts`, `web/src/nearby.ts`, `web/src/nearby.test.ts`

**Interfaces:**
- Consumes: `Watch` from Task 3, `Route` and `Stop` from `api.ts`.
- Produces:
  - `interface Recent extends Watch { stopName: string; route: string; color: string; textColor: string; label: string }`
  - `loadRecents(storage?: Pick<Storage, 'getItem'>): Recent[]`
  - `saveRecent(entry: Recent, storage?: Pick<Storage, 'getItem' | 'setItem'>): Recent[]`
  - `nearbyRoutes(stops: Stop[], at: { lat: number; lon: number }): Route[]`
  - `boxAround(at: { lat: number; lon: number }, metres: number): [south: number, west: number, north: number, east: number]`
  - `NEARBY_METRES = 800`, `NEARBY_WIDER_METRES = 2000`

- [ ] **Step 1: Write the failing tests.**

```ts
// recents
saveRecent puts the newest first
saving the same stop, route and direction moves it to the top, no duplicate
the same stop with another direction is a separate entry
a sixth entry drops the oldest
loadRecents: nothing stored, invalid JSON, a non-array, and entries missing fields -> [] or only the valid entries
a storage whose getItem or setItem throws -> no throw; saveRecent still returns the updated list

// nearby
routes are ordered by their nearest stop and listed once
a stop's several routes all appear
no stops -> []
boxAround(at, 800) is about 0.0072 degrees of latitude either side at 45 degrees north, wider in longitude
```

- [ ] **Step 2: Run:** `npm test`. Expected: FAIL, modules don't exist.
- [ ] **Step 3: Implement both modules.**
- [ ] **Step 4: Run:** `npm test`. Expected: all pass.
- [ ] **Step 5: Commit** the listed files: `feat(web): recents and nearby routes`.

### Task 5: Watching as a selection in the app

**Files:**
- Create: `web/src/useWatch.ts`
- Modify: `web/src/App.tsx`, `web/src/MapView.tsx` (re-export `Selection`, tolerate the new kind)

**Interfaces:**
- Consumes: `watch.ts` from Task 3.
- Produces: `interface WatchView { watch: Watch; stop: Stop | null; line: StopLine | null; plan: WatchPlan | null; dataAge: number | null; failed: 'load' | 'no-line' | null }` and `useWatch(watch: Watch | null, live: LiveVehicles): WatchView | null`.
- Produces: `MapView` gets a new prop `watching: WatchView | null`, and `App` a `startWatch(w: Watch)`.

- [ ] **Step 1: Write `useWatch`.** Queries `api.watchStop` every 10 s and `api.lines` once per stop. `failed` is `'load'` for an error with no data, and `'no-line'` when lines loaded and `findLine` is null.
- [ ] **Step 2: Update `App.tsx`.**
  - Use `parseHash` and `selectionHash` in place of `fromHash` and the inline hash.
  - `startWatch` sets the selection and folds the panel, remembering the earlier collapsed state. A fold caused by watching isn't written to `localStorage`.
  - Stopping with Escape or the search box's stop control restores the earlier collapsed state.
  - The panel body and `Heading` show the existing stop views for a watched stop.
  - The nav and rail's Scorecard and Charts buttons end watching. Live and the rail's open button only reopen the panel.
- [ ] **Step 3: Run:** `npm run build && npm run lint`. Expected: both pass.
- [ ] **Step 4: Check by hand** in the dev server with `API_URL` pointed at an API with Tasks 1 and 2: opening `#watch/<stop>/<route>/<direction>` shows the stop in the reopened panel, the hash survives reload, and Scorecard then Live shows the normal map with the hash cleared.
- [ ] **Step 5: Commit** the listed files: `feat(web): watching as a kind of selection`.

### Task 6: The search box

**Files:**
- Create: `web/src/SearchBox.tsx`, `web/src/searchSteps.ts`, `web/src/searchSteps.test.ts`
- Modify: `web/src/App.tsx`, `web/src/styles.css`, `web/src/MapView.tsx` (`onMapInteract`, map centre)

**Interfaces:**
- Consumes: `api.searchStops`, `api.directions`, `api.lines`, `api.routes`, `nearbyRoutes`, `boxAround`, `loadRecents`, `saveRecent`, `WatchView`, `Countdown` wording.
- Produces: `<SearchBox watching={WatchView | null} onWatch={(w: Watch) => void} onStop={() => void} autoView={boolean} onAutoView={() => void} listOpen={boolean} onListOpen={(open: boolean) => void} mapCentre={() => { lat: number; lon: number }} />`.
- Produces: `type Step = { at: 'home' } | { at: 'directions'; route: Route } | { at: 'stops'; route: Route; direction: RouteDirection; near: { lat: number; lon: number } | null } | { at: 'lines'; stop: Stop }` and `closestStop(stops: Stop[], at: { lat: number; lon: number }): string | null`, `matchRoutes(routes: Route[], q: string): Route[]`, in `searchSteps.ts`.

- [ ] **Step 1: Write the failing tests** for the pure helpers.

```ts
closestStop returns the nearest stop's id, and null for no stops
matchRoutes: '21' matches short name '21' first, then '21A'; 'lake' matches a long name; blank matches nothing
```

- [ ] **Step 2: Run:** `npm test`. Expected: FAIL.
- [ ] **Step 3: Implement `searchSteps.ts`.** Run `npm test`. Expected: pass.
- [ ] **Step 4: Implement `SearchBox`.**
  - Input with a list of buttons. Home shows Recent, and Routes near you once the box has focus; typing shows matching routes and stops, debounced 200 ms.
  - Location is requested on focus only. On refusal or error, use `mapCentre()` and label the list "Routes near the middle of the map". Widen to `NEARBY_WIDER_METRES` when `NEARBY_METRES` finds nothing.
  - A stop result with one line starts watching; with several it goes to `lines`. Picks from search and recents call `saveRecent` then `onWatch`.
  - While watching: the box shows the stop name, route badge and label, `watching.plan.note`, a stop control, and a "Recentre" button when `autoView` is false. When `dataAge` is set it adds "Live data is N min old". When `failed` is set it shows "Couldn't load this stop" or "This stop no longer has that route", with the stop control.
  - Escape closes an open list first and stops the event, so it doesn't also end watching.
- [ ] **Step 5: Wire it into `App.tsx`.** Visible only on the Live view. `listOpen` starts `true` when there are recents and nothing is watched. `MapView` calls `onMapInteract` on map `click` and `dragstart`, which closes the list.
- [ ] **Step 6: Add the CSS.** Top right, `z-index` above the map and level with the panel. At `max-width: 720px` it spans the top with a 12 px gutter. `.maplibregl-ctrl-top-right { top: 68px }`. The list scrolls within `calc(42vh - 60px)` on phones, so it clears the bottom sheet.
- [ ] **Step 7: Run:** `npm test && npm run build && npm run lint`. Expected: all pass.
- [ ] **Step 8: Check by hand** at desktop and 390 px wide: first visit closed, focus shows nearby, route to direction to stop reaches watching, typing a stop with several routes asks which, recents appear on reload and go straight to watching, and refusing location falls back with the label.
- [ ] **Step 9: Commit** the listed files: `feat(web): search box with recents and nearby routes`.

### Task 7: Watching on the map

**Files:**
- Modify: `web/src/MapView.tsx`, `web/src/App.tsx`, `web/src/styles.css`

**Interfaces:**
- Consumes: `WatchView`, `minutesLabel`, `busIsStale`, `shouldFrameSecond`, `edgePoint` from Tasks 3 and 5; `autoView` state from Task 6.

- [ ] **Step 1: Filter the map.** While watching, the vehicles source only includes `plan.first` and `plan.second`, and the `stops` layer is filtered to the watched stop, marked `selected`. Clicks keep their existing handlers, which end watching by replacing the selection.
- [ ] **Step 2: Draw the route and trail.** The route source uses `api.shape(line.shapeId)`, or the first bus's `shapeId` when there is one. Change `trailFor` to take a target distance: `nextStops` as before for a selected vehicle, `plan.first.dist` while watching, with the trail ending at the stop instead of the bus's next stop. The ring stays on the watched stop even with no bus on the road.
- [ ] **Step 3: Add the bubbles.** Use two MapLibre `Marker`s with HTML elements, `.watch-bubble` and `.watch-bubble.quiet`, anchored above their bus. Update position and text in the existing 100 ms timer, writing to the DOM only when text changes. A bus with `at == null` or not yet placed by `live.place()` has no bubble. Add `.stale` when `dataAge != null` or `busIsStale(pingAt, now)` is true. Clicking a bubble calls `onSelect` for that vehicle. Remove markers when watching ends.
- [ ] **Step 4: Add the automatic camera.** While `autoView`: every 5 s, and when the plan changes, compute `map.cameraForBounds` for stop, first and second, and for stop and first. Use `shouldFrameSecond` to pick, then `easeTo`, skipping while `map.isMoving()` or a press is down. Padding combines `panelPadding()` with 76 px on top for the box and bubbles. Cap at zoom 16. With no bus, centre on the stop at zoom 15. `dragstart` turns `autoView` off, using the existing pattern for `follow`.
- [ ] **Step 5: Add the off-screen label.** When the second bus isn't framed, or a drag has left it off screen, its bubble is shown with `.edge`, placed at `edgePoint(stop on screen, bus on screen, canvas size)`.
- [ ] **Step 6: Respect reduced motion.** `easeTo` uses `animate: false` when `REDUCED_MOTION.matches`.
- [ ] **Step 7: Run:** `npm test && npm run build && npm run lint`. Expected: all pass.
- [ ] **Step 8: Check by hand.** The automated Chrome here has WebGL off. Try Playwright's Chromium with `--use-angle=swiftshader --enable-unsafe-swiftshader`. If the map doesn't render, tell Mat that this step needs their browser. Check: both bubbles track their buses, other vehicles are hidden, a tap on a bus or stop ends watching, a drag keeps watching and shows Recentre.
- [ ] **Step 9: Commit** the listed files: `feat(web): the watching screen on the map`.

### Task 8: Finish and try it for real

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add a short section to the README** on the search box, the watching screen, the new endpoints, and `npm test`.
- [ ] **Step 2: Run:** `dotnet test`, then `npm test && npm run build && npm run lint` in `web/`. Expected: all pass.
- [ ] **Step 3: Commit** `README.md`: `docs: the watching screen`.
- [ ] **Step 4: Ask Mat before deploying to the Pi.** After a deploy, Mat checks on their phone over HTTPS, at home, with a real bus coming: location prompt, recents on reopening, the page after the phone sleeps, and saving the link to the home screen.
- [ ] **Step 5: Measure the open question.** Against the live API, sample departures for a few routes and report how often a trip with no vehicle sorts ahead of the first bus, so Mat can judge whether that text line is pulling its weight.
