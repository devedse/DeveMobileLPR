[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
if ([string]::IsNullOrWhiteSpace($root)) {
    throw 'Run this script inside a Git working tree.'
}

function Get-UnexpectedEolPaths {
    foreach ($entry in @(git ls-files --eol)) {
        $separator = $entry.IndexOf("`t")
        if ($separator -lt 0) {
            continue
        }

        $metadata = $entry.Substring(0, $separator)
        $actualMatch = [regex]::Match($metadata, 'w/(?<value>\S+)')
        $expectedMatch = [regex]::Match($metadata, 'eol=(?<value>lf|crlf)')
        if ($actualMatch.Success -and
            $expectedMatch.Success -and
            $actualMatch.Groups['value'].Value -ne 'none' -and
            $actualMatch.Groups['value'].Value -ne $expectedMatch.Groups['value'].Value) {
            $entry.Substring($separator + 1)
        }
    }
}

function Set-FileEol([string] $Path, [string] $Eol) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasUtf8Bom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    $offset = if ($hasUtf8Bom) { 3 } else { 0 }
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $content = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    $content = $content.Replace("`r`n", "`n").Replace("`r", "`n")
    if ($Eol -eq 'crlf') {
        $content = $content.Replace("`n", "`r`n")
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $content,
        [System.Text.UTF8Encoding]::new($hasUtf8Bom))
}

Push-Location $root
try {
    if (-not (Test-Path -LiteralPath '.gitattributes' -PathType Leaf)) {
        throw 'The repository does not define .gitattributes.'
    }

    $unstagedFiles = @(git diff --name-only)
    if ($unstagedFiles.Count -gt 0) {
        throw "Commit or stage tracked changes before normalization: $($unstagedFiles -join ', ')"
    }

    git add --renormalize .
    $pathsToRewrite = @(Get-UnexpectedEolPaths)
    foreach ($path in $pathsToRewrite) {
        $attributes = git check-attr eol -- $path
        $expectedEol = ([regex]::Match($attributes, 'eol: (?<value>lf|crlf)$')).Groups['value'].Value
        Set-FileEol -Path $path -Eol $expectedEol
    }

    $remainingPaths = @(Get-UnexpectedEolPaths)
    if ($remainingPaths.Count -gt 0) {
        throw "Line-ending normalization failed: $($remainingPaths -join ', ')"
    }

    git diff --cached --check
    Write-Host "Normalized the index and working tree ($($pathsToRewrite.Count) files rewritten)."
}
finally {
    Pop-Location
}
