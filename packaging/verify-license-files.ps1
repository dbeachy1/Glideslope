param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot
)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$requiredFiles = @('LICENSE', 'NOTICE', 'TRADEMARKS.md', 'THIRD-PARTY-NOTICES.txt')
$requiredNoticeSections = @(
    'Avalonia 12.1.3',
    'ANGLE Windows native package',
    'HarfBuzzSharp, HarfBuzzSharp.NativeAssets',
    'SkiaSharp, SkiaSharp.NativeAssets',
    'MicroCom.Runtime 0.11.6',
    'Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 10.0.12',
    'SQLitePCLRaw.bundle_e_sqlite3',
    'SQLite e_sqlite3 native library',
    'Tmds.DBus.Protocol 0.94.1',
    '.NET self-contained runtime 10.0.12 for win-x64 and linux-x64',
    'License notice for ASP.NET'
)

foreach ($name in $requiredFiles) {
    $source = Join-Path $sourceRoot $name
    $published = Join-Path $packageRoot $name
    foreach ($path in @($source, $published)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required licensing file is missing: $path"
        }
        if ((Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Required licensing file is empty: $path"
        }
    }

    $sourceInfo = Get-Item -LiteralPath $source
    $publishedInfo = Get-Item -LiteralPath $published
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash
    if ($sourceInfo.Length -ne $publishedInfo.Length -or $sourceHash -ne $publishedHash) {
        throw "Published licensing file differs from its repository source: $name"
    }
}

$notices = [IO.File]::ReadAllText((Join-Path $packageRoot 'THIRD-PARTY-NOTICES.txt'))
foreach ($section in $requiredNoticeSections) {
    if (-not $notices.Contains($section)) {
        throw "Third-party notices are missing required coverage: $section"
    }
}

Write-Output "Licensing files are present, non-empty, content-verified, and include required dependency sections: $packageRoot"
