[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\DeveMobileLPR.Android\DeveMobileLPR.Android.csproj'
$output = Join-Path $root "artifacts\windows\$RuntimeIdentifier"

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($output) | Out-Null

dotnet publish $project `
    --framework 'net10.0-windows10.0.19041.0' `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --no-restore `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishDir="$output\"

$executables = @(Get-ChildItem -LiteralPath $output -Filter '*.exe' -File)
if ($executables.Count -ne 1) {
    throw "Expected one Windows executable in $output, found $($executables.Count)."
}

$otherFiles = @(Get-ChildItem -LiteralPath $output -File | Where-Object Extension -ne '.exe')
if ($otherFiles.Count -ne 0) {
    throw "Expected a single-file Windows publish, but found: $($otherFiles.Name -join ', ')"
}

Write-Host "Published $($executables[0].FullName)"