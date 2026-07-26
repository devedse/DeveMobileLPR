[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\artifacts\models')
)

$ErrorActionPreference = 'Stop'
$Destination = [System.IO.Path]::GetFullPath($Destination)
[System.IO.Directory]::CreateDirectory($Destination) | Out-Null

$models = @(
    @{
        Name = 'yolo-v9-s-608-license-plates-end2end.onnx'
        Url = 'https://github.com/ankandrew/open-image-models/releases/download/assets/yolo-v9-s-608-license-plates-end2end.onnx'
        Sha256 = '2B878B38D9AA07B6DDC3EA75C4FFCB39869BC5C218E0A14002F60AB2F7B0BE9A'
        Length = 28612350
    },
    @{
        Name = 'cct_s_v2_global.onnx'
        Url = 'https://github.com/ankandrew/cnn-ocr-lp/releases/download/arg-plates/cct_s_v2_global.onnx'
        Sha256 = '384BBBD2CEA3EF54761D3DF70822EF3A349EE1A112AEAFDDBE0E3BA06BC6E47B'
        Length = 5262230
    }
)

foreach ($model in $models) {
    $target = Join-Path $Destination $model.Name
    $valid = Test-Path -LiteralPath $target
    if ($valid) {
        $item = Get-Item -LiteralPath $target
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash
        $valid = $item.Length -eq $model.Length -and $hash -eq $model.Sha256
    }

    if ($valid) {
        Write-Host "Verified $($model.Name)"
        continue
    }

    $temporary = "$target.download"
    try {
        Write-Host "Downloading $($model.Name)"
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $model.Url -OutFile $temporary
                break
            }
            catch {
                if ($attempt -eq 3) { throw }
                $delay = [Math]::Pow(2, $attempt)
                Write-Warning "Download attempt $attempt failed; retrying in $delay seconds."
                Start-Sleep -Seconds $delay
            }
        }
        $item = Get-Item -LiteralPath $temporary
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash
        if ($item.Length -ne $model.Length -or $hash -ne $model.Sha256) {
            throw "Integrity check failed for $($model.Name). Expected $($model.Sha256)/$($model.Length), got $hash/$($item.Length)."
        }

        Move-Item -Force -LiteralPath $temporary -Destination $target
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -Force -LiteralPath $temporary
        }
    }
}
