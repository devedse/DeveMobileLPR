# Map web dependencies

Leaflet and Leaflet.markercluster are normal npm dependencies declared in `package.json` and locked by `package-lock.json`.
The app does not run npm during a Visual Studio build: the exact browser files needed at runtime are generated into
`Resources/Raw/wwwroot/map/vendor` and committed. Therefore cloning the repository, restoring NuGet packages, and pressing
Run in Visual Studio is sufficient.

When changing a map dependency:

1. Run `npm install` in this directory to update the lock file.
2. Run `npm run sync` to update the committed runtime files and their SHA-256 manifest.
3. Run `npm run verify` before committing.

CI performs a clean `npm ci` followed by `npm run verify`, so a lock-file or generated-file mismatch fails the build.
Application-owned `index.html`, `history-map.js`, and `history-map.css` live beside the generated `vendor` directory and
are edited directly.
