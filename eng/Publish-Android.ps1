[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [ValidateRange(1, 2100000000)]
    [int]$ApplicationVersion,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ApplicationDisplayVersion,
    [string]$Keystore,
    [string]$KeystorePassword,
    [string]$KeyAlias,
    [string]$KeyPassword
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\DeveMobileLPR.Android\DeveMobileLPR.Android.csproj'
$output = Join-Path $root 'artifacts\android'
[System.IO.Directory]::CreateDirectory($output) | Out-Null
Get-ChildItem -LiteralPath $output -Filter '*.apk' -File -ErrorAction SilentlyContinue | Remove-Item -Force

$arguments = @('publish', $project, '--configuration', $Configuration, '--no-restore', '-p:AndroidPackageFormats=apk', "-p:PublishDir=$output\")
if ($PSBoundParameters.ContainsKey('Version')) {
    $arguments += "-p:Version=$Version"
}
if ($PSBoundParameters.ContainsKey('ApplicationVersion')) {
    $arguments += "-p:ApplicationVersion=$ApplicationVersion"
}
if ($PSBoundParameters.ContainsKey('ApplicationDisplayVersion')) {
    $arguments += "-p:ApplicationDisplayVersion=$ApplicationDisplayVersion"
}
if (-not [string]::IsNullOrWhiteSpace($Keystore)) {
    if (-not (Test-Path -LiteralPath $Keystore)) { throw "Keystore does not exist: $Keystore" }
    $arguments += @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$Keystore",
        "-p:AndroidSigningStorePass=$KeystorePassword",
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        "-p:AndroidSigningKeyPass=$KeyPassword"
    )
}

dotnet @arguments

$signedPackages = @(Get-ChildItem -LiteralPath $output -Filter '*-Signed.apk' -File)
if ($signedPackages.Count -ne 1) {
    throw "Expected one signed APK in $output, found $($signedPackages.Count)."
}

Get-ChildItem -LiteralPath $output -Filter '*.apk' -File |
    Where-Object { $_.Name -notlike '*-Signed.apk' } |
    Remove-Item -Force
Write-Host "Published $($signedPackages[0].FullName)"
