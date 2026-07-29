# Architecture and engineering decisions

The visual architecture, reusable components, and responsive-layout rules are documented in [DeveMobileLPR UI design system](ui-design-system.md).

## Resolution before frame rate

License-plate OCR fails when characters occupy too few pixels, so the camera path asks CameraX for 3840×2160 analysis and accepts the nearest device-supported resolution. The configured recognition-rate gate decides which frames are copied from CameraX. This gives the detector multiple observations while preserving character detail without copying every preview frame by default.

`ImageAnalysis.StrategyKeepOnlyLatest` is paired with an application-level slot of capacity one. There are therefore two independent backpressure boundaries: CameraX does not queue proxies, and inference does not queue copied frames. A slow/thermally throttled phone reduces sampling rate rather than increasing latency or memory.

Windows Drive uses `MediaCapture` for the live preview and a `MediaFrameReader` for BGRA frames. The frame callback atomically replaces one pending `SoftwareBitmap`; a single worker converts only the latest bitmap into pooled planar YUV and submits it to the same capacity-one recognition slot. Camera enumeration, selection, preview, and frame conversion remain in the Windows adapter, while inference, consensus, lookup, and persistence stay shared.

Camera planes are copied because an `ImageProxy` must be closed promptly and its buffers become invalid afterwards. Copies use pooled memory. Preprocessors bilinearly sample RGB values directly from rotated YUV planes into reusable detector/OCR tensors; they never materialize a 4K RGB bitmap.

Zoom is applied with CameraX `CameraControl.SetZoomRatio` after preview and analysis are bound to the same camera, so both outputs receive the same sensor crop. The requested ratio is retained and reapplied after camera or lifecycle rebinding. The AndroidX `ZoomState` live-data value is a Java peer and must be converted with Android's Java-aware cast; a normal CLR interface cast silently yields `null` on affected runtimes. Zoom futures are observed and logged so camera failures cannot disappear silently again.

## Inference contracts

The detector input is `[1,3,608,608]` RGB `float32`, normalized to 0–1 and letterboxed with value 114. Output rows are decoded as `[batch,x1,y1,x2,y2,class,score]`, then mapped back through letterbox padding and the road ROI.

The OCR input is `[1,64,128,3]` RGB `uint8`. The model performs its own normalization. It emits ten character slots over `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_` plus a 66-class region output. The decoder retains the best three alternatives per slot for temporal fusion.

Inference sessions and input `OrtValue` objects are long-lived. XNNPACK is attempted on Android with bounded threads. Windows attempts DirectML first, allowing supported graph nodes to run on the GPU. Either platform falls back to the CPU provider with ONNX Runtime's default multi-core intra-op pool. Runs are serialized per model because their input buffers are reused.

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
- Models are verified by byte length and SHA-256 before use on Android and Windows.
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

## Offline video analysis

The Analyze tab is intentionally separate from Drive and History. Android copies a selected document-provider video into durable private app storage because those streams cannot be reopened reliably by the platform media decoder. Windows reuses the selected local path when available and falls back to a private staged copy. A source is removed only when no saved analysis references it. Detections and confirmations are persisted as compact JSON metadata, atomically replaced, and remain separate from sighting history. Raw frames, previews, and plate crops are never persisted.

Android's media retriever and a Windows Media Foundation Source Reader implement the shared `IVideoFrameSource` contract. These adapters own only native media opening, timeline metadata, decoding, and conversion into pooled planar YUV. Android uses timestamp-based retrieval. Windows requests NV12 and reads forward sequentially, discarding unsampled frames without seeking; it permits hardware transforms and DXVA while retaining system-memory output so driver-specific GPU-surface failures cannot abort an analysis. Recognition receives the decoded source resolution on both platforms: the detector still preprocesses to 608×608, while OCR crops from the full source frame. Only lazy review thumbnails are scaled to 1280 pixels wide.

The platform-independent `VideoAnalysisEngine` owns sampling, duration limits, cancellation, progress, serialized runs, and compact result projection. It delegates recognition, geometry resets, tracking, and temporal consensus to `RecognitionStreamProcessor`, the same stateful component used by Drive mode. Shared analysis records, `VideoFrameSampling`, and `VideoFrameTimeline` keep processing and persistence semantics consistent across Android and Windows. This boundary keeps native media APIs out of the recognition workflow while ensuring live and recorded frames exercise the same recognition behavior.

The Analyze UI has one pending-video section and one analyses stream. Starting a run clears the pending selection and inserts a non-openable progress row; its background fills from left to right as sampled frames complete. Finished and previously saved rows share the same click-to-review behavior. Review seeks always resolve to an analyzed frame, so the slider, timestamp, decoded preview, and previous/next frame controls cannot disagree.

Review previews are decoded lazily from the source video and held in a bounded in-memory cache. The timeline stores only normalized detection positions, and plate-index entries seek to the nearest analyzed frame. If a source video is missing, saved detection metadata remains reviewable without a preview.

Offline analysis applies backpressure and processes every requested sample. Unlike the live camera's latest-frame slot, it does not drop selected frames. Sampling accepts any positive source-frame interval; the UI provides common presets plus a custom interval. Runs can optionally be limited to the first 30 seconds. When frame-rate metadata is absent, timing is derived from reported frame count and duration before falling back to 30 frames per second.

Recognition diagnostics use milliseconds consistently. Live telemetry reports source, preview, and completed-recognition cadence as milliseconds per frame. Per-recognition diagnostics separately record detector and OCR queue, preprocessing, inference, and postprocessing time, plus every detector candidate (including empty or skipped OCR), tracking time, active tracks, observation counts, current-frame associations, and live latest-slot replacements. Enabling diagnostics in Settings shows these values and candidate/track overlays in Drive and Analyze; analyzed-video JSON retains the compact numeric/text diagnostics but never image pixels.

Analysis progress reports average total, decode, and recognition milliseconds per processed frame. Those cumulative progress values are not persisted. When recognition diagnostics are enabled, compact per-frame model and tracking timings are persisted so runs can be compared later without retaining frames. Windows attempts DirectML for detector and OCR sessions, then falls back to the multi-core CPU provider when DirectML is absent or session creation fails.

The Windows Media Foundation frame source lives in a separate Windows-only library
referenced by both the MAUI app and the end-to-end test project. A local video can
therefore be replayed through the production decoder, production ONNX models, and
the same `RecognitionStreamProcessor` used by Drive and Analyze. The opt-in fixture
defaults to the first 30 seconds and approximately two analyzed frames per second,
accepts arbitrary duration and source-frame interval overrides, and can write the
complete diagnostic result as JSON. Large source videos and generated reports remain
outside version control.
