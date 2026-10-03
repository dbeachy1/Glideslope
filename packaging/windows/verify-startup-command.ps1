param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir
)

# Start at sign-in on Windows is the per-user Run value
#   HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Glideslope = "<installed Glideslope.App.exe>" --autostart
# written by the app's headless --startup-registration command. The Startup-folder .cmd entries it
# replaces (Glideslope.startup.cmd, and the pre-rename GlidePath.startup.cmd) are moved to the Run value
# and removed only when the app owns them. This proof runs the frozen exe from a simulated installed
# folder (the publish plus the installer's marker) whose path has a space, a non-ASCII letter and a
# percent sign, the characters that broke the batch-file entry, against an isolated proof root where
# the app keeps the Run value as a file and never touches the registry or the real Startup folder.

$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$publishedApp = Join-Path $publishRoot 'Glideslope.App.exe'
$markerSource = Join-Path $PSScriptRoot 'glideslope-installed.marker'
if (-not (Test-Path -LiteralPath $publishedApp -PathType Leaf)) { throw "Frozen Windows publish is missing Glideslope.App.exe: $publishRoot" }
if (-not (Test-Path -LiteralPath $markerSource -PathType Leaf)) { throw "The installed-copy marker source is missing: $markerSource" }

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "Glideslope.Packaging-WindowsStartup-$([guid]::NewGuid().ToString('N'))"
$ownerMarker = [guid]::NewGuid().ToString('N')
$markerPath = Join-Path $tempRoot '.owner'
$savedLocalAppData = $env:LOCALAPPDATA
$savedProofRoot = $env:GLIDESLOPE_PROOF_ROOT
$savedBundleCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$process = $null
$app = $null
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Set-Content -LiteralPath $markerPath -Value $ownerMarker -NoNewline
    $proofRoot = Join-Path $tempRoot 'proof-root'
    $localAppData = Join-Path $tempRoot 'isolated-local-app-data'
    $bundleCache = Join-Path $tempRoot 'bundle-cache-must-stay-empty'
    $installDir = Join-Path $tempRoot ('Programs Zo' + [char]0x00EB + ' 100% Glideslope')
    New-Item -ItemType Directory -Path $proofRoot, $localAppData, $bundleCache, $installDir | Out-Null
    Get-ChildItem -LiteralPath $publishRoot | Copy-Item -Destination $installDir -Recurse
    Copy-Item -LiteralPath $markerSource -Destination (Join-Path $installDir 'glideslope-installed.marker')
    $app = Join-Path $installDir 'Glideslope.App.exe'
    $env:LOCALAPPDATA = $localAppData
    $env:GLIDESLOPE_PROOF_ROOT = $proofRoot
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleCache

    $resolvedPath = & dotnet run --project (Join-Path $PSScriptRoot '..\tests\startup-path-probe\StartupPathProbe.csproj') `
        --configuration Release --artifacts-path (Join-Path $tempRoot 'probe-build') -- --proof-root $proofRoot
    if ($LASTEXITCODE -ne 0) { throw 'Non-writing startup destination proof failed.' }
    $startupDir = Join-Path $proofRoot 'startup'
    $runValue = Join-Path $startupDir 'HKCU-Run\Glideslope.txt'
    $approval = Join-Path $startupDir 'HKCU-StartupApproved-Run\Glideslope.bin'
    if (($resolvedPath | Out-String).Trim() -ne $runValue) {
        throw "Startup service resolved outside the owned proof root. Expected '$runValue'; observed '$resolvedPath'."
    }

    $utf8 = [Text.UTF8Encoding]::new($false)
    $expectedRunValue = '"' + $app + '" --autostart'

    function Read-RunValue {
        if (Test-Path -LiteralPath $runValue -PathType Leaf) { return [IO.File]::ReadAllText($runValue, $utf8) }
        return $null
    }

    # The exact legacy four-line shape, for the current or pre-rename product name.
    function Write-OwnedCmd([string] $Name, [string] $Product, [string] $Target) {
        New-Item -ItemType Directory -Path $startupDir -Force | Out-Null
        $content = "@echo off`r`nrem X-$Product-Managed=true`r`nset `"DOTNET_BUNDLE_EXTRACT_BASE_DIR=%LOCALAPPDATA%\$Product\cache\bundle`"`r`n`"$Target`" --autostart`r`n"
        [IO.File]::WriteAllText((Join-Path $startupDir $Name), $content, $utf8)
    }

    function Invoke-IsolatedApp([string] $Executable, [string[]] $Arguments, [int] $ExpectedExitCode) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $Executable
        $startInfo.WorkingDirectory = $env:TEMP
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardError = $true
        # One quoted string (not ArgumentList) so this proof also runs under Windows PowerShell 5.1. The
        # paths here contain spaces, a non-ASCII letter and a percent sign, never a quote.
        $startInfo.Arguments = ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
        }) -join ' '
        $script:process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $script:process) { throw 'Could not start the frozen startup-command proof process.' }
        $ownedPid = $script:process.Id
        $ownedCreationTime = $script:process.StartTime.ToUniversalTime()
        if (-not $script:process.WaitForExit(60000)) {
            $running = Get-Process -Id $ownedPid -ErrorAction Stop
            if ($running.StartTime.ToUniversalTime() -ne $ownedCreationTime -or
                [IO.Path]::GetFullPath($running.Path) -ne [IO.Path]::GetFullPath($Executable)) {
                throw 'Timed-out startup-command process identity changed; refusing to stop it.'
            }
            $script:process.Kill()
            if (-not $script:process.WaitForExit(10000)) { throw 'Owned startup-command process did not exit after termination.' }
            throw 'Frozen startup-command proof exceeded 60 seconds.'
        }
        $actual = $script:process.ExitCode
        $stderr = $script:process.StandardError.ReadToEnd()
        $script:process.Dispose()
        $script:process = $null
        if ($actual -ne $ExpectedExitCode) { throw "Startup command $($Arguments -join ' ') returned $actual; expected $ExpectedExitCode. Diagnostics: $stderr" }
        if ($stderr) { Write-Output $stderr.Trim() }
    }

    $enable = @('--startup-registration', 'enable', '--proof-root', $proofRoot)
    $disable = @('--startup-registration', 'disable', '--proof-root', $proofRoot)

    # Enable from an installed copy migrates both owned legacy batch entries (here stale because the
    # retired launcher and GlidePath folder are gone) to the exact Run value, then removes them.
    Write-OwnedCmd 'Glideslope.startup.cmd' 'Glideslope' (Join-Path $installDir 'glideslope-launcher.cmd')
    Write-OwnedCmd 'GlidePath.startup.cmd' 'GlidePath' (Join-Path $tempRoot 'old GlidePath\glideslope-launcher.cmd')
    Invoke-IsolatedApp $app $enable 0
    if ((Read-RunValue) -cne $expectedRunValue) {
        throw "The Run value is not the quoted installed exe with --autostart, byte for byte. Observed: $(Read-RunValue)"
    }
    $leftCmd = @(Get-ChildItem -LiteralPath $startupDir -File -Filter '*.cmd')
    if ($leftCmd.Count -ne 0) { throw "Enable left Startup-folder batch files behind: $(($leftCmd | ForEach-Object Name) -join ', ')" }

    # 2. An entry the user turned off in Windows (Task Manager, Settings > Apps > Startup) is turned back
    #    on by an explicit enable, which removes only this app's approval value.
    New-Item -ItemType Directory -Path (Split-Path -Parent $approval) -Force | Out-Null
    [IO.File]::WriteAllBytes($approval, [byte[]](3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))
    Invoke-IsolatedApp $app $enable 0
    if (Test-Path -LiteralPath $approval) { throw 'Enable did not clear the Windows startup-approval "off" value of its own entry.' }
    if ((Read-RunValue) -cne $expectedRunValue) { throw 'Enable over an approved-off entry changed the Run value.' }

    # Disable removes the Run value and all owned batch entries, including the legacy name, so a later launch
    # cannot restore start-at-sign-in.
    Write-OwnedCmd 'GlidePath.startup.cmd' 'GlidePath' (Join-Path $tempRoot 'old GlidePath\glideslope-launcher.cmd')
    Invoke-IsolatedApp $app $disable 0
    if (Test-Path -LiteralPath $runValue) { throw 'Disable did not remove its owned Run value.' }
    if (Test-Path -LiteralPath (Join-Path $startupDir 'GlidePath.startup.cmd')) {
        throw 'Disable left the owned GlidePath startup entry, which the next launch would turn back on.'
    }

    # 4. A Run value named Glideslope that this app did not write is never changed.
    New-Item -ItemType Directory -Path (Split-Path -Parent $runValue) -Force | Out-Null
    [IO.File]::WriteAllText($runValue, 'foreign command', $utf8)
    Invoke-IsolatedApp $app $disable 1
    Invoke-IsolatedApp $app $enable 1
    if ((Read-RunValue) -cne 'foreign command') { throw 'A foreign Run value was changed.' }
    Remove-Item -LiteralPath $runValue

    # 5. A foreign file at a retired batch-entry name is never changed.
    $foreignCmd = Join-Path $startupDir 'Glideslope.startup.cmd'
    [IO.File]::WriteAllText($foreignCmd, "foreign user content`r`n", $utf8)
    Invoke-IsolatedApp $app $enable 0
    Invoke-IsolatedApp $app $disable 0
    if ([IO.File]::ReadAllText($foreignCmd, $utf8) -cne "foreign user content`r`n") { throw 'A foreign Startup-folder file changed.' }
    Remove-Item -LiteralPath $foreignCmd

    # 6. A copy without the installer's marker is not an installed copy: exit 2, nothing written.
    $missingMarkerDir = Join-Path $tempRoot 'missing-marker'
    New-Item -ItemType Directory -Path $missingMarkerDir | Out-Null
    $missingApp = Join-Path $missingMarkerDir 'Glideslope.App.exe'
    Copy-Item -LiteralPath $publishedApp -Destination $missingApp
    $missingProof = Join-Path $proofRoot 'missing-marker-proof'
    Invoke-IsolatedApp $missingApp @('--startup-registration', 'enable', '--proof-root', $missingProof) 2
    if (Test-Path -LiteralPath (Join-Path $missingProof 'startup')) { throw 'A copy without the installer marker wrote a startup entry.' }

    # 7. None of these runs extracted anything: the native libraries load from beside the exe.
    $extracted = @(Get-ChildItem -LiteralPath $bundleCache -Recurse -Force)
    if ($extracted.Count -ne 0) { throw "The frozen exe extracted $($extracted.Count) bundle entries at run time." }

    Write-Output 'Frozen Windows startup-command proof passed: Run value from a non-ASCII and percent path, batch-entry migration, approval cleared, disable removes legacy entries, foreign entries kept, missing marker refused, nothing extracted.'
}
finally {
    $env:LOCALAPPDATA = $savedLocalAppData
    $env:GLIDESLOPE_PROOF_ROOT = $savedProofRoot
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $savedBundleCache
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $current = Get-Process -Id $process.Id -ErrorAction Stop
            if ($current.StartTime.ToUniversalTime() -eq $process.StartTime.ToUniversalTime() -and
                [IO.Path]::GetFileName($current.Path) -eq 'Glideslope.App.exe' -and
                [IO.Path]::GetFullPath($current.Path).StartsWith([IO.Path]::GetFullPath($tempRoot), [StringComparison]::OrdinalIgnoreCase)) {
                $process.Kill()
                if (-not $process.WaitForExit(10000)) { throw 'Owned startup-command process survived cleanup.' }
            }
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $canonicalTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $canonicalRoot = [IO.Path]::GetFullPath($tempRoot)
        if (-not $canonicalRoot.StartsWith($canonicalTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $canonicalRoot).StartsWith('Glideslope.Packaging-WindowsStartup-', [StringComparison]::Ordinal) -or
            -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
            (Get-Content -LiteralPath $markerPath -Raw) -ne $ownerMarker) {
            throw "Refusing to remove a Windows startup temp root without its verified ownership marker: $canonicalRoot"
        }
        Remove-Item -LiteralPath $canonicalRoot -Recurse
        if (Test-Path -LiteralPath $canonicalRoot) { throw "Windows startup temp cleanup failed: $canonicalRoot" }
    }
}
