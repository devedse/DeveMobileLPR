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
$projectOutput = Join-Path $root "src\DeveMobileLPR.Android\bin\$Configuration"
$output = Join-Path $root 'artifacts\android'
[System.IO.Directory]::CreateDirectory($output) | Out-Null
Get-ChildItem -LiteralPath $output -Filter '*.apk' -File -ErrorAction SilentlyContinue | Remove-Item -Force

# A solution build creates a debug-signed APK before this script runs in CI. The Android signing
# target is incremental and does not consider a changed keystore sufficient to invalidate that
# existing output, so remove only the generated signed package and force it to be signed again.
Get-ChildItem -LiteralPath $projectOutput -Filter '*-Signed.apk' -File -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force

$arguments = @('publish', $project, '--framework', 'net10.0-android36.0', '--configuration', $Configuration, '--no-restore', '-p:AndroidPackageFormats=apk', "-p:PublishDir=$output\")
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
    if ([string]::IsNullOrWhiteSpace($KeystorePassword)) { throw 'KeystorePassword is required when Keystore is set.' }
    if ([string]::IsNullOrWhiteSpace($KeyAlias)) { throw 'KeyAlias is required when Keystore is set.' }
    if ([string]::IsNullOrWhiteSpace($KeyPassword)) { throw 'KeyPassword is required when Keystore is set.' }

    $env:DEVEMOBILELPR_ANDROID_SIGNING_STORE_PASSWORD = $KeystorePassword
    $env:DEVEMOBILELPR_ANDROID_SIGNING_KEY_PASSWORD = $KeyPassword
    $arguments += @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$Keystore",
        '-p:AndroidSigningStorePass=env:DEVEMOBILELPR_ANDROID_SIGNING_STORE_PASSWORD',
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        '-p:AndroidSigningKeyPass=env:DEVEMOBILELPR_ANDROID_SIGNING_KEY_PASSWORD'
    )
}

try {
    dotnet @arguments
}
finally {
    Remove-Item Env:DEVEMOBILELPR_ANDROID_SIGNING_STORE_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:DEVEMOBILELPR_ANDROID_SIGNING_KEY_PASSWORD -ErrorAction SilentlyContinue
}

$signedPackages = @(Get-ChildItem -LiteralPath $output -Filter '*-Signed.apk' -File)
if ($signedPackages.Count -ne 1) {
    throw "Expected one signed APK in $output, found $($signedPackages.Count)."
}

Get-ChildItem -LiteralPath $output -Filter '*.apk' -File |
    Where-Object { $_.Name -notlike '*-Signed.apk' } |
    Remove-Item -Force

if (-not [string]::IsNullOrWhiteSpace($Keystore)) {
    $keytool = Get-Command 'keytool' -ErrorAction SilentlyContinue
    if ($null -eq $keytool) {
        throw 'keytool is required to verify the release signing certificate.'
    }

    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $sdkRoots = @(
        $env:ANDROID_HOME,
        $env:ANDROID_SDK_ROOT,
        $(if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }),
        $(if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) { Join-Path $programFilesX86 'Android\android-sdk' })
    ) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container) } |
        Select-Object -Unique
    $apksigner = $sdkRoots |
        ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $_ 'build-tools') -Filter 'apksigner.bat' -File -Recurse -ErrorAction SilentlyContinue } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $apksigner) {
        throw 'apksigner.bat is required to verify the release-signed APK.'
    }

    $keystoreDetails = & $keytool.Source -list -v -keystore $Keystore -alias $KeyAlias -storepass $KeystorePassword 2>&1
    $expectedMatch = [regex]::Match(($keystoreDetails -join "`n"), 'SHA256:\s*([0-9A-Fa-f:]{64,})')
    if (-not $expectedMatch.Success) {
        throw 'Could not read the SHA-256 certificate fingerprint from the keystore.'
    }

    $apkDetails = & $apksigner.FullName verify --verbose --print-certs $signedPackages[0].FullName 2>&1
    $actualMatch = [regex]::Match(($apkDetails -join "`n"), 'certificate SHA-256 digest:\s*([0-9A-Fa-f]{64})')
    if (-not $actualMatch.Success) {
        throw 'Could not read the SHA-256 signing certificate fingerprint from the APK.'
    }

    $expectedFingerprint = ($expectedMatch.Groups[1].Value -replace ':', '').ToLowerInvariant()
    $actualFingerprint = $actualMatch.Groups[1].Value.ToLowerInvariant()
    if ($actualFingerprint -ne $expectedFingerprint) {
        throw "APK signing certificate mismatch. Expected $expectedFingerprint but found $actualFingerprint."
    }

    Write-Host "Verified release signing certificate SHA-256: $actualFingerprint"
}

Write-Host "Published $($signedPackages[0].FullName)"
