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
    (Join-Path $root 'tests\DeveMobileLPR.Storage.Tests\DeveMobileLPR.Storage.Tests.csproj')
)

if ($SkipAndroid) {
    foreach ($project in $testProjects) {
        dotnet restore $project --locked-mode
        dotnet build $project --configuration $Configuration --no-restore
    }
}
else {
    dotnet workload install android --skip-manifest-update
    dotnet restore (Join-Path $root 'DeveMobileLPR.slnx') --locked-mode
    dotnet build (Join-Path $root 'DeveMobileLPR.slnx') --configuration $Configuration --no-restore
}

foreach ($project in $testProjects) {
    dotnet test $project --configuration $Configuration --no-build --filter 'Category!=Model' --collect 'XPlat Code Coverage' --results-directory (Join-Path $root 'artifacts\test-results')
}
dotnet test (Join-Path $root 'tests\DeveMobileLPR.Inference.Tests\DeveMobileLPR.Inference.Tests.csproj') --configuration $Configuration --no-build --filter 'Category=Model'

if (-not $SkipAndroid) {
    & (Join-Path $root 'eng\Publish-Android.ps1') -Configuration $Configuration
}
