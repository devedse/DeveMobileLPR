[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $IsMacOS) {
    throw 'The iPhone build requires macOS with Xcode and the .NET iOS workload.'
}

$project = Join-Path $PSScriptRoot '..\src\DeveMobileLPR.App\DeveMobileLPR.App.csproj'
dotnet build $project `
    --framework net10.0-ios `
    --configuration $Configuration `
    --runtime ios-arm64 `
    --no-restore `
    -p:EnableCodeSigning=false `
    -p:IosOnly=true `
    -p:ContinuousIntegrationBuild=true
