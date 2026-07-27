# DeveMobileLPR

DeveMobileLPR is an offline-first .NET MAUI license-plate recognition app for Android and Windows, written in C#. Drive mode accepts CameraX capture from a securely mounted Android phone or live webcam capture on Windows, and both platforms support recorded-video analysis. Media stays on the device: the app stores confirmed plate text, trips, optional route points, matched RDW vehicle facts, and compact video-analysis metadata, but does not store raw frames or plate crops.

The first implementation targets Android because direct CameraX access gives the required control over analysis resolution, YUV frames, zoom, and backpressure. The reusable recognition, inference, tracking, and SQLite layers target plain .NET.

## What is implemented

- Direct CameraX preview and YUV analysis, requesting a practical 3840×2160 stream with device-specific fallback.
- Live Windows webcam preview and frame analysis with camera selection and latest-frame backpressure.
- A full-screen, low-distraction Drive mode with live plate boxes, OCR text, RDW vehicle labels, camera selection, 1×–4× zoom, and a visible road-region guide.
- Latest-frame-only ingestion at up to four high-resolution samples per second. Slow inference drops stale frames instead of consuming more memory.
- MIT-licensed YOLOv9-S 608 plate detection and CCT-S V2 global OCR through ONNX Runtime.
- Direct YUV-to-model sampling: there is no full-frame RGB bitmap allocation.
- IoU tracking and confidence/quality-weighted multi-frame consensus. A plate needs at least three supporting frames and character-level majority support.
- Dutch sidecode validation and official three-group formatting for sidecodes 1–14.
- Local SQLite trips, filtered route traces, duplicate merging within a drive, a searchable vehicle library, daily/drive statistics, CSV export, optional GPS, and “most expensive car” highlights.
- Recorded-video analysis on Android and Windows with scaled decoding, temporal consensus, a unified processing/history list, lazy previews, frame-snapped timelines, and plate-based seeking.
- .NET MAUI Shell navigation with Drive, History, and Settings surfaces plus trip and vehicle detail views.
- A resumable C# console downloader that builds the app's indexed SQLite database directly from official RDW Open Data.
- Import of the generated RDW SQLite database through Android's document picker. Imports are validated and replaced atomically.
- Model integrity verification at download time and before bundled assets are used on Android or Windows.
- Unit tests, SQLite integration tests, and an inference smoke test that loads and executes both real ONNX files.
- Reproducible NuGet lock files and warnings-as-errors.
- A GitHub Actions pipeline on every push and manual run. Every successful build uploads a versioned APK, self-contained Windows executable, and RDW-downloader ZIP; signed pushes to `master` also create a GitHub release.

## Architecture

```text
CameraX 4K-ish YUV_420_888
        │  throttle + copy three pooled planes
        ▼
latest-frame slot (capacity 1)
        │
        ▼
road ROI → YOLOv9 detector (RGB CHW float, 608×608)
        │
        ├─ plate crop → CCT OCR (RGB NHWC uint8, 128×64)
        │
        ▼
IoU tracks → 3+ frame weighted consensus → Dutch validation
        │
        ├─ optional indexed RDW lookup
        ▼
local SQLite sighting + optional GPS → history/statistics UI
```

The important boundaries are deliberate:

- `DeveMobileLPR.Core` owns geometry, YUV representation, tracking, plate rules, and video-analysis contracts.
- `DeveMobileLPR.Inference` owns exact ONNX tensor contracts, preprocessing, execution providers, and decoding.
- `DeveMobileLPR.Storage` owns sightings, compact video-analysis persistence, and the stable RDW view contract.
- `DeveMobileLPR.RdwDownloader` owns official dataset paging, resumable imports, joining, and final database validation.
- The MAUI app project owns the shared UI and platform hosts. Android owns CameraX, permissions, location, and model installation; Windows owns MediaCapture webcam and MediaComposition video adapters. Both feed shared recognition and persistence layers.

See [docs/architecture.md](docs/architecture.md) for the implementation rationale and tuning points.

## Prerequisites

- Windows, macOS, or Linux with the SDK selected by `global.json`.
- The .NET MAUI Android and Windows workloads: `dotnet workload install maui-android maui-windows`.
- PowerShell 7 (`pwsh`) for the repository scripts.
- Android API 26 or newer on the target phone. A 64-bit phone with at least 4 GB RAM is recommended.

The checked-in SDK feature band is intentionally exact. On this development machine the Android build was also validated with the installed .NET 10.0.110 workload set because the newer Visual Studio workload registration was inconsistent; clean CI installs the workload declared by `global.json`.

## Build and test

From the repository root:

```powershell
./build.ps1 -Configuration Release
```

That command:

1. downloads and SHA-256-verifies both models;
2. ensures the Android and Windows workloads are installed;
3. restores the locked dependency graph;
4. builds with warnings as errors;
5. runs unit/integration tests and the real-model contract test;
6. publishes the portable RDW-downloader ZIP to `artifacts/rdw-downloader`, an APK to `artifacts/android`, and the self-contained `DeveMobileLPR.exe` to `artifacts/windows/win-x64`.

For portable development without Android packaging:

```powershell
./build.ps1 -Configuration Release -SkipAndroid
```

To run only the model check:

```powershell
./eng/Download-Models.ps1
$env:DEVEMOBILELPR_MODEL_DIR = (Resolve-Path ./artifacts/models).Path
dotnet test ./tests/DeveMobileLPR.Inference.Tests -c Release --filter 'Category=Model'
```

Model binaries are build inputs and are ignored by Git. Their URLs, sizes, and hashes are pinned in both `ModelCatalog` and `eng/Download-Models.ps1`; details are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Build and import the RDW database

The app stays offline while recognizing. Build its indexed RDW snapshot on a desktop using the included console project:

```powershell
dotnet run --project ./src/DeveMobileLPR.RdwDownloader -c Release -- `
  --output C:\RdwData\rdw.sqlite
```

The tool streams the official vehicle and fuel datasets, resumes through `<output>.building` after interruption, verifies that the RDW snapshots did not change mid-run, validates counts and SQLite integrity, and only then replaces the requested final output. A free Socrata app token is optional but recommended for the roughly 17-million-row full import; set `SOCRATA_APP_TOKEN` rather than placing it in command history.

For a quick live test, add `--sample-rows 100 --page-size 50 --restart`. Sample output is deliberately marked and is not a complete lookup database.

Copy the finished file to the phone, open **Settings**, tap **Import / replace**, and select it. The app copies it into private storage, validates it, and only then replaces the previous database. Recognition and history work without RDW; only vehicle enrichment and RDW-backed statistics will be absent.

See [docs/rdw-database.md](docs/rdw-database.md) for source datasets, free-token setup, sizing expectations, consistency guarantees, refresh behavior, and all options.

## RDW database contract

The included downloader creates a SQLite database containing this stable view:

```sql
rdw_vehicles(
  normalized_plate,
  make,
  model,
  catalog_price,
  registration_year,
  fuel_description,
  body_type
)
```

`normalized_plate` is uppercase without hyphens or spaces and is the primary key of the source table. [config/rdw-view.example.sql](config/rdw-view.example.sql) remains available only for users adapting a different pre-existing RDW dump.

The RDW database may be several gigabytes. Ensure the phone has enough free storage before import. Vehicle lookup remains optional; recognition and history work without it.

## CI and releases

`.github/workflows/githubactionsbuilds.yml` runs for every push and manual dispatch. It uses the same versioning convention as DevePXEBoot: `onyxmueller/build-tag-number@v1` generates the build number and all outputs use `1.0.<build number>`.

- The .NET assemblies, Android display version, artifact name, Git tag, and release name all use `1.0.<build number>`. Android's numeric version code uses the generated build number.
- Every successful run uploads the installable APK, self-contained Windows executable, and portable RDW-downloader ZIP as workflow artifacts for 14 days; test results are retained for 7 days.
- Without signing secrets, Android applies its development signature. That artifact is suitable for testing, not store distribution.
- A push to `master` with all four signing secrets creates the latest GitHub release tagged `1.0.<build number>`, containing the signed APK, Windows executable, and downloader ZIP.

Configure these GitHub Actions secrets for release signing:

| Secret | Meaning |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | Base64 contents of the release `.keystore` file |
| `ANDROID_KEYSTORE_PASSWORD` | Keystore password |
| `ANDROID_KEY_ALIAS` | Signing key alias |
| `ANDROID_KEY_PASSWORD` | Signing key password |

Never commit a keystore or signing password. Keep an offline backup; losing the key prevents compatible app upgrades.

## Phone setup and safe use

1. Install the APK and grant camera permission. Location permission is optional.
2. Import the RDW database from **Settings** while parked, if desired.
3. Fix the phone securely in landscape orientation with the rear camera unobstructed.
4. Select the camera, set zoom, and tap **Start drive** before moving.
5. Do not interact with the app while the vehicle is moving.

Heat, windscreen reflections, shutter speed, plate pixel height, and device-specific CameraX resolution support dominate real-world accuracy. Camera diagnostics report the selected analysis resolution and applied hardware zoom so a physical road benchmark can make those trade-offs measurable.

## Privacy and legal responsibility

License plates and precise travel history can be personal data. DeveMobileLPR performs recognition locally and deliberately retains no images, but the sightings database can still reveal movements. Protect the phone, keep only data you need, and determine the lawful basis and retention policy required for the countries where you use it. Do not use the software for covert surveillance or while manually operating a phone in traffic.

This repository is an engineering implementation, not legal advice or a guarantee that a particular use is lawful.
