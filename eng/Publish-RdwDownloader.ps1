[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root 'artifacts\rdw-downloader'
$publishDirectory = Join-Path $artifactRoot 'publish'
$archive = Join-Path $artifactRoot "DeveMobileLPR.RdwDownloader-$Version.zip"

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

dotnet publish (Join-Path $root 'src\DeveMobileLPR.RdwDownloader\DeveMobileLPR.RdwDownloader.csproj') `
    --configuration $Configuration `
    --no-restore `
    --output $publishDirectory `
    -p:UseAppHost=false `
    -p:Version=$Version

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Published RDW downloader: $archive"
