[CmdletBinding()]
param(
    [string]$ModelDirectory = (Join-Path $PSScriptRoot '..\artifacts\models'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$converterImage = 'pinto0309/onnx2tf@sha256:a0ebd140357df75d5a3c7cd06d1fcf97500898c858f081584516f8232da3bbf3'
$sourceName = 'yolo-v9-s-608-license-plates-end2end.onnx'
$rawName = 'yolo-v9-s-608-license-plates-raw.onnx'
$targetName = 'yolo-v9-s-608-license-plates-raw_float32.tflite'
$expectedSourceHash = '2B878B38D9AA07B6DDC3EA75C4FFCB39869BC5C218E0A14002F60AB2F7B0BE9A'
$expectedRawHash = '8886A067DD514404E99FDF1CFC642827303A4700E3D9FFE829DADC446BB94BCE'
$expectedRawLength = 28608718
$expectedTargetHash = 'EE20A2F2DAAD51525A449E2A7E388965E4F9DEC5F39CB8D0348C21232FFAA1E2'
$expectedTargetLength = 28561524

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$modelDirectory = [System.IO.Path]::GetFullPath($ModelDirectory)
$tool = Join-Path $root 'eng\model-tools\prepare_litert_detector.py'
$source = Join-Path $modelDirectory $sourceName
$rawTarget = Join-Path $modelDirectory $rawName
$target = Join-Path $modelDirectory $targetName
[System.IO.Directory]::CreateDirectory($modelDirectory) | Out-Null

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "The source detector is missing: $source. Run eng/Download-Models.ps1 first."
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash -ne $expectedSourceHash) {
    throw "The source detector failed its SHA-256 integrity check: $source"
}

function Test-GeneratedModel {
    if (-not (Test-Path -LiteralPath $rawTarget -PathType Leaf) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) { return $false }
    $rawItem = Get-Item -LiteralPath $rawTarget
    $rawHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $rawTarget).Hash
    $item = Get-Item -LiteralPath $target
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash
    return $rawItem.Length -eq $expectedRawLength `
        -and $rawHash -eq $expectedRawHash `
        -and $item.Length -eq $expectedTargetLength `
        -and $hash -eq $expectedTargetHash
}

if (-not $Force -and (Test-GeneratedModel)) {
    Write-Host "Verified $targetName"
    return
}

$docker = Get-Command docker -ErrorAction SilentlyContinue
if ($null -eq $docker) {
    throw 'Docker is required to generate the Android LiteRT detector. Install Docker or generate the model in CI.'
}

$generationDirectory = Join-Path $modelDirectory 'litert-generation'
if (Test-Path -LiteralPath $generationDirectory) {
    $resolvedGenerationDirectory = [System.IO.Path]::GetFullPath($generationDirectory)
    if (-not $resolvedGenerationDirectory.StartsWith($modelDirectory + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a generation directory outside the model directory: $resolvedGenerationDirectory"
    }
    Remove-Item -LiteralPath $resolvedGenerationDirectory -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($generationDirectory) | Out-Null

$raw = Join-Path $generationDirectory $rawName
$converted = Join-Path $generationDirectory 'converted'
[System.IO.Directory]::CreateDirectory($converted) | Out-Null

try {
    & $docker.Source run --rm `
        --mount "type=bind,source=$source,target=/input/source.onnx,readonly" `
        --mount "type=bind,source=$tool,target=/tools/prepare_litert_detector.py,readonly" `
        --mount "type=bind,source=$generationDirectory,target=/work" `
        $converterImage `
        python /tools/prepare_litert_detector.py extract `
            --source /input/source.onnx `
            --destination "/work/$rawName"

    & $docker.Source run --rm `
        --mount "type=bind,source=$generationDirectory,target=/work" `
        $converterImage `
        onnx2tf `
            -i "/work/$rawName" `
            -o /work/converted `
            -coion `
            -ewo `
            -ens 5 `
            -efot `
            -v info

    $generatedName = [System.IO.Path]::GetFileNameWithoutExtension($rawName) + '_float32.tflite'
    $generated = Join-Path $converted $generatedName
    $accuracyReportName = [System.IO.Path]::GetFileNameWithoutExtension($rawName) + '_accuracy_report.json'
    $accuracyReport = Join-Path $converted $accuracyReportName

    & $docker.Source run --rm `
        --mount "type=bind,source=$tool,target=/tools/prepare_litert_detector.py,readonly" `
        --mount "type=bind,source=$converted,target=/output,readonly" `
        $converterImage `
        python /tools/prepare_litert_detector.py validate `
            --model "/output/$generatedName" `
            --accuracy-report "/output/$accuracyReportName"

    Move-Item -LiteralPath $generated -Destination $target -Force
    Copy-Item -LiteralPath $raw -Destination $rawTarget -Force
    if (-not (Test-GeneratedModel)) {
        $actualRaw = Get-Item -LiteralPath $rawTarget
        $actualRawHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $rawTarget).Hash
        $actual = Get-Item -LiteralPath $target
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash
        throw "Generated models did not match the pinned results. Raw: expected $expectedRawHash/$expectedRawLength, got $actualRawHash/$($actualRaw.Length). LiteRT: expected $expectedTargetHash/$expectedTargetLength, got $actualHash/$($actual.Length)."
    }
}
finally {
    if (Test-Path -LiteralPath $generationDirectory) {
        Remove-Item -LiteralPath $generationDirectory -Recurse -Force
    }
}

Write-Host "Generated and verified $target"
