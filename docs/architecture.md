# Architecture and engineering decisions

The visual architecture, reusable components, and responsive-layout rules are documented in [DeveMobileLPR UI design system](ui-design-system.md).

## Platform boundary

`DeveMobileLPR.Application` is a plain `net10.0` project: it references Core and Inference, but not MAUI, Android, WinUI, or Storage. It owns the complete Drive and Analyze workflows, their snapshots and diagnostics, recognition-session lifetime, frame backpressure, overlay visibility/projection, and the ports through which a host supplies storage, settings, model creation, video input/decoding, location, dispatch, and device feedback.

The MAUI project is the composition root. Android and Windows register different implementations of `IRecognitionPipelineProvider`, `IDriveVideoInput`, and `IVideoFileBackend`; Android also supplies its native location tracker. ViewModels and shared workflows never select a platform or construct a native implementation. A target without a capability registers a shared `Unsupported*` or passive policy instead of placing a fake platform implementation under that target. Both Drive and Analyze receive the same platform pipeline provider and the same `RecognitionTuningConfiguration`, so model verification/installation and recognition-pipeline creation cannot drift between modes.

Native application code is grouped by both platform and capability under `Platforms/Android` and `Platforms/Windows`. A capability folder exists only when that target has native behavior; matching empty or no-op folders are deliberately avoided. A shared page or view model may depend on a platform-neutral port, but it does not contain native API branches or platform-specific product copy. The two live-video stacks deliberately use the same decomposition: `AndroidDriveVideoInput` selects between `CameraXFrameSource` and `AndroidHlsFrameSource`, while `WindowsDriveVideoInput` selects between `WindowsWebcamFrameSource` and `WindowsHlsFrameSource`. Recorded-video decoders that are reusable without MAUI live in the matching `DeveMobileLPR.Video.Android` and `DeveMobileLPR.Video.Windows` projects; the platform `Video` folders in the app only adapt native media APIs to `IVideoFileBackend`. Selected-file copying is shared by `SelectedVideoFileStager`, while Windows can reuse a local path and Android always creates a durable private copy. The Analyze page uses MAUI's `FilePickerFileType.Videos` abstraction rather than maintaining its own platform extension table.

| Capability | Shared policy or contract | Android native owner | Windows native owner | Boundary decision |
| --- | --- | --- | --- | --- |
| Background scanning | `IBackgroundScanningManager`, `UnsupportedBackgroundScanningManager` | `Platforms/Android/Background` | None | Android owns its foreground service; unsupported targets use the shared fallback. |
| Drive display behavior | `IDriveDisplayMode`, `PassiveDriveDisplayMode` | `Platforms/Android/Display` | None | Android controls orientation/keep-awake; Windows needs no native action. |
| Drive location | `IDriveLocationTrackerFactory`, `UnsupportedDriveLocationTrackerFactory` | `Platforms/Android/Location` | None | Android uses native providers; unsupported targets return no fixes through the shared port. |
| Selected-video staging | `SelectedVideoFileStager` | `Platforms/Android/Video/AndroidVideoFileBackend.cs` | `Platforms/Windows/Video/WindowsVideoFileBackend.cs` | Copying is generic; path lifetime and decoding remain platform-specific. |
| Video picker types | MAUI `FilePickerFileType.Videos` | None | None | The framework owns platform MIME types/extensions. |
| Camera/live HLS | Application video ports | `Platforms/Android/Camera` | `Platforms/Windows/Camera` | CameraX/Media3 and MediaCapture/Media Foundation have genuinely different lifecycle and buffer APIs. |
| Recorded decoding | `IVideoFrameSource` | `DeveMobileLPR.Video.Android` | `DeveMobileLPR.Video.Windows` | Native decoders remain paired platform libraries behind the same contract. |
| Inference execution | Recognition pipeline contracts and shared preprocessing/postprocessing | `Platforms/Android/Inference` | `Platforms/Windows/Inference` | LiteRT and ONNX Runtime/DirectML runners remain native adapters. |
| Map web-view setup | Shared map control plus platform configurator partial | `Platforms/Android/Map` | `Platforms/Windows/Map` | WebView cookie/storage APIs differ by native engine. |
| Settings capability text | `IPlatformSettingsInfo` | `Platforms/Android/Settings` | `Platforms/Windows/Settings` | Text reports real target capabilities and permission behavior. |

State whose meaning is one drive lives in a `DriveTrip` that `DriveCoordinator` creates on start and disposes on stop: the trip id, the plates seen, and the location tracker. Clearing a list of fields on every start is the alternative, and a field that is missed from that list stamps the next trip with the previous one's data — which is exactly how a short drive once recorded its cars at the position of the drive before it. The recognition session deliberately stays outside the trip because it owns the loaded models; its per-trip tracking state is cleared by `ResetTracking`. A location fix carries the time it was observed, and a fix older than `DriveTrip.MaximumLocationAge` counts as no fix, so a lost signal leaves a sighting unlocated rather than placing it where the device was last certain. The Android tracker subscribes to every enabled provider rather than GPS alone, because a cold GPS fix can outlast a short drive.

Native UI handlers still own native preview controls and source lifetime. CameraX/Media3 and MediaCapture/Media Foundation remain platform adapters because their lifecycle, permissions, and buffers are genuinely different. Android camera permission is requested by the Android video-input adapter; location permission is requested by the Android location adapter. The shared coordinator sees only explicit success/error data and never references MAUI permissions or infers failure from diagnostic text.

Camera preview, AI orientation, and detection projection changes must follow the explicit contracts and Pixel 9 Pro acceptance matrix in [camera-preview-and-detection-geometry-plan.md](camera-preview-and-detection-geometry-plan.md). That plan supersedes incidental implementation details in this paragraph while the multi-camera geometry work remains experimental. Detection rendering is a single cross-platform `DriveOverlayView` (a MAUI `GraphicsView`) that the live drive view and analysis review both use, so a detection cannot look one way on Android, another on Windows, and a third in review. `DriveOverlayFactory` maps live recognition and offline analysis onto the same `DriveOverlay` list, `DriveOverlayLayout` does the debug filtering and fit/fill projection, `ConfirmedOverlayTracker` owns how long a confirmed plate lingers once its car leaves view, and `DriveOverlayStyle` holds one entry per overlay kind. Overlaying MAUI content on the native preview is not a compositing gamble here: the drive controls, metrics, and scrim already sit above the live surface on both platforms. The overlay must however share the preview's *layout box*, not the page's — on Windows the preview sizes itself to the source aspect and centres, so an overlay stretched across the page projects detections against a viewport the video does not occupy and the boxes drift off the plates. `DrivePage` therefore wraps the preview and the overlay in a centred `Grid` that hugs the preview. What the preview surface still decides is how it fits the frame — Android fills for camera and letterboxes for network streams, Windows letterboxes both — so the handlers report `CameraScaleMode` and `StreamScaleMode` to `CameraPreview` and the overlay projects against whichever is active. Android forces `PreviewView.ImplementationMode.Compatible` so the preview is a `TextureView` composited in normal view order rather than a `SurfaceView` hole. ARGB and BGRA decoded frames use shared Core factories and one YUV color conversion formula. Windows locks `SoftwareBitmap` memory directly and passes its actual stride to the shared BGRA factory, avoiding the previous full-frame managed byte-array allocation on every webcam frame.

## Resolution before frame rate

License-plate OCR fails when characters occupy too few pixels, so the camera path asks CameraX for 3840×2160 analysis and accepts the nearest device-supported resolution. The configured recognition-rate gate decides which frames are copied from CameraX. This gives the detector multiple observations while preserving character detail without copying every preview frame by default.

`ImageAnalysis.StrategyKeepOnlyLatest` is paired with an application-level slot of capacity one. There are therefore two independent backpressure boundaries: CameraX does not queue proxies, and inference does not queue copied frames. A slow/thermally throttled phone reduces sampling rate rather than increasing latency or memory.

Windows Drive uses `MediaCapture` for the live preview and a `MediaFrameReader` for BGRA frames. It requests exclusive camera control because WinRT's shared-read-only capture mode cannot change device configuration. `WindowsWebcamFrameSource` applies the requested zoom through `VideoDeviceController.ZoomControl`, clamps it to the webcam's reported min/max/step, reapplies it after initialization and preview start, and reports unsupported devices without failing capture. Exclusive control means another application cannot simultaneously control the selected webcam. The frame callback atomically replaces one pending `SoftwareBitmap`; a single worker converts only the latest bitmap into pooled planar YUV and submits it to the same capacity-one recognition slot. Camera enumeration, selection, preview, and frame conversion remain in the Windows adapter, while inference, consensus, lookup, and persistence stay shared.

Camera planes are copied because an `ImageProxy` must be closed promptly and its buffers become invalid afterwards. Each Java plane is copied directly into its final pooled managed array; there is no intermediate rented array or second copy. Preprocessors bilinearly sample RGB values directly from rotated YUV planes into reusable detector/OCR tensors; they never materialize a 4K RGB bitmap. A per-frame sampler pins the three plane spans and their stride/rotation metadata once, instead of resolving pooled memory twelve times for every detector output pixel. OCR still samples its crop from the full-resolution source frame.

Zoom is applied with CameraX `CameraControl.SetZoomRatio` after preview and analysis are bound to the same camera, so both outputs receive the same sensor crop. The requested ratio is retained and reapplied after camera or lifecycle rebinding. The AndroidX `ZoomState` live-data value is a Java peer and must be converted with Android's Java-aware cast; a normal CLR interface cast silently yields `null` on affected runtimes. Zoom futures are observed and logged so camera failures cannot disappear silently again.

## Inference contracts

The source detector input is `[1,3,608,608]` RGB `float32`; Android LiteRT exposes the equivalent `[1,608,608,3]` layout. Both are normalized to 0–1 and letterboxed with value 114. Windows decodes end-to-end rows as `[batch,x1,y1,x2,y2,class,score]`. Android receives raw `[1,7581,4]` boxes and `[1,7581,1]` scores, then applies the shared confidence filter and NMS. Both paths map final coordinates back through letterbox padding and the road ROI.

The OCR input is `[1,64,128,3]` RGB `uint8`. The model performs its own normalization. It emits ten character slots over `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_` plus a 66-class region output. The decoder retains the best three alternatives per slot for temporal fusion.

The detector's trained YOLO graph is shared, but its execution adapter is platform-specific. Windows keeps the end-to-end ONNX graph and attempts DirectML before CPU. The Android build removes the ONNX Non-Maximum Suppression tail at build time, converts the fixed raw box/score graph to float32 LiteRT, and executes it in NHWC layout. The generated raw graph also replaces the detector head's uneven 65-channel Split with equivalent static Slice nodes and removes a singleton ReduceMax while preserving the fixed outputs. Shared C# code owns confidence filtering, coordinate mapping, and NMS.

Android converts the CCT-S V2 OCR graph to float32 LiteRT while preserving its uint8 NHWC input and two float outputs. Detector and OCR each create a GPU-only LiteRT compiled model and accept it only after a complete warm inference; if that fails, they create an explicit CPU model. Windows keeps both models on ONNX Runtime and attempts DirectML before CPU. Preprocessing, OCR decoding, and detector postprocessing live below the platform runners so both runtimes use identical recognition semantics. Model sessions and reusable buffers are long-lived, and runs are serialized per model. The selected detector and OCR backend names are retained on every recognition diagnostic frame and shown in Drive and Analyze diagnostics.

The Android Release project publishes CoreCLR JIT for arm64 with ReadyToRun disabled. Mono AOT is not used because the pinned .NET 10 Android toolchain's cross-assembler fails both universal and arm64 profiled-AOT packaging. Model execution remains native LiteRT; CoreCLR accelerates the managed preprocessing path. .NET 10 still classifies CoreCLR on Android as experimental and not intended for production use, so sustained device stability remains a release risk to validate.

## Confirmation policy

Each track retains at most twelve observations and expires after 1.5 seconds without a match. Association runs in three ordered tiers: exact weighted OCR identity, one-character OCR variation with a tighter geometry gate, and timestamp-aware constant-velocity prediction for compatible partial or near-identical reads. Center movement, scale change, previous/predicted overlap, and elapsed time constrain every candidate. Conflicting full plate strings are never joined solely because their boxes overlap.

Within each tier a maximum-weight bipartite assignment selects one global one-to-one mapping between observations and tracks. This avoids the order-dependent identity swaps of greedy nearest-box matching when several vehicles are visible. The tier, predicted box, movement, scale, overlap, edit distance, and score are retained in optional diagnostics so thresholds can be tuned from replay evidence rather than guesses.

A complete Dutch plate can use a narrow expedited path when two distinct frames contain exactly the same text, both observations pass strict OCR, character-margin, crop-quality, and combined-evidence thresholds, and the text matches a valid Dutch sidecode. This recovers short-lived plates on devices that cannot produce a third AI frame. Partial and foreign plates, weaker pairs, and every conflicting sequence continue through normal consensus.

Normal consensus requires at least three distinct frames to agree and its weighted share and winner margin to pass the configured thresholds. Weight combines detection confidence, OCR confidence, and a crop-quality estimate. If exact strings differ, character alternatives are fused position by position. Every selected character must have at least 60% support; this prevents three mutually different final characters from being accepted merely because the other five agree.

All detector, crop-quality, tracking, association-ranking, normal-consensus, and strong-fast-path thresholds live in one `RecognitionTuningConfiguration` object. The MAUI dependency container shares that same instance with Drive and Analyze on both platforms, and Settings formats every property into read-only subsections. This gives replay tests and future editable settings one source of truth instead of separate UI and algorithm defaults.

For OCR results classified as Dutch, a confirmed string must match one of RDW sidecode layouts 1–14. Formatting uses the exact group lengths for that layout rather than guessing where hyphens belong.

## Persistence

Confirmed sightings within three minutes of the same plate and the same trip are merged. The merge keeps the earliest first-seen time, advances the last-seen time, adds observation counts, keeps the strongest confidence, and fills missing GPS/RDW facts. A trip boundary always creates a distinct appearance, even when two drives happen close together.

The schema is EF Core Code First: `Models/` owns the mapped classes, `LprDbContext` owns their configuration, and `Migrations/` owns the SQL, so a schema change is a model edit plus `dotnet ef migrations add`. `InitializeAsync` applies pending migrations, switches SQLite to WAL mode, and closes trips that process termination left open. Indexes cover plate/time, trip/time, route points, and catalog price. Timestamps are stored as round-trip UTC text, which is fixed width and therefore sortable, and catalog prices as REAL, because EF Core cannot sort a decimal stored as text in SQLite. A database written before the Code First conversion has no `__EFMigrationsHistory` table; it is discarded and rebuilt rather than migrated, since a later drive recreates the history.

RDW data is intentionally a second SQLite database because it is large and replaceable. It is deliberately not Code First: the downloader produces it, and `RdwVehicleLookup` reads it through a keyless model mapped to the stable `rdw_vehicles` view, so the downloader's schema stays decoupled from the app and the app owns no migrations for it.

## Security and privacy defaults

- No network permission is declared.
- Models and RDW data are read locally.
- Raw source frames and plate crops are never persisted.
- Vehicle images are disabled by default. When enabled, only confirmation frames are encoded; files remain in private app storage and are deleted with history.
- The Android manifest disallows cleartext traffic.
- Models are verified by byte length and SHA-256 before use on Android and Windows.
- RDW imports are copied to a temporary file, schema-validated, and atomically moved.
- Location is optional and a missing permission does not block recognition.
- Android background recognition is disabled by default. When explicitly enabled for a drive, a camera foreground service keeps CameraX analysis active and exposes a persistent notification with a stop action; the normal foreground-only lifecycle remains unchanged when disabled.

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

Recognition diagnostics use milliseconds consistently. Live telemetry reports source and preview cadence as milliseconds between frames, while AI processing reports the actual stopwatch duration of the latest completed recognition. Per-recognition diagnostics separately record detector and plate-reader wait, input preparation, model execution, and output processing time, plus crop-quality evaluation, every detector candidate (including empty or skipped OCR), tracking time, active tracks, observation counts, current-frame associations, and live latest-slot replacements. Enabling diagnostics in Settings shows the same timing names and candidate/track overlays in Drive and Analyze; analyzed-video JSON retains the compact numeric/text diagnostics but never image pixels.

Analysis progress reports average total, decode, and recognition milliseconds per processed frame. Those cumulative progress values are not persisted. When recognition diagnostics are enabled, progress also carries the latest `RecognitionStreamDiagnostics` object so Analyze renders the same structured timing control as Drive without reconstructing individual values in its view model. The same compact per-frame model and tracking diagnostics are persisted so runs can be compared later without retaining frames. Windows attempts DirectML for detector and OCR sessions, then falls back to the multi-core CPU provider when DirectML is absent or session creation fails.

The Windows Media Foundation frame source lives in a separate Windows-only library
referenced by both the MAUI app and the end-to-end test project. A local video can
therefore be replayed through the production decoder, production ONNX models, and
the same `RecognitionStreamProcessor` used by Drive and Analyze. The opt-in fixture
defaults to the first 30 seconds and approximately two analyzed frames per second,
accepts arbitrary duration and source-frame interval overrides, and can write the
complete diagnostic result as JSON. Large source videos and generated reports remain
outside version control.
