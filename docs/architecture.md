# Architecture and engineering decisions

The visual architecture, reusable components, and responsive-layout rules are documented in [RoadLens UI design system](ui-design-system.md).

## Resolution before frame rate

License-plate OCR fails when characters occupy too few pixels, so the camera path asks CameraX for 3840×2160 analysis and accepts the nearest device-supported resolution. Only one frame every 250 ms is copied from CameraX. This gives the detector multiple observations while preserving character detail and avoiding sustained 30 fps memory bandwidth.

`ImageAnalysis.StrategyKeepOnlyLatest` is paired with an application-level slot of capacity one. There are therefore two independent backpressure boundaries: CameraX does not queue proxies, and inference does not queue copied frames. A slow/thermally throttled phone reduces sampling rate rather than increasing latency or memory.

Camera planes are copied because an `ImageProxy` must be closed promptly and its buffers become invalid afterwards. Copies use pooled memory. Preprocessors bilinearly sample RGB values directly from rotated YUV planes into reusable detector/OCR tensors; they never materialize a 4K RGB bitmap.

Zoom is applied with CameraX `CameraControl.SetZoomRatio` after preview and analysis are bound to the same camera, so both outputs receive the same sensor crop. The requested ratio is retained and reapplied after camera or lifecycle rebinding. The AndroidX `ZoomState` live-data value is a Java peer and must be converted with Android's Java-aware cast; a normal CLR interface cast silently yields `null` on affected runtimes. Zoom futures are observed and logged so camera failures cannot disappear silently again.

## Inference contracts

The detector input is `[1,3,608,608]` RGB `float32`, normalized to 0–1 and letterboxed with value 114. Output rows are decoded as `[batch,x1,y1,x2,y2,class,score]`, then mapped back through letterbox padding and the road ROI.

The OCR input is `[1,64,128,3]` RGB `uint8`. The model performs its own normalization. It emits ten character slots over `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_` plus a 66-class region output. The decoder retains the best three alternatives per slot for temporal fusion.

Inference sessions and input `OrtValue` objects are long-lived. XNNPACK is attempted on Android with bounded threads; a failure is reported and falls back to the CPU provider. Runs are serialized per model because their input buffers are reused.

## Confirmation policy

Detections are associated with active tracks by intersection-over-union. Each track retains at most twelve observations and expires after 1.5 seconds without a match.

An exact plate can be confirmed when at least three distinct frames agree and its weighted share and winner margin pass the configured thresholds. Weight combines detection confidence, OCR confidence, and a crop-quality estimate. If exact strings differ, character alternatives are fused position by position. Every selected character must have at least 60% support; this prevents three mutually different final characters from being accepted merely because the other five agree.

For OCR results classified as Dutch, a confirmed string must match one of RDW sidecode layouts 1–14. Formatting uses the exact group lengths for that layout rather than guessing where hyphens belong.

## Persistence

Confirmed sightings within three minutes of the same plate and the same trip are merged. The merge keeps the earliest first-seen time, advances the last-seen time, adds observation counts, keeps the strongest confidence, and fills missing GPS/RDW facts. A trip boundary always creates a distinct appearance, even when two drives happen close together.

SQLite uses WAL mode and indexes plate/time, trip/time, route points, and catalog price. Schema version 2 adds trips and filtered GPS route points while migrating version-1 sightings in place. RDW data is intentionally a second SQLite database because it is large and replaceable. `SqliteRdwVehicleLookup` reads through the stable `rdw_vehicles` view so the user's downloader schema remains decoupled from the app.

## Security and privacy defaults

- No network permission is declared.
- Models and RDW data are read locally.
- Raw frames and crops are never persisted.
- The Android manifest disallows cleartext traffic.
- Models are verified by byte length and SHA-256 before use.
- RDW imports are copied to a temporary file, schema-validated, and atomically moved.
- Location is optional and a missing permission does not block recognition.

## Device-validation plan

Compilation and model execution prove API/tensor correctness, but a moving windscreen test is required before threshold claims. Collect a consented, manually labelled route covering daylight, dusk, rain, reflections, Dutch yellow plates, foreign plates, and different following distances. Report full-plate exact-match precision/recall at the final confirmation level—not character accuracy—and separate detector misses from OCR errors.

Tune in this order:

1. measure actual analysis resolution and plate pixel height;
2. adjust mount/zoom and road ROI;
3. measure motion blur and thermal throttling;
4. tune detector threshold and track timeout;
5. tune consensus thresholds only after reviewing false confirmations;
6. consider detector/OCR fine-tuning only when the remaining error set is understood.

False confirmations are more damaging than missed sightings because they poison long-term history. Threshold changes should therefore preserve high precision first.
