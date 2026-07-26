# Building the offline RDW database

DeveMobileLPR does not query RDW while recognizing plates. A desktop console tool creates a compact, indexed SQLite snapshot first; Android then imports that file into private app storage. Recognition and sighting history continue to work when no RDW database has been imported.

## Data sources and cost

The downloader uses two official RDW Open Data datasets:

- [Gekentekende voertuigen (`m9d7-ebf2`)](https://opendata.rdw.nl/Voertuigen/Open-Data-RDW-Gekentekende_voertuigen/m9d7-ebf2) for plate, make, model, catalog price, first-registration year, and body type.
- [Gekentekende voertuigen brandstof (`8ys7-d773`)](https://opendata.rdw.nl/Voertuigen/Open-Data-RDW-Gekentekende_voertuigen_brandstof/8ys7-d773) for the one or more fuels registered for a vehicle.

RDW publishes these datasets under CC0. The downloader, database builder, SQLite library, and CSV parser are all free software or public open-data services; there is no paid recognition or RDW API dependency.

Both source datasets currently contain roughly 17 million rows and change over time. The tool fetches current source counts and update timestamps at the start of every run instead of embedding those numbers.

## Quick start from source

Install the SDK selected by `global.json`, open PowerShell in the repository root, and run:

```powershell
dotnet run --project ./src/DeveMobileLPR.RdwDownloader -c Release -- `
  --output C:\RdwData\rdw.sqlite
```

With no `--output`, the default is `artifacts/rdw/rdw.sqlite` below the current directory.

Every CI build also publishes `DeveMobileLPR.RdwDownloader-<version>.zip`. Extract it on a machine with the .NET 10 runtime and run the portable DLL:

```powershell
dotnet ./DeveMobileLPR.RdwDownloader.dll --output C:\RdwData\rdw.sqlite
```

Use a local SSD. Exact duration and size depend on the current datasets, storage, network, and RDW service load; allow several hours and multiple gigabytes of free desktop space for the complete snapshot. The final phone also needs room for both Android's temporary import copy and the installed database during replacement.

## Public quota and optional app token

Small runs work without authentication. Unauthenticated requests share the public Socrata quota, however, so a full import is more reliable with a free Socrata application token:

```powershell
$env:SOCRATA_APP_TOKEN = 'your-token'
dotnet run --project ./src/DeveMobileLPR.RdwDownloader -c Release -- `
  --output C:\RdwData\rdw.sqlite
```

`--app-token <token>` is also supported, but an environment variable keeps the token out of shell history. The tool sends it only in the `X-App-Token` header to `opendata.rdw.nl`. Do not commit it.

## Manual GitHub Actions export

The **RDW Database Export** workflow builds a fresh complete snapshot on demand:

1. Open the repository's **Actions** tab.
2. Select **RDW Database Export**.
3. Select **Run workflow**.
4. Choose how long GitHub should retain the generated artifact and start the run.
5. When the run completes, download the `DeveMobileLPR-RDW-<run number>` artifact from its summary page.

The artifact contains the validated `rdw.sqlite` and `rdw.sqlite.sha256` checksum file. GitHub wraps them in an artifact ZIP; extract `rdw.sqlite` before copying it to the phone.

For a more reliable full download, configure an optional Actions repository secret named `SOCRATA_APP_TOKEN`. Without it, the workflow uses Socrata's shared public quota. GitHub-hosted runners are temporary, so a cancelled or failed workflow run cannot resume its partial download; triggering it again starts a new export.

## Resume, consistency, and replacement behavior

The in-progress database is `<output>.building`. Each API page and its cursor are committed in one SQLite transaction. If the process, network, or computer stops, rerun the same command and it resumes after the last committed plate.

The source is paged by stable keys instead of numeric offsets:

- vehicles: `kenteken`;
- fuels: `(kenteken, brandstof_volgnummer)`.

This avoids increasingly slow high offsets and prevents normal page boundaries from duplicating rows. The tool also:

1. checks that RDW still exposes every required field;
2. records each dataset's `rowsUpdatedAt` value in the partial database;
3. refuses to resume if the source changed or `--sample-rows` differs;
4. checks both source timestamps again after downloading;
5. checks complete-run source row counts, unique vehicle keys, and `PRAGMA quick_check`;
6. validates the same `rdw_vehicles` view used by Android;
7. replaces the requested final output only after all validation passes.

If RDW changed during a long run, or you intentionally want to discard the partial snapshot, add `--restart`. This deletes only the named `.building` database and its SQLite sidecars. An existing final `rdw.sqlite` remains untouched until a valid replacement is ready.

## Bounded smoke test

Before a full download, exercise the live API and complete database pipeline with a small sample:

```powershell
dotnet run --project ./src/DeveMobileLPR.RdwDownloader -c Release -- `
  --output C:\RdwData\rdw-sample.sqlite `
  --page-size 50 `
  --sample-rows 100 `
  --restart
```

Sample mode is recorded in `rdw_import_metadata` and clearly reported by the console. It proves the plumbing works, but it is not representative enough for driving use because it takes only the first ordered rows from each dataset.

## What is stored

The console tool deliberately does not mirror every RDW column. It stores the fields the app currently displays:

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

`normalized_plate` is uppercase without spaces or hyphens and is the primary lookup key. Multiple fuels are combined in sequence order, for example `Benzine / Elektriciteit`. Internal tables also retain source IDs, update timestamps, source and imported row counts, generation time, and whether the result is a sample.

The tool does not add sightings to this database. Sightings remain in the app's separate private SQLite database, so replacing the RDW snapshot does not erase trip history.

## Import into Android

1. Copy the completed `rdw.sqlite` to storage visible to the phone, using USB or another trusted transfer method.
2. Open DeveMobileLPR while parked.
3. Tap **Import RDW** and select the file.
4. Wait for **RDW database installed** before deleting the transferred copy.

Android streams the selected file to `rdw.sqlite.importing` in private app storage, validates the required view, and then replaces the previous installed snapshot. A failed or cancelled copy does not replace the previous database.

The phone does not download or refresh the full RDW dataset itself. That is intentional: importing and joining tens of millions of rows is a desktop maintenance task, while plate recognition must remain responsive and offline. To refresh RDW data later, rerun the console tool and import the new finished file.

## Command reference

```text
-o, --output <path>       Final SQLite path (default: artifacts/rdw/rdw.sqlite)
--page-size <1..50000>    Rows per committed API page (default: 50000)
--sample-rows <count>     Build a bounded, marked test database
--app-token <token>       Optional Socrata application token
--restart                 Discard the partial build and start again
-h, --help                Show help
```

The default page size balances request overhead against memory. Lower it only when diagnosing unreliable connections or constrained machines.
