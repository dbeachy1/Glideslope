param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir,
    [Parameter(Mandatory = $true)]
    [string] $AppVersion
)

# The package starts the Windows GUI executable directly and keeps native libraries beside it. This proof checks
# that the publish contains no launcher, has the expected x64 GUI executable and libraries, and extracts nothing.

$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
& (Join-Path $repoRoot 'packaging\verify-license-files.ps1') -SourceRoot $repoRoot -PackageRoot $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Windows publish licensing-file verification failed.' }
$app = Join-Path $publishRoot 'Glideslope.App.exe'
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
    throw "The frozen Windows publish is missing Glideslope.App.exe: $publishRoot"
}

$scripts = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Where-Object { $_.Extension -in '.cmd', '.bat', '.ps1', '.vbs' })
if ($scripts.Count -ne 0) {
    throw "The frozen Windows publish still contains launcher scripts: $(($scripts | ForEach-Object Name) -join ', ')"
}

foreach ($native in 'av_libglesv2.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'e_sqlite3.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $native) -PathType Leaf)) {
        throw "Native library $native is not beside Glideslope.App.exe; the publish would extract it at run time."
    }
}

# PE header: e_lfanew at 0x3C, then "PE\0\0", the 20-byte file header, and the optional header whose
# Magic is 0x20B for PE32+ and whose Subsystem (offset 68) is 2 for a Windows GUI program.
$stream = [IO.File]::OpenRead($app)
try {
    $header = [byte[]]::new(4096)
    [void] $stream.Read($header, 0, $header.Length)
}
finally {
    $stream.Dispose()
}
$peOffset = [BitConverter]::ToInt32($header, 0x3C)
if ([BitConverter]::ToUInt32($header, $peOffset) -ne 0x00004550) { throw 'Glideslope.App.exe has no PE signature.' }
$optionalHeader = $peOffset + 24
$magic = [BitConverter]::ToUInt16($header, $optionalHeader)
if ($magic -ne 0x20B) { throw ('Glideslope.App.exe is not a PE32+ (x64) image; magic 0x{0:X}.' -f $magic) }
$subsystem = [BitConverter]::ToUInt16($header, $optionalHeader + 68)
if ($subsystem -ne 2) { throw "Glideslope.App.exe subsystem is $subsystem; expected 2 (Windows GUI, no console window)." }

$productVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
if ($null -eq $productVersion -or $productVersion.Split('+')[0] -ne $AppVersion) {
    throw "Glideslope.App.exe product version is '$productVersion'; expected $AppVersion."
}

# Run the frozen exe for real with its extraction folder pointed at an empty directory. The .NET host
# extracts, if at all, before Main; the headless command then stops at "not an installed copy" (exit 2),
# because a raw publish has no installer marker. The folder must still be empty afterwards.
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "Glideslope.Packaging-WindowsLauncher-$([guid]::NewGuid().ToString('N'))"
$ownerMarker = [guid]::NewGuid().ToString('N')
$markerPath = Join-Path $tempRoot '.owner'
$savedLocalAppData = $env:LOCALAPPDATA
$savedProofRoot = $env:GLIDESLOPE_PROOF_ROOT
$savedBundleCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$process = $null
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Set-Content -LiteralPath $markerPath -Value $ownerMarker -NoNewline
    $proofRoot = Join-Path $tempRoot 'proof-root'
    $localAppData = Join-Path $tempRoot 'isolated-local-app-data'
    $bundleCache = Join-Path $tempRoot 'bundle-cache-must-stay-empty'
    New-Item -ItemType Directory -Path $proofRoot, $localAppData, $bundleCache | Out-Null
    $env:LOCALAPPDATA = $localAppData
    $env:GLIDESLOPE_PROOF_ROOT = $proofRoot
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleCache

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $app
    $startInfo.WorkingDirectory = $tempRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    # Arguments as one quoted string (not ArgumentList) so this proof also runs under Windows PowerShell 5.1.
    $startInfo.Arguments = (@('--startup-registration', 'disable', '--proof-root', $proofRoot) | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'Could not start the frozen exe for the extraction proof.' }
    $ownedCreationTime = $process.StartTime.ToUniversalTime()
    if (-not $process.WaitForExit(60000)) {
        $running = Get-Process -Id $process.Id -ErrorAction Stop
        if ($running.StartTime.ToUniversalTime() -ne $ownedCreationTime -or
            [IO.Path]::GetFullPath($running.Path) -ne [IO.Path]::GetFullPath($app)) {
            throw 'The timed-out extraction-proof process identity changed; refusing to stop it.'
        }
        $process.Kill()
        if (-not $process.WaitForExit(10000)) { throw 'Owned extraction-proof process did not exit after termination.' }
        throw 'The extraction proof exceeded 60 seconds.'
    }
    if ($process.ExitCode -ne 2) { throw "The raw publish's headless command returned $($process.ExitCode); expected 2 (not an installed copy)." }
    $extracted = @(Get-ChildItem -LiteralPath $bundleCache -Recurse -Force)
    if ($extracted.Count -ne 0) {
        throw "The frozen exe extracted $($extracted.Count) bundle entries at run time; native libraries must load from beside the exe."
    }
    Write-Output 'Windows publish proof passed: no launcher script, GUI-subsystem x64 exe of the expected version, native libraries beside it, nothing extracted at run time.'
}
finally {
    $env:LOCALAPPDATA = $savedLocalAppData
    $env:GLIDESLOPE_PROOF_ROOT = $savedProofRoot
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $savedBundleCache
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $current = Get-Process -Id $process.Id -ErrorAction Stop
            if ($current.StartTime.ToUniversalTime() -eq $process.StartTime.ToUniversalTime() -and
                [IO.Path]::GetFullPath($current.Path) -eq [IO.Path]::GetFullPath($app)) {
                $process.Kill()
                if (-not $process.WaitForExit(10000)) { throw 'Owned extraction-proof process survived cleanup.' }
            }
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $canonicalTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $canonicalRoot = [IO.Path]::GetFullPath($tempRoot)
        if (-not $canonicalRoot.StartsWith($canonicalTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $canonicalRoot).StartsWith('Glideslope.Packaging-WindowsLauncher-', [StringComparison]::Ordinal) -or
            -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
            (Get-Content -LiteralPath $markerPath -Raw) -ne $ownerMarker) {
            throw "Refusing to remove a Windows launcher temp root without its verified ownership marker: $canonicalRoot"
        }
        Remove-Item -LiteralPath $canonicalRoot -Recurse
        if (Test-Path -LiteralPath $canonicalRoot) { throw "Windows launcher temp cleanup failed: $canonicalRoot" }
    }
}
