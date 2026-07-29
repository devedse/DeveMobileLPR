[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipAndroid
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = $PSScriptRoot
& (Join-Path $root 'eng\Download-Models.ps1')
$testProjects = @(
    (Join-Path $root 'tests\DeveMobileLPR.Core.Tests\DeveMobileLPR.Core.Tests.csproj'),
    (Join-Path $root 'tests\DeveMobileLPR.Inference.Tests\DeveMobileLPR.Inference.Tests.csproj'),
    (Join-Path $root 'tests\DeveMobileLPR.Storage.Tests\DeveMobileLPR.Storage.Tests.csproj'),
    (Join-Path $root 'tests\DeveMobileLPR.RdwDownloader.Tests\DeveMobileLPR.RdwDownloader.Tests.csproj')
)

$requiredWorkloads = @('maui-windows')
if (-not $SkipAndroid) {
    $requiredWorkloads += 'maui-android'
}
$installedWorkloads = dotnet workload list
$missingWorkloads = @()
foreach ($workload in $requiredWorkloads) {
    if (-not ($installedWorkloads -match "^\s*$([regex]::Escape($workload))\s")) {
        $missingWorkloads += $workload
    }
}
if ($missingWorkloads.Count -ne 0) {
    throw "Missing .NET workload(s): $($missingWorkloads -join ', '). Install them once from an elevated terminal: dotnet workload install $($missingWorkloads -join ' ')"
}

if ($SkipAndroid) {
    $windowsProject = Join-Path $root 'src\DeveMobileLPR.App\DeveMobileLPR.App.csproj'
    dotnet restore (Join-Path $root 'DeveMobileLPR.slnx') --locked-mode
    dotnet build $windowsProject --framework net10.0-windows10.0.19041.0 --configuration $Configuration --runtime win-x64 --no-restore
    foreach ($project in $testProjects) {
        dotnet restore $project --locked-mode
        dotnet build $project --configuration $Configuration --no-restore
    }
}
else {
    dotnet restore (Join-Path $root 'DeveMobileLPR.slnx') --locked-mode
    dotnet build (Join-Path $root 'DeveMobileLPR.slnx') --configuration $Configuration --no-restore
}

foreach ($project in $testProjects) {
    dotnet test $project --configuration $Configuration --no-build --filter 'Category!=Model' --collect 'XPlat Code Coverage' --results-directory (Join-Path $root 'artifacts\test-results')
}
dotnet test (Join-Path $root 'tests\DeveMobileLPR.Inference.Tests\DeveMobileLPR.Inference.Tests.csproj') --configuration $Configuration --no-build --filter 'Category=Model'

& (Join-Path $root 'eng\Publish-RdwDownloader.ps1') -Configuration $Configuration
& (Join-Path $root 'eng\Publish-Windows.ps1') -Configuration $Configuration

if (-not $SkipAndroid) {
    & (Join-Path $root 'eng\Publish-Android.ps1') -Configuration $Configuration
}
