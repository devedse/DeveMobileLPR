# Android screen-space audit

All screenshots below were captured from the Pixel 9 API 36 Android emulator at
1080 × 2424. The populated examples use a deliberately long vehicle name to
exercise the same constrained layouts that real RDW data can produce.

## Dashboard metrics

The old wrapping layout added outer margins to every metric and could measure the
highlight tile too short. The replacement keeps a true 12dp gap between tiles,
aligns them with the page content, and lets the price, label, and plate determine
the row height.

| Before | After |
| --- | --- |
| ![Dashboard before](before/history-populated.png) | ![Dashboard after](after/history-populated.png) |

## Settings card widths

Paired settings cards previously kept their desktop-only right gap after wrapping
onto separate phone rows. The first card was therefore 16dp narrower than the next
card. Wrapped cards now fill equally; the gap is restored only when the cards
actually sit side by side.

| Before | After |
| --- | --- |
| ![Settings before](before/settings.png) | ![Settings after](after/settings.png) |

## Vehicle list density

Vehicle rows reserved a fixed 72dp snapshot column even when no image existed.
The empty column now collapses, giving long make/model text the space it needs.

| Before | After |
| --- | --- |
| ![Vehicle list before](before/vehicles-populated.png) | ![Vehicle list after](after/vehicles-populated.png) |

## Vehicle profile wrapping

The hero card now wraps the plate and identity as complete blocks on narrow
screens, instead of forcing both into undersized columns. It returns to a compact
side-by-side layout in landscape.

| Before | After |
| --- | --- |
| ![Vehicle profile before](before/vehicle-detail.png) | ![Vehicle profile after](after/vehicle-detail.png) |

## Trip-detail alignment

The four trip metrics now use an edge-aligned 2 × 2 grid. This removes the extra
inset above the full-width map and gives the trip highlight enough measured height
for both its value and plate.

| Before | After |
| --- | --- |
| ![Trip details before](before/trip-detail-populated.png) | ![Trip details after](after/trip-detail-populated.png) |

## Emulator coverage

The audit covered light theme in portrait and landscape across:

- Drive setup and an active virtual-camera drive
- Analyze setup
- History dashboard, trips, and vehicles in empty and populated states
- Trip details, vehicle details, embedded maps, and the full-screen map
- Settings from top to bottom, including its wrapped and side-by-side card states
