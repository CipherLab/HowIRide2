# Watch a stop: design

Date: 2026-11-05
Status: approved by Mat, 2026-11-05

Items marked **(call)** were decided while writing this and haven't been discussed yet, so they are open to change.
Everything else was agreed in conversation.

## Why

Mat is at home near their stop and wants to know when to walk out the door.
The decision should come from where the next bus actually is and when it will reach the stop, not from the timetable.

Today the app can show this, but only in pieces.
You find the stop on the map, read its departures in the side panel, then tap a bus, and the map and the panel never show the stop and its bus together.

## What we're building

1. A search box at the top right of the map for picking a stop, with recent picks and nearby routes.
2. A "watching" screen showing the picked stop and the next two buses coming toward it, each with its time riding on it.

## Goals

- From opening the app, a rider who has watched their stop before gets back to it in one tap.
- At a glance, the rider sees where the next bus is relative to their stop and how long until it gets there.
- The rider can see what happens if they miss it, from the bus behind.

## Anti-goals

- A timetable time must never look like a real bus. No bubble on the map ever shows a scheduled time.
- The screen must not show a confident time and position when the live data has stopped. See "When live data goes stale".

## Non-goals for this version **(call)**

- Alerts or notifications ("time to leave").
- Walking time or a "leave by" time.
- The "be at the stop by" time on the watching screen. Mat picked the version without it. It stays in the existing stop panel.
- Accounts, or recents that follow you between devices.
- Watching several routes at one stop at once.
- Trip planning.

## The search box

**Position.** Top right of the map, clear of the side panel, which stays on the left.
The map's zoom and locate buttons, currently top right, move underneath it.
On phones, where the panel is a bottom sheet, the box runs across the top of the screen. **(call)**

**On opening the app.** If the rider has recents and isn't watching anything, the recents are already showing under the box.
Tapping one goes straight to the watching screen.
Tapping or dragging the map closes the list **(call)**, and tapping the box brings it back.
On a first visit there are no recents, so the box is closed.

**Recents.** Each one holds a stop, a route and a direction.
They're kept in that browser's `localStorage`, wrapped so a refused write doesn't break anything, as the panel's collapsed state already does in `web/src/App.tsx`.
Keep the five most recent, with no duplicates. **(call)**

**Picking a stop the first time.** Tapping the box shows routes near you, nearest first.
Then: pick a route, pick its direction, pick your stop from the direction's stops in route order, with the closest to you marked.

**Typing.** A route number, stop name or stop number all match as you type.
A route result continues at "pick its direction".
A stop result that serves more than one route and direction asks which one, because watching is always one route and one direction. **(call)**

**Location.** Ask for it only when the box is tapped, not on page load. **(call)**
Geolocation works in production: the site is served over HTTPS through Nginx Proxy Manager, which isn't in this repo.
If the rider declines, use the middle of the map and label the list accordingly. **(call)**

## The watching screen

The mockup is `.superpowers/brainstorm/2864250-1793891431/content/watching-stop.html`, option C.

**On the map**

- The picked stop, marked as yours, with the ring the map already uses for a bus's next stop.
- The route line, in the route's colour.
- The next two buses on this route and direction that haven't passed the stop.
  Each has a bubble above it with minutes to the stop. The first is prominent, the second quieter.
- Breadcrumbs from the first bus to the stop, using the existing trail, extended to run to this stop rather than the bus's next one.
- Other vehicles and other stops are hidden so nothing competes with the two buses. **(call)**

**In the search box.** It collapses to show the stop name, route badge and direction, with a way to stop watching. **(call)**
Escape also stops watching, as it clears a selection today.
Stopping returns to the normal map.

**The side panel** folds away when watching starts, using the existing collapsed state, because on a phone it would cover over half the map.
Reopened, it shows the existing stop panel for this stop. **(call)**

**The camera** moves and zooms by itself to keep the stop and both buses in view, until the rider drags the map.
After a drag it stays put, and a button brings the automatic view back. **(call)**
If fitting the second bus would make the first too small to read, the camera frames the stop and first bus only, and the second bus's time sits at the edge of the screen on the side it's coming from.
What counts as too small is left to the builder.

**Which time goes on a bus.** Our estimate, or Metro's when we don't have one, in whole minutes, using the app's existing countdown wording.
This is a departure time from the stop, the same as the rest of the app. It's the right number to plan around, because it's when you'd miss the bus. **(call)**
`bestEstimate` in `web/src/api.ts` falls back to the timetable, so it can't be used as is for bubbles.

**States**

| Situation | What shows |
|---|---|
| Two buses on the road | Both, as above |
| One bus on the road, and the trip after it hasn't started | That bus, with no second bubble, and one line under the search box such as "After this one: 8:52, timetable" |
| A trip with no bus attached is due before the first bus on the road | The buses as normal, and one line under the search box such as "Metro lists a trip in 3 min, no bus located yet" |
| A bus on the road with no estimate from us or Metro | The bus with no bubble, and "Bus on the way, no estimate yet" under the search box **(call)** |
| No bus on the road, a trip is due | "No bus on the road yet", with the scheduled time labelled as the timetable. Nothing on the map but the stop and route |
| Nothing due in the window | "Nothing due here in the next 90 minutes", matching the stop panel |
| A bus passes the stop | It drops off, and the one behind becomes the main bus |

"On the road" means the departure has a `vehicleId`.
Bubbles and the camera only ever use buses on the road, so the first bus on the road is the prominent one even if a trip with no bus is listed ahead of it.
Trips with no bus attached only ever appear as a line of text, with their source named (Metro or timetable).
If more than one line applies, show the one about the earlier trip. **(call)**

**Ending watching.** Any tap on something else ends watching and the other vehicles reappear:

- Tapping a watched bus or the watched stop on the map selects it as it does today, with the panel open.
- Tapping a departure row in the reopened stop panel goes to that bus.
- Going to Scorecard or Charts ends watching. Coming back to Live shows the normal map. **(call)**

Watching is therefore a new kind of `Selection`, replaced like any other, rather than a separate mode.
Dragging and zooming the map only affect the camera, and tapping empty map does nothing, so neither ends watching. **(call)**
The stop stays in recents, so getting back is one tap.

**When live data goes stale.** With the panel folded, its live status line isn't visible.
Once the vehicle feed or the connection has been quiet for long enough, the screen says how old the data is, and the bubbles stop looking current. **(call)**
The threshold and treatment are left to the builder. They need to work when a phone wakes from sleep with the tab already open.

**Links.** Watching lives in the URL like other selections, for example `#watch/<stop>/<route>/<direction>`, so it can be bookmarked or saved to a phone's home screen. **(call)**

## Existing behaviour that stays

- Tapping a stop or bus on the map works as it does now.
- The Live, Scorecard and Charts tabs are unchanged.

## What the server has to add

Found by reading the code. The exact shape of each is left to the builder.

- **Filtered departures.** `LiveState.Departures` returns at most 20 for the whole stop, and `Departure` has no direction.
  At a busy stop the 20 can crowd out the route being watched, so filtering in the browser isn't safe.
  The server needs to filter by route and direction before applying the limit.
  The result has to keep trips with no vehicle alongside the buses, because the text lines in the states table depend on them.
- **A route's directions and stops.** Nothing returns this today. It needs each direction's label and its stops in order.
  The feed has `direction_id` and headsigns, and the label can come from the most common headsign. **(call)**
  Routes can have several patterns per direction, so the list has to merge them.
- **Search over stops.** Stop name and number, on the server. `/stops` only takes a bounding box.

Nearby routes need nothing new: `/stops?bbox=` around the location already returns stops with their routes.

## Things to know while building

- Positions drawn on the map run 15 s or more behind the newest ping, and the feed's own lag swings 19-38 s (`web/src/live.ts`). The times on the bubbles are predictions to the stop and don't share that delay.
- Trips only appear in departures if Metro's trip updates list them or a vehicle is on them, and only within 90 minutes. The "no bus on the road" and "nothing due" states follow from that.
- `Departures` skips a trip whose stop is its first checkpoint (`cpIndex == 0`), so a route's first stop won't show departures. The stop picker needs to handle this. Hiding those stops or letting them show as empty are both acceptable. Left to the builder.
- Search and watching include light rail and commuter rail as well as buses. **(call)**
- `web/` has no test runner. `tests/Transit.Tests` covers the .NET side.
- The automated Chrome on this machine has WebGL off, so the map doesn't render there. Checking the map layers needs a browser with WebGL or a check by Mat.
- The working tree on `ui` has uncommitted work touching `App.tsx`, `api.ts`, `components.tsx`, `panels.tsx`, `styles.css` and `LiveState.cs`. Build on it and don't overwrite it.

## Left to the builder

- How bubbles are drawn (HTML markers or map layers), so long as they read clearly on a phone and track the moving bus smoothly.
- Module structure, query intervals, and debounce on typing.
- Whether a watched stop's data comes from a new endpoint or a parameter on the existing one.
