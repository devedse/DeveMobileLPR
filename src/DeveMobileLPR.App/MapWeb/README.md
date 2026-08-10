# Map web dependencies

Leaflet and Leaflet.markercluster are normal npm dependencies declared in `package.json` and locked by `package-lock.json`.
The app does not run npm during a Visual Studio build: the exact browser files needed at runtime are generated into
`Resources/Raw/wwwroot/map/vendor` and committed. Therefore cloning the repository, restoring NuGet packages, and pressing
Run in Visual Studio is sufficient.

When changing a map dependency:

1. Run `Update-MapDependencies.cmd` to update both Leaflet packages to their latest versions.
2. Review and test the resulting lock-file and generated-asset changes before committing.

The command file runs the equivalent npm install, asset synchronization, and verification steps. To select a specific
version instead, run `npm install --save-exact <package>@<version>` followed by `npm run sync` and `npm run verify`.

CI performs a clean `npm ci` followed by `npm run verify`, so a lock-file or generated-file mismatch fails the build.
Application-owned `index.html`, `history-map.js`, and `history-map.css` live beside the generated `vendor` directory and
are edited directly.
