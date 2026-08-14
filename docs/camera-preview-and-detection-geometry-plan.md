# Camera preview and detection geometry plan

Status: implementation plan. Do not make another isolated rotation, stretch, or overlay-offset
change before the contracts and tests below exist.

## Why this document exists

The Android camera work currently has four geometry problems that have repeatedly been treated as
one problem:

1. the orientation of the camera buffer sent to a preview surface;
2. the orientation metadata attached to a YUV frame sent to AI;
3. aspect-ratio fitting or cropping inside a visible panel;
4. projection of AI detection coordinates into that visible panel.

They influence one another, but they are not interchangeable. In particular, **Camera2 preview
rotation and YUV/AI rotation must be stored and handled separately**.

The regression in build `b65b17d` is a concrete example. `Camera2PhysicalFrameSource` calculated a
relative rotation for the YUV frame and passed the same value to `AspectRatioTextureView`. On the
Pixel 9 Pro that rotated both physical-camera previews by 90 degrees. The older working preview
path used the preview surface/display transform while the YUV frame retained its own rotation
metadata.

## Non-negotiable visual contract

Every source has one canonical, upright image coordinate system. AI detections are expressed in
that coordinate system. A preview may Fit or Fill its panel, but may never change the image's
horizontal-to-vertical proportions.

| Mode | Preview policy | Expected result |
| --- | --- | --- |
| Single integrated camera | Fill | Full screen; symmetric cropping is allowed; no stretching |
| Multi integrated camera | Fit per panel | Entire frame visible; letterboxing is allowed; no stretching |
| LL-HLS | Fit per panel | Entire decoded frame visible; letterboxing is allowed; no stretching |
| Detection overlay | Same transform and panel as its source | Box stays on the detected plate |

Changing Fit to Fill, changing a panel layout, or changing rotation is incomplete unless preview
and overlay are verified together.

## Separate facts that each source must report

Introduce one immutable `SourceGeometry` snapshot per active source. It must contain facts, not
guesses derived later by the UI:

- source ID;
- native preview panel bounds inside the shared preview host;
- preview buffer width and height;
- preview rotation actually required by that native surface;
- whether the preview is mirrored;
- AI/YUV raw width and height;
- AI/YUV rotation metadata;
- canonical upright width and height;
- Fit or Fill policy;
- resulting visible content rectangle after Fit/Fill;
- crop region, when Android applies sensor crop/zoom.

`PreviewRotationDegrees` and `AiRotationDegrees` must be different properties even if they happen
to contain the same value on a device. No API may expose a generic `RotationDegrees` whose purpose
is ambiguous.

## Canonical coordinate pipeline

```text
Camera sensor
    |
    +-- preview stream --> native preview transform --> source panel/content rectangle
    |
    +-- YUV stream --> AI rotation --> canonical upright AI pixels --> detections
                                                               |
                                                               +-- canonical-to-content transform
                                                                   --> on-screen boxes
```

The transform used for detections is therefore:

```text
raw YUV coordinates
  -> rotate/mirror into canonical upright AI coordinates
  -> apply sensor crop semantics already present in the output frame
  -> uniform Fit/Fill scale
  -> add content-rectangle and panel offsets
  -> screen coordinates
```

There must be no independent X and Y scale in the final canonical-to-screen step. A uniform scale
is the invariant that prevents wide cars and narrow faces.

## Platform-specific contracts

### CameraX: normal single camera and CameraX-supported combinations

CameraX already owns preview orientation through `PreviewView` and reports analysis orientation
through `ImageProxy.ImageInfo.RotationDegrees`. Keep those responsibilities separate.

Implementation requirements:

1. Set the same target rotation on Preview and ImageAnalysis.
2. Bind them through a `UseCaseGroup` with one `ViewPort` wherever CameraX supports it. This gives
   Preview and ImageAnalysis the same sensor crop.
3. Obtain preview transformation/crop information from CameraX rather than reconstructing it from
   sensor orientation.
4. Continue passing `ImageProxy.ImageInfo.RotationDegrees` only to `Yuv420Frame`/AI.
5. Publish the actual PreviewView panel and content geometry for the overlay.

### Camera2: two physical cameras behind one logical camera

Camera2 has separate preview `SurfaceTexture` and YUV `ImageReader` outputs. Treat them separately.

Implementation requirements:

1. Log and retain sensor orientation and Android display rotation, but do not automatically apply
   the YUV relative rotation to `TextureView`.
2. Determine the preview transform from the preview surface contract. Start from the last
   device-proven upright behavior and verify it with the Pixel matrix below.
3. Preserve the producer/SurfaceTexture transform where Android supplies one; add only the
   correction needed for rotation and uniform Fit/Fill.
4. Keep the calculated YUV relative rotation on `Yuv420Frame` so AI receives an upright frame.
5. Use preview and YUV streams with matching oriented aspect ratios. If Android negotiates a
   different preview aspect, report the real value and content rectangle rather than assuming
   `1280x720` maps exactly to the analysis stream.
6. Reapply the preview transform on panel-size or display-rotation changes.
7. Publish each actual native panel/content rectangle to the shared overlay. Do not have
   `DriveOverlayView` recreate the native grid using only source count.

### LL-HLS

Use decoder-reported width, height, and rotation. The native video view and overlay must consume
the same Fit content rectangle. LL-HLS must remain composable with one or two integrated sources.

## Proposed ownership

| Responsibility | Owner |
| --- | --- |
| Discover sensor/display metadata | Android camera source |
| Decide CameraX/Camera2 preview rotation | Corresponding Android camera source |
| Rotate YUV access for AI | `Yuv420Frame` and recognition pipeline |
| Calculate uniform Fit/Fill geometry | shared Core geometry type |
| Lay out native source panels | Android drive video input |
| Report actual panel/content bounds | native preview host/handler |
| Project and draw detections | `DriveOverlayView`, using reported geometry |

The shared geometry type should accept source dimensions, rotation, mirror state, viewport, and
Fit/Fill policy and return both forward and inverse transforms. Android `Matrix` values and MAUI
overlay coordinates must be adaptations of this one result, not separate formulas.

## Implementation sequence

### Phase 1: observability before behavior changes

For every source, add one structured geometry log entry containing:

- sensor orientation;
- Android display rotation;
- preview buffer dimensions;
- chosen preview rotation and mirror state;
- raw YUV dimensions and AI rotation;
- canonical upright dimensions;
- panel bounds and content bounds;
- Fit/Fill mode;
- active sensor crop/zoom region.

Add an optional geometry-debug overlay that draws the panel boundary, visible content boundary,
center point, and source ID. This allows one screenshot to prove which transform is wrong.

### Phase 2: shared geometry model and tests

Replace `AspectRatioCorrection` with or evolve it into a complete model that supports:

- rotations 0, 90, 180, and 270;
- optional horizontal mirroring;
- Fit and Fill;
- non-zero panel offsets;
- forward and inverse point/rectangle projection;
- a returned content rectangle;
- validation that the final X/Y pixel scale is uniform.

Tests must use asymmetric markers, not only rectangles. A symmetric rectangle can hide a 180-degree
or mirroring error. Test top-left, top-right, bottom-left, center, and a non-square detection box.

### Phase 3: make single-camera behavior the reference

Keep the currently upright, undistorted single-camera preview as the reference behavior. Connect
its CameraX viewport/crop metadata to the new `SourceGeometry`. Verify overlays at 1x and at a
non-trivial zoom such as 3.2x before changing multi-camera rendering.

### Phase 4: fix Camera2 preview only

Use the recorded Pixel metadata to implement `PreviewRotationDegrees` independently from
`AiRotationDegrees`. First achieve an upright and proportionally correct preview with overlays
disabled. Do not modify YUV rotation during this phase.

### Phase 5: connect Camera2 detections

Once both preview panels are stable, project each source's detections using its reported panel and
content rectangle. Verify each panel independently and then simultaneously.

### Phase 6: add LL-HLS combinations

Verify LL-HLS alone, integrated camera plus LL-HLS, and two integrated cameras plus LL-HLS if the
device/session supports it. Each source retains independent geometry and AI timing.

## Required automated tests

1. Fit and Fill preserve a circle's aspect at 0/90/180/270 degrees.
2. Rotation maps all four asymmetric corner markers correctly.
3. Mirroring maps left/right correctly without changing vertical coordinates.
4. Two panel offsets map detections into the correct half of the screen.
5. Three and four panel layouts use reported native bounds rather than a duplicated grid formula.
6. Overlay rectangle projection round-trips through the inverse transform.
7. Crop/zoom does not add a second userspace crop to already-cropped output coordinates.
8. A source with a different preview and analysis resolution but the same oriented aspect aligns.
9. A mismatched preview/analysis aspect produces explicit content rectangles and still aligns.

## Pixel 9 Pro device acceptance matrix

Test in the actual landscape drive screen, not only the camera experiment page.

| Case | Sources | Resolution/crop | Must verify |
| --- | --- | --- | --- |
| S1 | Rear automatic | actual 4000x3000 or selected 4K, 1x | upright, Fill, no stretch, boxes align |
| S2 | Rear automatic | selected 4K, 3.2x | same as S1 and preview/AI share zoom crop |
| S3 | Front | largest supported, 1x | upright, intentional mirror policy, boxes align |
| M1 | ID 5 + ID 4 | 3840x2160 each, crop 1x | both upright, Fit, no stretch, boxes align |
| M2 | ID 2 + ID 4 | 3840x2160 each, configured crops | both upright; effective crop is logged accurately |
| M3 | ID 3 + ID 4 | 1920x1080 each | same geometry behavior at lower resolution |
| N1 | LL-HLS only | stream native | upright, Fit, boxes align |
| N2 | one integrated + LL-HLS | independent sizes | both panels and boxes align |

For every case capture:

- a screenshot containing a person or circular object and a readable plate;
- the structured geometry log;
- whether the whole source is visible or intentionally cropped;
- maximum box error at the plate edges.

Acceptance thresholds:

- no 90/180/270-degree orientation error;
- no visible anisotropic stretching;
- detection-box edge error at most 1% of its panel width or 8 display pixels, whichever is larger;
- no unused navigation bar or unexplained page inset;
- restarting the same configuration produces identical geometry.

## Regression rules

1. Never pass AI/YUV rotation directly into a preview transform without a device-proven preview
   contract.
2. Never compute native panel layout independently a second time in the overlay.
3. Never use separate horizontal and vertical final scales.
4. Never declare a geometry fix from a screenshot without also checking detection boxes.
5. Never change single-camera geometry while fixing Camera2 unless the single-camera acceptance
   cases are rerun.
6. A camera geometry PR is not complete until the Pixel 9 Pro matrix results are attached to it.

## Definition of done

The work is complete only when a single `SourceGeometry` snapshot explains both what the user sees
and where every detection is drawn, all automated tests pass, and the Pixel 9 Pro acceptance matrix
passes for single and multi-camera modes. Until then, geometry changes remain experimental and the
PR stays marked `DONT MERGE`.
