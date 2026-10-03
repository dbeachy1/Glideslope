param(
    [string] $OutputDirectory = '..\..\artifacts\packages\linux-x64-publish'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
$appProject = Join-Path $repoRoot 'src\Glideslope.App\Glideslope.App.csproj'
$versionOutput = & dotnet msbuild $appProject -getProperty:Version
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the authoritative Glideslope.App version.' }
$appVersion = ($versionOutput | Out-String).Trim()
if ($appVersion -notmatch '^[0-9A-Za-z.+~-]+$') { throw "Invalid app version from Glideslope.App.csproj: $appVersion" }
if (Test-Path -LiteralPath $outputRoot) { throw "Refusing to overwrite an existing frozen package input: $outputRoot" }

New-Item -ItemType Directory -Path $outputRoot | Out-Null
try {
    & dotnet publish $appProject --configuration Release --runtime linux-x64 `
        --self-contained true -p:PublishProfile=linux-x64 -p:PublishDir="$outputRoot\" `
        -p:DebugSymbols=false -p:DebugType=None `
        "-p:Version=$appVersion" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Linux app publish failed with exit code $LASTEXITCODE." }
    & (Join-Path $repoRoot 'packaging\verify-license-files.ps1') -SourceRoot $repoRoot -PackageRoot $outputRoot
    if ($LASTEXITCODE -ne 0) { throw 'Linux publish licensing-file verification failed.' }
    $publishFiles = @(Get-ChildItem -LiteralPath $outputRoot -File -Recurse)
    $manifest = $publishFiles | ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($outputRoot.TrimEnd('\').Length + 1).Replace('\','/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifestPath = "$outputRoot.files.json"
    $versionPath = "$outputRoot.version"
    if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $versionPath)) {
        throw 'Refusing to overwrite a Linux package input manifest or version marker.'
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Set-Content -LiteralPath $versionPath -Value $appVersion -NoNewline -Encoding ascii
    Write-Output "Built frozen linux-x64 publish input from Glideslope.App version $appVersion at $outputRoot."
    Write-Output "Manifest: $manifestPath"
    Write-Output "Version source record: $versionPath"
}
catch {
    # This path was required not to exist and was created by this invocation.
    if (Test-Path -LiteralPath $outputRoot) {
        $canonicalRoot = [IO.Path]::GetFullPath($outputRoot)
        $canonicalPackages = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\packages')).TrimEnd('\') + '\'
        if (-not $canonicalRoot.StartsWith($canonicalPackages, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected failed Linux publish directory: $canonicalRoot"
        }
        Remove-Item -LiteralPath $canonicalRoot -Recurse
        if (Test-Path -LiteralPath $canonicalRoot) { throw "Linux publish cleanup failed: $canonicalRoot" }
    }
    throw
}
