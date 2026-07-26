# DeveMobileLPR

DeveMobileLPR is an offline-first Android license-plate recognition app written in C#. It is designed for a securely mounted phone looking through a car windscreen. Camera frames stay on the phone: the app stores confirmed plate text, time, optional position, and matched RDW vehicle facts, but does not store raw video or plate crops.

The first implementation targets Android because direct CameraX access gives the required control over analysis resolution, YUV frames, zoom, and backpressure. The reusable recognition, inference, tracking, and SQLite layers target plain .NET.

## What is implemented

- Direct CameraX preview and YUV analysis, requesting a practical 3840×2160 stream with device-specific fallback.
- Manual 1×–3× zoom control and a visible road-region guide.
- Latest-frame-only ingestion at up to four high-resolution samples per second. Slow inference drops stale frames instead of consuming more memory.
- MIT-licensed YOLOv9-S 608 plate detection and CCT-S V2 global OCR through ONNX Runtime.
- Direct YUV-to-model sampling: there is no full-frame RGB bitmap allocation.
- IoU tracking and confidence/quality-weighted multi-frame consensus. A plate needs at least three supporting frames and character-level majority support.
- Dutch sidecode validation and official three-group formatting for sidecodes 1–14.
- Local SQLite sightings, duplicate merging, recent history, optional GPS, and “most expensive car” statistics.
- Import of an existing RDW SQLite database through Android's document picker. Imports are validated and replaced atomically.
- Model integrity verification at download time and again when Android installs bundled assets.
- Unit tests, SQLite integration tests, and an inference smoke test that loads and executes both real ONNX files.
- Reproducible NuGet lock files and warnings-as-errors.
- A GitHub Actions pipeline on every push and manual run. Every successful build uploads a versioned APK; signed pushes to `master` also create a GitHub release.

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

- `DeveMobileLPR.Core` owns geometry, YUV representation, tracking, plate rules, and contracts.
- `DeveMobileLPR.Inference` owns exact ONNX tensor contracts, preprocessing, execution providers, and decoding.
- `DeveMobileLPR.Storage` owns sightings and the stable RDW view contract.
- `DeveMobileLPR.Android` owns CameraX, permissions, location, model installation, lifecycle, and UI.

See [docs/architecture.md](docs/architecture.md) for the implementation rationale and tuning points.

## Prerequisites

- Windows, macOS, or Linux with the SDK selected by `global.json`.
- The .NET Android workload: `dotnet workload install android`.
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
2. ensures the Android workload is installed;
3. restores the locked dependency graph;
4. builds with warnings as errors;
5. runs unit/integration tests and the real-model contract test;
6. publishes an APK to `artifacts/android`.

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

## RDW database contract

The app expects a SQLite database containing this view:

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

`normalized_plate` is uppercase without hyphens or spaces and must be efficiently indexed in the source table. Adapt and run [config/rdw-view.example.sql](config/rdw-view.example.sql) against the database produced by your downloader. In the app, tap **Import RDW** and select that SQLite file. The app copies it into private application storage, validates the view, and only then replaces the previous database.

The RDW database may be several gigabytes. Ensure the phone has enough free storage before import. Vehicle lookup remains optional; recognition and history work without it.

## CI and releases

`.github/workflows/githubactionsbuilds.yml` runs for every push and manual dispatch. It uses the same versioning convention as DevePXEBoot: `onyxmueller/build-tag-number@v1` generates the build number and all outputs use `1.0.<build number>`.

- The .NET assemblies, Android display version, artifact name, Git tag, and release name all use `1.0.<build number>`. Android's numeric version code uses the generated build number.
- Every successful run uploads the installable APK as a workflow artifact for 14 days; test results are retained for 7 days.
- Without signing secrets, Android applies its development signature. That artifact is suitable for testing, not store distribution.
- A push to `master` with all four signing secrets creates the latest GitHub release tagged `1.0.<build number>`.

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
2. Import the RDW database while parked, if desired.
3. Fix the phone securely in landscape orientation with the rear camera unobstructed.
4. Aim the yellow guide at the road, set zoom, and tap **Start before driving**.
5. Do not interact with the app while the vehicle is moving.

Heat, windscreen reflections, shutter speed, plate pixel height, and device-specific CameraX resolution support dominate real-world accuracy. The app displays the actual selected analysis resolution and per-frame inference time so a physical road benchmark can make those trade-offs measurable.

## Privacy and legal responsibility

License plates and precise travel history can be personal data. DeveMobileLPR performs recognition locally and deliberately retains no images, but the sightings database can still reveal movements. Protect the phone, keep only data you need, and determine the lawful basis and retention policy required for the countries where you use it. Do not use the software for covert surveillance or while manually operating a phone in traffic.

This repository is an engineering implementation, not legal advice or a guarantee that a particular use is lawful.
