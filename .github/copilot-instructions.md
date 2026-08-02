# DeveMobileLPR Engineering Instructions

## Product and quality bar

DeveMobileLPR is an offline-first .NET 10 MAUI application for Dutch license-plate recognition on Android and Windows. Android uses CameraX; Windows uses MediaCapture. Both platforms support recorded-video analysis and share recognition, inference, persistence, view models, and MAUI UI wherever platform APIs do not force a boundary.

Treat this as production software used around vehicles and sensitive location/plate data:

- Build durable solutions at the owning abstraction. Do not accumulate TODO fixes, duplicate implementations, compatibility shims without an exit path, or knowingly leave a broken adjacent state.
- Prefer a small complete change over a broad partial rewrite. Preserve public contracts unless the requested behavior requires changing them.
- Keep warnings-as-errors, nullable analysis, deterministic builds, lock files, cancellation, disposal, and tests healthy.
- Resolve root causes. A successful compile is necessary, not evidence that runtime behavior or layout is correct.
- Preserve user-authored changes. Work with a dirty tree and never revert unrelated edits.
- Before adding a helper, control, model, or service, search for an existing equivalent.
- Call out a material defect or risk discovered nearby, but do not expand into unrelated cleanup.

## Privacy and safety invariants

- Recognition, video analysis, history, and RDW lookup stay on-device. Do not add Internet permission, remote inference, analytics, crash telemetry, or upload behavior without explicit approval.
- Never persist raw frames or plate crops. Persist normalized sightings, compact analysis metadata, optional route points, and explicitly selected RDW data only.
- Never commit ONNX binaries, APKs, captured media, plate crops, RDW databases, keystores, secrets, or signing credentials.
- Location is optional. Recognition and history must continue when location is denied or unavailable.
- Recognition requires temporal agreement. Never replace multi-frame consensus with single-frame acceptance.
- Camera ingestion remains latest-frame-only with bounded memory. Pooled YUV frames must be disposed deterministically.
- Do not encourage interaction while driving. Controls used during a drive must remain low-distraction.

## Solution ownership

Put code in the lowest reusable layer that owns the behavior:

1. `DeveMobileLPR.Core`
	- Geometry, imaging contracts, YUV primitives, Dutch plate rules, tracking, consensus, and platform-neutral video-analysis contracts.
	- Must not reference MAUI, Android, WinUI, ONNX Runtime, or SQLite.
2. `DeveMobileLPR.Inference`
	- Detector/OCR preprocessing, exact tensor contracts, ONNX execution, decoding, and frame-to-recognition projection.
	- Keep hot paths allocation-aware and platform-neutral.
3. `DeveMobileLPR.Storage`
	- SQLite sightings/trips, JSON analysis metadata, RDW lookup contracts, WAL behavior, atomic replacement, and migrations/compatibility.
4. `DeveMobileLPR.RdwDownloader`
	- Official dataset paging, resumability, joining, indexing, and final database validation.
5. `DeveMobileLPR.App`
	- Shared MAUI pages, controls, view models, app services, navigation, and presentation state.
	- Keep business rules out of code-behind. Code-behind should bridge UI events only.
6. `Platforms/Android` and Android-only adapters
	- CameraX, Android permissions, Android location, Android media decoding, and model asset installation.
7. `Platforms/Windows` and Windows-only adapters
	- MediaCapture/webcam, WinUI handlers, MediaComposition decoding, and Windows frame conversion.

If Android and Windows need the same behavior, define one shared contract/implementation and keep only API adaptation platform-specific. Do not copy a coordinator or algorithm merely because the input API differs. When conditional compilation currently selects platform-specific files with the same service name, preserve the shared public behavior and move newly reusable logic downward.

## Cross-platform design rules

- Shared view models expose state and commands; pages bind to them. Platform handlers render or acquire native data.
- Platform sources feed the same `Yuv420Frame`, recognition, temporal consensus, and persistence paths.
- Use MAUI controls for shared UI unless native rendering or capture is required.
- A feature is complete only when Android and Windows behavior is considered. If one platform cannot support it, make that limitation explicit and graceful.
- Coordinate transforms must match media scaling:
  - `AspectFit` / `Uniform`: use `min(viewWidth/sourceWidth, viewHeight/sourceHeight)` and centered letterbox offsets.
  - `AspectFill` / `UniformToFill`: use `max(...)` and centered crop offsets.
- Persist source dimensions with detection bounds when results must be rendered later. Old persisted data must deserialize safely and communicate unavailable geometry.

## Lifecycle and concurrency

- Cancellation belongs to explicit user intent or object disposal, not incidental Shell tab navigation.
- Switching tabs must not stop video analysis, geometry enrichment, downloads, or a drive unless the product explicitly says so.
- Back/Close may cancel work owned by the view being closed; Cancel must remain explicit and responsive.
- Use one bounded latest-frame slot for live capture. Never queue frames without a strict capacity.
- Stop/dispose camera readers, players, inference sessions, cancellation sources, pooled buffers, and event subscriptions at their ownership boundary.
- Publish UI-bound state on the MAUI main thread. Do not mutate WinUI/Android views from frame workers.
- Avoid fire-and-forget work unless failures are surfaced into user-visible state and lifetime is owned.

## Storage and compatibility

- SQLite uses WAL mode. Repeated sightings inside the merge window update the existing sighting.
- RDW import is user-selected, validated before activation, and atomically replaced. Missing RDW data is a supported state.
- The imported database exposes the stable `rdw_vehicles` view documented in `config/rdw-view.example.sql`.
- JSON analysis changes must remain backward compatible. Prefer optional/defaulted fields and add round-trip tests.
- Deleting an analysis deletes its analysis metadata only unless the source is demonstrably an app-owned staging copy with no remaining references. Never delete a user-selected original video.

## Model contracts

- Detector: YOLOv9-S plate detector, float RGB CHW `[1,3,608,608]`, letterbox value 114.
- Detector rows: `[batch,x1,y1,x2,y2,class,score]`.
- OCR: CCT-S V2, uint8 RGB NHWC `[1,64,128,3]`, ten character slots.
- OCR alphabet: `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_`.
- Model URLs, sizes, and SHA-256 values are pinned in both `eng/Download-Models.ps1` and `ModelCatalog`; update both and extend model-contract tests together.
- Android LiteRT attempts GPU first and retains an explicit LiteRT CPU fallback. Windows ONNX Runtime attempts DirectML before CPU.
- The bundled detector detects license plates, not whole vehicles. Do not label plate bounds as vehicle bounds.

## UI implementation standard

- Follow `docs/ui-design-system.md` and existing resources before introducing colors, spacing, typography, or control styles.
- Optimize operational screens for the primary task. Video/camera media should consume available space while essential controls remain visible.
- Keep stable grid tracks and dimensions for sliders, timelines, media, and compact icon controls. Dynamic content must not shift navigation.
- Empty states are compact content near their owning controls, not full-screen overlays. Bind explicit empty-state properties when framework `EmptyView` behavior conflicts with headers or refresh containers.
- Provide semantic descriptions/tooltips for icon-only controls. Use familiar symbols and existing styles.
- Verify both narrow/mobile and wide/desktop layouts. Check clipping, overlap, text truncation, and z-order at runtime.
- For Windows UI/runtime work, load and follow the `maui-windows-ui-automation` skill.

## Fast engineering workflow

1. Start from the failing behavior, direct owner, or nearest existing test.
2. Read only enough neighboring code to state one falsifiable hypothesis and one cheap check.
3. Make the smallest grounded edit.
4. Immediately run the narrowest executable check that can disprove the change.
5. Repair the same slice and rerun before widening scope.
6. Add or update tests proportional to persistence, inference, lifecycle, or shared-contract risk.
7. Run the full repository build before completion.
8. For visible Windows behavior, launch the exact published artifact and verify it with UI Automation and a screenshot. Do not claim a visual feature works from compilation alone.

Do not repeatedly map the whole repository after identifying the controlling path. Do not batch unrelated refactors into a bug fix.

## Build and validation ladder

Prerequisites are the SDK from `global.json`, PowerShell 7, and the MAUI workloads. Normal builds do not install workloads or request elevation. If missing, install once from an elevated terminal:

```powershell
dotnet workload install maui-android maui-windows
```

Use the cheapest relevant command first:

```powershell
# Focused test project
dotnet test .\tests\DeveMobileLPR.Storage.Tests\DeveMobileLPR.Storage.Tests.csproj -c Release --no-restore

# Focused Windows app compile
dotnet build .\src\DeveMobileLPR.App\DeveMobileLPR.App.csproj `
  -f net10.0-windows10.0.19041.0 -c Release --no-restore

# Full required validation: locked restore, both apps, tests, model contract, all publishes
.\build.ps1 -Configuration Release

# Faster full validation without Android build/package when explicitly appropriate
.\build.ps1 -Configuration Release -SkipAndroid
```

Rules:

- Run affected Core/Inference/Storage tests when changing those layers.
- Run the real-model contract test for tensor/model/preprocessing changes.
- Run `./build.ps1 -Configuration Release` before completing implementation work unless the environment is genuinely blocked.
- `build.ps1` publishes:
  - `artifacts/windows/win-x64/DeveMobileLPR.exe`
  - `artifacts/android/nl.deve.mobilelpr-Signed.apk`
  - `artifacts/rdw-downloader/*.zip`
- A running workspace executable locks Windows build/publish DLLs. Stop only processes whose `Path` is under this workspace before rebuilding; never terminate a user's downloaded/installed copy.
- Run `git diff --check` and inspect `git status` before commit. Ignore generated `artifacts`, `bin`, `obj`, and test-result churn.

## Runtime verification

- Test the artifact produced by the current build, not an older downloaded release.
- For Windows, use `artifacts/windows/win-x64/DeveMobileLPR.exe` after `build.ps1`.
- Read UI state through semantic names and native patterns; do not rely only on pixel coordinates.
- Verify state transitions, not just control presence: slider values before/after navigation, progress before/after tab switches, persisted JSON after analysis, and bounds relative to adjacent controls.
- Capture and inspect screenshots for visual claims such as detection boxes, media sizing, empty-state placement, clipping, and overlap.
- Never validate destructive controls against user data. Use temporary fixtures or inspect control presence without invoking it.
- If hardware is absent, verify device enumeration and the exact graceful diagnostic. Do not claim camera frames were tested.

## CI, versioning, and Git

- `.github/workflows/githubactionsbuilds.yml` is the authoritative CI/release workflow.
- CI installs workloads explicitly; local `build.ps1` only checks prerequisites.
- Build numbers come from `onyxmueller/build-tag-number@v1`.
- Assembly, display, artifact, tag, and release versions use `1.0.<build number>`; Android `ApplicationVersion` uses the numeric build number.
- Signed releases are created only from `master` when all four Android signing secrets are configured.
- Do not commit or create branches unless requested. When asked to publish changes, stage only intended files, use a behavioral commit message, push the current feature branch, and confirm a clean synchronized tree.
