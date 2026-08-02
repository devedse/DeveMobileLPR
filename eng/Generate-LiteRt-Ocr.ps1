[CmdletBinding()]
param(
    [string]$ModelDirectory = (Join-Path $PSScriptRoot '..\artifacts\models'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$converterImage = 'pinto0309/onnx2tf@sha256:a0ebd140357df75d5a3c7cd06d1fcf97500898c858f081584516f8232da3bbf3'
$sourceName = 'cct_s_v2_global.onnx'
$targetName = 'cct_s_v2_global_float32.tflite'
$expectedSourceHash = '384BBBD2CEA3EF54761D3DF70822EF3A349EE1A112AEAFDDBE0E3BA06BC6E47B'
$expectedTargetHash = '215049B9D372B7DBB2BA392E85E0E1079681085F66FE92A9884B00CC6681F25C'
$expectedTargetLength = 5177440

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$modelDirectory = [System.IO.Path]::GetFullPath($ModelDirectory)
$validator = Join-Path $root 'eng\model-tools\validate_litert_ocr.py'
$source = Join-Path $modelDirectory $sourceName
$target = Join-Path $modelDirectory $targetName
[System.IO.Directory]::CreateDirectory($modelDirectory) | Out-Null

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "The source OCR model is missing: $source. Run eng/Download-Models.ps1 first."
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash -ne $expectedSourceHash) {
    throw "The source OCR model failed its SHA-256 integrity check: $source"
}

function Test-GeneratedModel {
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $target
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash
    return $item.Length -eq $expectedTargetLength -and $hash -eq $expectedTargetHash
}

if (-not $Force -and (Test-GeneratedModel)) {
    Write-Host "Verified $targetName"
    return
}

$docker = Get-Command docker -ErrorAction SilentlyContinue
if ($null -eq $docker) {
    throw 'Docker is required to generate the Android LiteRT OCR model. Install Docker or generate the model in CI.'
}

$generationDirectory = Join-Path $modelDirectory 'litert-ocr-generation'
if (Test-Path -LiteralPath $generationDirectory) {
    $resolvedGenerationDirectory = [System.IO.Path]::GetFullPath($generationDirectory)
    if (-not $resolvedGenerationDirectory.StartsWith($modelDirectory + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a generation directory outside the model directory: $resolvedGenerationDirectory"
    }
    Remove-Item -LiteralPath $resolvedGenerationDirectory -Recurse -Force
}
$converted = Join-Path $generationDirectory 'converted'
[System.IO.Directory]::CreateDirectory($converted) | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $generationDirectory $sourceName)

try {
    & $docker.Source run --rm `
        --mount "type=bind,source=$generationDirectory,target=/work" `
        $converterImage `
        onnx2tf `
            -i "/work/$sourceName" `
            -o /work/converted `
            -coion `
            -kt input `
            -ewo `
            -ens 5 `
            -efot `
            -v info

    $accuracyReportName = 'cct_s_v2_global_accuracy_report.json'
    & $docker.Source run --rm `
        --mount "type=bind,source=$validator,target=/tools/validate_litert_ocr.py,readonly" `
        --mount "type=bind,source=$converted,target=/output,readonly" `
        $converterImage `
        python /tools/validate_litert_ocr.py `
            --model "/output/$targetName" `
            --accuracy-report "/output/$accuracyReportName"

    Move-Item -LiteralPath (Join-Path $converted $targetName) -Destination $target -Force
    if (-not (Test-GeneratedModel)) {
        $actual = Get-Item -LiteralPath $target
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash
        throw "Generated OCR model did not match the pinned result. Expected $expectedTargetHash/$expectedTargetLength, got $actualHash/$($actual.Length)."
    }
}
finally {
    if (Test-Path -LiteralPath $generationDirectory) {
        Remove-Item -LiteralPath $generationDirectory -Recurse -Force
    }
}

Write-Host "Generated and verified $target"