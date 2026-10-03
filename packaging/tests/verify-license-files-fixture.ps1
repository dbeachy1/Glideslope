$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$checker = Join-Path $repoRoot 'packaging\verify-license-files.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "Glideslope.LicenseFixture-$([guid]::NewGuid().ToString('N'))"
$ownerMarker = [guid]::NewGuid().ToString('N')
$markerPath = Join-Path $tempRoot '.owner'

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Set-Content -LiteralPath $markerPath -Value $ownerMarker -NoNewline
    foreach ($name in @('LICENSE', 'NOTICE', 'TRADEMARKS.md', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $tempRoot
    }

    & $checker -SourceRoot $repoRoot -PackageRoot $tempRoot

    $alteredFile = Join-Path $tempRoot 'NOTICE'
    [IO.File]::AppendAllText($alteredFile, 'fixture change')
    $changedRejected = $false
    try {
        & $checker -SourceRoot $repoRoot -PackageRoot $tempRoot
    }
    catch {
        if ($_.Exception.Message -notmatch 'differs from its repository source') { throw }
        $changedRejected = $true
    }
    if (-not $changedRejected) { throw 'The licensing-file checker accepted a fixture whose NOTICE bytes changed.' }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'NOTICE') -Destination $alteredFile -Force
    Remove-Item -LiteralPath (Join-Path $tempRoot 'NOTICE')
    $missingRejected = $false
    try {
        & $checker -SourceRoot $repoRoot -PackageRoot $tempRoot
    }
    catch {
        if ($_.Exception.Message -notmatch 'Required licensing file is missing') { throw }
        $missingRejected = $true
    }
    if (-not $missingRejected) { throw 'The licensing-file checker accepted a fixture with NOTICE missing.' }

    Write-Output 'Licensing-file fixture passed: byte-identical files accepted; changed and missing files rejected.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        $canonicalTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $canonicalRoot = [IO.Path]::GetFullPath($tempRoot)
        if (-not $canonicalRoot.StartsWith($canonicalTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $canonicalRoot).StartsWith('Glideslope.LicenseFixture-', [StringComparison]::Ordinal) -or
            -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
            (Get-Content -LiteralPath $markerPath -Raw) -ne $ownerMarker) {
            throw "Refusing to remove an unexpected licensing fixture path: $canonicalRoot"
        }
        Remove-Item -LiteralPath $canonicalRoot -Recurse
        if (Test-Path -LiteralPath $canonicalRoot) { throw "Licensing fixture cleanup failed: $canonicalRoot" }
    }
}
