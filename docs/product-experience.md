# DeveMobileLPR product experience

DeveMobileLPR is a road journal, not a dashboard to operate while a vehicle is moving. Its two modes therefore have intentionally different information density:

- **Drive** is full-screen, glanceable, and has one dominant action: stop and save.
- **Parked use** supports exploration: daily highlights, trips, vehicle search, route traces, RDW management, export, and deletion.

This split follows Android's automotive guidance to keep in-motion text short, information glanceable, and task flows shallow. It also follows Android's privacy guidance to minimize permission requests: camera access is requested when the user starts a drive, location is optional, and no network permission is required. See [Android writing guidance](https://developer.android.com/design/ui/cars/guides/foundations/writing-guidelines), [driver task-flow guidance](https://developer.android.com/design/ui/cars/guides/ux-requirements/plan-task-flows), and [permission minimization](https://developer.android.com/privacy-and-security/minimize-permission-requests).

## Primary user stories

### Before and during a drive

- As a driver, I want to select the usable camera and zoom before moving so plates occupy enough pixels for reliable OCR.
- As a driver, I want one clear start action and an unmistakable live state so I know whether the app is processing.
- As a driver, I want recognized plates boxed in the camera view with the best current text so I can verify alignment at a glance.
- As a driver, I want confirmed boxes to add make/model, catalog value, and body type when RDW is installed.
- As a driver, I want duration, unique-car count, most expensive car, GPS state, and the latest confirmation without opening another screen.
- As a driver, I want one large stop-and-save action and no reachable navigation tabs while processing.
- As a privacy-conscious user, I want processing to stop when the app leaves the foreground and raw frames never to be persisted.

### Reviewing the day

- As a user, I want today's drive count, unique cars, distance, and most expensive car immediately visible.
- As a user, I want drives kept as real sessions with start/end time and distance rather than guessed time buckets.
- As a user, I want a trip to show its route trace, confirmed vehicles, RDW facts, confidence, and individual locations.
- As a user, I want to search by plate, make, or model and see every appearance of the same vehicle across trips.
- As a user, I want a vehicle profile with first/last seen, appearances, catalog price, and map handoff for saved positions.

### Setup, trust, and control

- As a user, I want RDW import to clearly show installed/missing state and reject invalid files before replacing the working database.
- As a user, I want location trails, the road guide, and confirmation haptics to be independently configurable.
- As a user, I want an explicit explanation of what stays local and which Android permissions are active.
- As a user, I want to export all history to CSV or permanently delete trips, points, and sightings without deleting the reusable RDW snapshot.
- As a maintainer, I want model names, app/build version, package ID, and platform baseline visible for diagnostics.

## Information architecture

The top-level MAUI Shell has three destinations:

1. **Drive** — preparation, camera/zoom selection, live processing, and stop/save.
2. **History** — today's dashboard with Trips and Vehicles subviews.
3. **Settings** — RDW, drive preferences, privacy/permissions, data controls, and diagnostics.

Trip and vehicle profiles are pushed detail pages, keeping the top-level navigation stable. Shell tabs disappear during an active drive. .NET MAUI Shell supplies the navigation hierarchy, while a custom handler embeds the proven native CameraX preview. The detection overlay is a native composited view so boxes stay synchronized with the preview; MAUI owns the remaining interface. See [.NET MAUI Shell pages](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/shell/pages?view=net-maui-10.0), [MAUI dependency injection](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/dependency-injection?view=net-maui-10.0), and [CameraX preview transforms](https://developer.android.com/media/camera/camerax/transform-output).

## Interaction and accessibility rules

- Active-drive controls use large touch targets, high contrast, short labels, and redundant shape/text rather than color alone.
- The start and stop actions have stable automation identifiers; the camera preview has a semantic description.
- Motion is not required to understand state. Live, ready, loading, and attention states are expressed in text.
- The plate-yellow accent is reserved for license plates and vehicle-value highlights; mint indicates live/success; red is reserved for stop/destructive actions.
- Details and settings remain usable in portrait or landscape; an active drive locks to sensor landscape and immersive full screen.
- Route points are drawn offline without requiring a Google Maps API key; an explicit action hands a saved coordinate to the user's installed map app.
- Permission denial produces an actionable state and does not prevent history or settings use. Accessibility follows MAUI semantic-property guidance: [MAUI accessibility fundamentals](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/accessibility?view=net-maui-10.0).

## Data behavior

- Video frames and plate crops exist only in pooled memory and are discarded after inference.
- A confirmed appearance belongs to the active trip. Repeated confirmations within three minutes merge only inside that trip.
- Route points are sampled at most every ten seconds, ignore poor accuracy, and suppress short stationary jitter; raw continuous location is not stored.
- An interrupted process closes its open trip at the last route or sighting timestamp during the next database initialization.
- RDW remains a replaceable, separately validated database. History survives RDW replacement.
- CSV export is explicit and includes coordinates because it is a user-initiated portability action.
