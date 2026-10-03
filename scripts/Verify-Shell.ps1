[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ArtifactPath,
    [ValidateRange(1, 300)]
    [int] $DurationSeconds = 30,
    [switch] $VerifySecondLaunch,
    [ValidateRange(1, 60)]
    [int] $SecondLaunchTimeoutSeconds = 10,
    [switch] $ExpectedCleanExit,
    [string[]] $ArtifactArgument = @()
)

$ErrorActionPreference = "Stop"
$ownedRoot = $null
$markerPath = $null
$ownedProcess = $null
$ownedPid = $null
$ownedStartUtc = $null
$ownedExecutable = $null
$secondProcess = $null
$secondPid = $null
$secondStartUtc = $null
$secondExecutable = $null
$ownedProcessInfo = @{}
$environmentBefore = @{}

function Set-ChildEnvironment {
    param([string] $Name, [string] $Value)
    $environmentBefore[$Name] = [Environment]::GetEnvironmentVariable($Name, "Process")
    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
}

function Restore-ChildEnvironment {
    foreach ($name in $environmentBefore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environmentBefore[$name], "Process")
    }
}

function Assert-OwnedRoot {
    if ([string]::IsNullOrWhiteSpace($ownedRoot) -or -not (Test-Path -LiteralPath $ownedRoot -PathType Container)) { throw "owned_temp_root_missing" }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $canonicalRoot = [IO.Path]::GetFullPath($ownedRoot)
    $expectedPrefix = "$tempRoot$([IO.Path]::DirectorySeparatorChar)Glideslope-ShellProof-"
    if (-not $canonicalRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "owned_temp_root_outside_expected_temp_prefix" }
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or (Get-Content -LiteralPath $markerPath -Raw) -ne "Glideslope-ShellProof-v1`n") { throw "owned_temp_root_marker_invalid" }
}

function Get-VerifiedProcess {
    if ($null -eq $ownedPid) { return $null }
    $candidate = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return $null }
    $candidatePath = $null
    try { $candidatePath = $candidate.MainModule.FileName } catch { }
    if ([string]::IsNullOrWhiteSpace($candidatePath) -or -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($candidatePath), $ownedExecutable)) { throw "owned_process_executable_mismatch" }
    if ([Math]::Abs(($candidate.StartTime.ToUniversalTime() - $ownedStartUtc).TotalSeconds) -gt 2) { throw "owned_process_start_time_mismatch" }
    return $candidate
}

function Get-OwnedDescendantIds {
    if ($null -eq (Get-VerifiedProcess)) { return @() }
    $rows = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId)
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $found = [System.Collections.Generic.List[int]]::new()
    $pending.Enqueue([int]$ownedPid)
    while ($pending.Count -gt 0) {
        $parent = $pending.Dequeue()
        foreach ($row in $rows | Where-Object { [int]$_.ParentProcessId -eq $parent }) {
            $child = [int]$row.ProcessId
            if (-not $found.Contains($child)) { $found.Add($child); $pending.Enqueue($child) }
        }
    }
    return $found.ToArray()
}

function Record-OwnedDescendants {
    if ($null -eq $ownedPid) { return }
    foreach ($descendant in @(Get-OwnedDescendantIds)) {
        if (-not $ownedProcessInfo.ContainsKey([int]$descendant)) {
            $candidate = Get-Process -Id $descendant -ErrorAction SilentlyContinue
            if ($null -ne $candidate) {
                $candidatePath = $null
                try { $candidatePath = [IO.Path]::GetFullPath($candidate.MainModule.FileName) } catch { }
                if (-not [string]::IsNullOrWhiteSpace($candidatePath)) {
                    $ownedProcessInfo[[int]$descendant] = [pscustomobject]@{ StartUtc = $candidate.StartTime.ToUniversalTime(); Executable = $candidatePath }
                }
            }
        }
    }
}

function Stop-OneOwnedProcess {
    param([int] $ProcessId, [DateTime] $ExpectedStartUtc, [string] $ExpectedExecutable)
    $candidate = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return }
    if ([Math]::Abs(($candidate.StartTime.ToUniversalTime() - $ExpectedStartUtc).TotalSeconds) -gt 2) { throw "owned_process_start_time_mismatch" }
    $candidatePath = $null
    try { $candidatePath = [IO.Path]::GetFullPath($candidate.MainModule.FileName) } catch { }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($candidatePath, $ExpectedExecutable)) { throw "owned_process_executable_mismatch" }
    if ($candidate.HasExited) { [void]$candidate.WaitForExit(2000); return }
    [void]$candidate.CloseMainWindow()
    if (-not $candidate.WaitForExit(2000)) {
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        if (-not $candidate.WaitForExit(5000)) { throw "owned_process_did_not_exit" }
    }
}

function Stop-OwnedProcess {
    Record-OwnedDescendants
    $descendants = @($ownedProcessInfo.Keys | Where-Object { [int]$_ -ne [int]$ownedPid } | ForEach-Object { [int]$_ })
    [array]::Reverse($descendants)
    foreach ($descendant in $descendants) {
        $info = $ownedProcessInfo[$descendant]
        Stop-OneOwnedProcess -ProcessId $descendant -ExpectedStartUtc $info.StartUtc -ExpectedExecutable $info.Executable
    }
    $candidate = Get-VerifiedProcess
    if ($null -ne $candidate) { Stop-OneOwnedProcess -ProcessId $ownedPid -ExpectedStartUtc $ownedStartUtc -ExpectedExecutable $ownedExecutable }
}

function Remove-OwnedRoot {
    Assert-OwnedRoot
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try { Remove-Item -LiteralPath $ownedRoot -Recurse -Force -ErrorAction Stop; break }
        catch { if ($attempt -eq 8) { throw }; Start-Sleep -Milliseconds (125 * $attempt) }
    }
    if (Test-Path -LiteralPath $ownedRoot) { throw "owned_temp_root_survived_cleanup" }
}

function Stop-SecondLaunch {
    if ($null -eq $secondPid -or $null -eq $secondStartUtc -or [string]::IsNullOrWhiteSpace($secondExecutable)) { return }
    Stop-OneOwnedProcess -ProcessId $secondPid -ExpectedStartUtc $secondStartUtc -ExpectedExecutable $secondExecutable
}

$exitCode = 0
try {
    $resolvedArtifact = [IO.Path]::GetFullPath($ArtifactPath)
    if (-not (Test-Path -LiteralPath $resolvedArtifact -PathType Leaf)) { throw "artifact_not_found" }

    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $ownedRoot = Join-Path $tempBase ("Glideslope-ShellProof-{0}" -f ([Guid]::NewGuid().ToString("N")))
    if (Test-Path -LiteralPath $ownedRoot) { throw "owned_temp_root_collision" }
    New-Item -ItemType Directory -Path $ownedRoot -ErrorAction Stop | Out-Null
    $markerPath = Join-Path $ownedRoot ".owner"
    Set-Content -LiteralPath $markerPath -Value "Glideslope-ShellProof-v1`n" -NoNewline
    Assert-OwnedRoot

    $bundleRoot = Join-Path $ownedRoot "bundle"
    $resolvedProofRoot = Join-Path $ownedRoot "app-root"
    New-Item -ItemType Directory -Path $resolvedProofRoot, $bundleRoot -Force | Out-Null
    Set-ChildEnvironment "DOTNET_BUNDLE_EXTRACT_BASE_DIR" $bundleRoot

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedArtifact
    $startInfo.WorkingDirectory = $ownedRoot
    $startInfo.UseShellExecute = $false
    [void]$startInfo.ArgumentList.Add("--proof-root")
    [void]$startInfo.ArgumentList.Add($resolvedProofRoot)
    foreach ($argument in $ArtifactArgument) { [void]$startInfo.ArgumentList.Add($argument) }
    $ownedProcess = [Diagnostics.Process]::new()
    $ownedProcess.StartInfo = $startInfo
    if (-not $ownedProcess.Start()) { throw "artifact_start_failed" }
    $ownedPid = $ownedProcess.Id
    $ownedExecutable = [IO.Path]::GetFullPath($resolvedArtifact)
    $ownedStartUtc = $ownedProcess.StartTime.ToUniversalTime()
    $ownedProcessInfo[[int]$ownedPid] = [pscustomobject]@{ StartUtc = $ownedStartUtc; Executable = $ownedExecutable }

    Write-Output "PROOF_ROOT_CREATED=True"
    Write-Output "PROOF_ROOT=$resolvedProofRoot"
    Write-Output "OWNED_PID=$ownedPid"
    Write-Output "OWNED_EXECUTABLE=$ownedExecutable"
    Write-Output "OWNED_CREATION_UTC=$($ownedStartUtc.ToString('o'))"
    Write-Output "GUI_SUCCESS_ASSERTION=MANUAL"
    Write-Output "GUI_PROOF_INTERVAL_SECONDS=$DurationSeconds"

    if ($VerifySecondLaunch) {
        $secondStartInfo = [Diagnostics.ProcessStartInfo]::new()
        $secondStartInfo.FileName = $resolvedArtifact
        $secondStartInfo.WorkingDirectory = $ownedRoot
        $secondStartInfo.UseShellExecute = $false
        [void]$secondStartInfo.ArgumentList.Add("--proof-root")
        [void]$secondStartInfo.ArgumentList.Add($resolvedProofRoot)
        foreach ($argument in $ArtifactArgument) { [void]$secondStartInfo.ArgumentList.Add($argument) }
        $secondProcess = [Diagnostics.Process]::new()
        $secondProcess.StartInfo = $secondStartInfo
        if (-not $secondProcess.Start()) { throw "second_launch_start_failed" }
        $secondPid = $secondProcess.Id
        $secondExecutable = [IO.Path]::GetFullPath($resolvedArtifact)
        $secondStartUtc = $secondProcess.StartTime.ToUniversalTime()
        Write-Output "SECOND_LAUNCH_ENABLED=True"
        Write-Output "SECOND_LAUNCH_PID=$secondPid"
        Write-Output "SECOND_LAUNCH_EXECUTABLE=$secondExecutable"
        Write-Output "SECOND_LAUNCH_CREATION_UTC=$($secondStartUtc.ToString('o'))"
        $secondDeadline = [Diagnostics.Stopwatch]::GetTimestamp() + ([Diagnostics.Stopwatch]::Frequency * $SecondLaunchTimeoutSeconds)
        while (-not $secondProcess.HasExited -and [Diagnostics.Stopwatch]::GetTimestamp() -lt $secondDeadline) { Start-Sleep -Milliseconds 100 }
        if (-not $secondProcess.HasExited) {
            Write-Output "SECOND_LAUNCH_EXITED=False"
            throw "second_launch_timeout"
        }
        $secondExitCode = $secondProcess.ExitCode
        Write-Output "SECOND_LAUNCH_EXITED=True"
        Write-Output "SECOND_LAUNCH_EXIT_CODE=$secondExitCode"
        if ($secondExitCode -ne 0) { throw "second_launch_exit_$secondExitCode" }
        Write-Output "SECOND_LAUNCH_RESULT=FORWARDED_AND_EXITED"
        $secondProcess.Dispose()
        $secondProcess = $null
    }

    $deadline = [Diagnostics.Stopwatch]::GetTimestamp() + ([Diagnostics.Stopwatch]::Frequency * $DurationSeconds)
    while ([Diagnostics.Stopwatch]::GetTimestamp() -lt $deadline) {
        Record-OwnedDescendants
        if ($ownedProcess.HasExited) {
            Write-Output "PROCESS_EXITED=True"
            if ($ownedProcess.ExitCode -ne 0) { throw "artifact_exit_$($ownedProcess.ExitCode)" }
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ownedProcess.HasExited) { Write-Output "GUI_PROOF_INTERVAL_COMPLETE=True" } else {
        Write-Output "GUI_PROOF_INTERVAL_COMPLETE=False"
        if (-not $ExpectedCleanExit -and $ownedProcess.ExitCode -eq 0) { throw "primary_exited_before_interval" }
    }
}
catch {
    $exitCode = 1
    Write-Error ("SHELL_PROOF_FAILED: " + $_.Exception.Message)
}
finally {
    $cleanupErrors = [System.Collections.Generic.List[string]]::new()
    $processCleanupFailed = $false
    try { Stop-SecondLaunch } catch { $processCleanupFailed = $true; $cleanupErrors.Add("second_process: $($_.Exception.Message)") }
    try { if ($null -ne $secondProcess) { $secondProcess.Dispose() } } catch { $cleanupErrors.Add("second_dispose: $($_.Exception.Message)") }
    try { Stop-OwnedProcess } catch { $processCleanupFailed = $true; $cleanupErrors.Add("primary_process: $($_.Exception.Message)") }
    try { if ($null -ne $ownedProcess) { $ownedProcess.Dispose() } } catch { $cleanupErrors.Add("primary_dispose: $($_.Exception.Message)") }
    if ($null -ne $ownedRoot -and $null -ne $markerPath) {
        if ($processCleanupFailed) { $cleanupErrors.Add("owned_root_retained: $ownedRoot") }
        else {
            try { Remove-OwnedRoot; Write-Output "PROOF_ROOT_REMOVED=True" } catch { $cleanupErrors.Add("owned_root: $($_.Exception.Message); owned_root=$ownedRoot") }
        }
    }
    if ($cleanupErrors.Count -gt 0) {
        $exitCode = 1
        foreach ($cleanupError in $cleanupErrors) { Write-Error ("SHELL_PROOF_CLEANUP_FAILED: " + $cleanupError) }
    }
    Restore-ChildEnvironment
}
exit $exitCode
