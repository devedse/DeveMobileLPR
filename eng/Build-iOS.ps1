[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '0.1.0',
    [ValidateRange(1, 2147483647)]
    [int]$ApplicationVersion = 1
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
    -p:ValidateXcodeVersion=false `
    -p:IosOnly=true `
    -p:Version=$Version `
    -p:ApplicationVersion=$ApplicationVersion `
    -p:ApplicationDisplayVersion=$Version `
    -p:ContinuousIntegrationBuild=true
