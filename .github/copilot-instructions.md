# DeveMobileLPR Project Instructions

## Critical instructions

- Preserve user-authored changes. If files changed between requests, treat those edits as intentional and work around them.
- Implement only the requested behavior, but call out relevant defects or risks discovered while working.
- Before adding a helper, abstraction, or model, search the repository for an existing equivalent.
- Never commit model binaries, APKs, keystores, RDW databases, captured frames, plate crops, or signing credentials.
- Keep recognition and vehicle lookup fully on-device. Do not add an Internet permission or remote telemetry without explicit approval.
- Run `./build.ps1` before completing implementation work. Run the affected test projects when changing recognition, inference, or storage behavior.

## Project overview

DeveMobileLPR is a .NET 10 Android application for recognizing Dutch vehicle registration plates from a windscreen-mounted phone. It combines CameraX capture, ONNX Runtime inference, temporal consensus across multiple frames, SQLite sighting history, optional location, and offline RDW vehicle lookup.

The solution contains:

1. `DeveMobileLPR.Core`: imaging primitives, Dutch plate normalization, tracking, and temporal consensus.
2. `DeveMobileLPR.Inference`: detector/OCR preprocessing and ONNX Runtime model execution.
3. `DeveMobileLPR.Storage`: SQLite sightings and RDW lookup.
4. `DeveMobileLPR.App`: shared MAUI UI and platform adapters for Android and Windows.
5. `tests`: unit, SQLite integration, and real-model contract tests.

## Architecture and invariants

- Camera analysis uses a latest-frame slot with bounded memory. Do not introduce an unbounded frame queue.
- Camera frames use pooled YUV buffers and must be disposed deterministically.
- The requested analysis resolution is 3840x2160 with CameraX fallback when the device cannot provide it.
- Raw frames and plate crops are never persisted. Only normalized sightings and explicitly selected RDW data belong in SQLite.
- Location permission is optional. Recognition and history must continue to work when it is denied.
- Recognition requires multiple agreeing frames. Do not replace temporal consensus with single-frame acceptance.
- Dutch plates are stored normalized as uppercase ASCII without spaces or hyphens and displayed using supported Dutch sidecode formatting.

## Model contracts

- Detector: YOLOv9-S license-plate detector, float RGB CHW input `[1,3,608,608]`, letterbox value 114.
- Detector output rows: `[batch,x1,y1,x2,y2,class,score]`.
- OCR: CCT-S V2, uint8 RGB NHWC input `[1,64,128,3]`, ten character slots.
- OCR alphabet: `0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_`.
- Model URLs, byte sizes, and SHA-256 values are pinned in `eng/Download-Models.ps1` and `ModelCatalog`. Update both sides together and extend the model-contract tests when changing a model.
- Prefer ONNX operators supported by the mobile runtime. XNNPACK may be attempted, but CPU inference must remain a working fallback.

## Storage and RDW

- Sightings use SQLite WAL mode.
- Repeated sightings of the same plate within the configured merge window update the existing sighting instead of creating noise.
- RDW import is user-selected, validated before activation, and replaced atomically.
- The imported database must expose the stable `rdw_vehicles` view documented in `config/rdw-view.example.sql`.
- Vehicle lookup is optional and must fail gracefully when no RDW database is installed.

## Coding and validation

- Nullable reference types, analyzers, deterministic builds, and warnings-as-errors are enabled repository-wide.
- Keep platform-independent recognition logic out of the Android project when it can live in Core or Inference.
- Avoid reflection and unnecessary allocations in the per-frame path.
- Maintain cancellation and lifecycle safety: stopping the session must release camera, inference, and location resources.
- Use the checked-in NuGet lock files and restore with `--locked-mode` in automation.
- Standard validation: `./build.ps1`.
- Android packaging validation: `./eng/Publish-Android.ps1 -Configuration Release`.

## CI and versioning

- The primary workflow is `.github/workflows/githubactionsbuilds.yml` and runs on pushes and manual dispatches.
- Build numbers come from `onyxmueller/build-tag-number@v1`, matching the DevePXEBoot convention.
- Assembly, APK display version, artifact, tag, and release names use `1.0.<build number>`.
- Android `ApplicationVersion` uses the numeric build number so every released APK has an increasing version code.
- Signed releases are created only from `master` when all four Android signing secrets are configured.
