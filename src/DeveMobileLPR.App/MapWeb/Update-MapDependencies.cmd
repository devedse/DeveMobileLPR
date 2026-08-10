@echo off
setlocal

pushd "%~dp0" || exit /b 1

echo Updating Leaflet dependencies to their latest versions...
call npm install --save-exact leaflet@latest leaflet.markercluster@latest
if errorlevel 1 goto :failed

echo Synchronizing committed MAUI map assets...
call npm run sync
if errorlevel 1 goto :failed

echo Verifying generated map assets...
call npm run verify
if errorlevel 1 goto :failed

popd
echo Map dependencies and committed assets are up to date.
exit /b 0

:failed
set "exitCode=%errorlevel%"
popd
echo Map dependency update failed with exit code %exitCode%.
exit /b %exitCode%
