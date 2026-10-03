[CmdletBinding()]
param(
    [string] $Configuration = 'Debug'
)

# Builds the solution into an isolated artifacts folder and runs Glideslope.NativeProof against the resulting
# Glideslope.App.exe. This proof is excluded from default suites and must be invoked explicitly.
#
# Exit codes are defined by Glideslope.NativeProof (see Program.cs): 0 means all scenarios completed as designed;
# 1 means a scenario failed; 2 means arguments were invalid; 3 means the hidden desktop or app could not start;
# 4 means cleanup could not verify that the process, desktop, and folder were removed. A build failure exits 1.

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repoRoot 'Glideslope.sln'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) { throw "solution_not_found: $solutionPath" }

$artifactsRelative = 'temp/artifacts-wp4'
$artifactsPath = Join-Path $repoRoot $artifactsRelative
$logDirectory = Join-Path $repoRoot 'temp'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

$buildLog = Join-Path $logDirectory 'wp4-build.log'
Write-Output "Building $solutionPath into $artifactsRelative (configuration $Configuration)..."
& dotnet build $solutionPath --configuration $Configuration --artifacts-path $artifactsRelative *> $buildLog
$buildExitCode = $LASTEXITCODE
Get-Content -LiteralPath $buildLog | Write-Output
if ($buildExitCode -ne 0) {
    Write-Error "dotnet build failed with exit code $buildExitCode; see $buildLog"
    exit 1
}

# The .NET SDK's --artifacts-path layout is <path>/bin/<ProjectName>/<configuration lowercased>/...;
# Get-ChildItem is scoped to this run's artifacts folder.
function Find-BuiltExecutable {
    param([string] $ProjectName)
    $candidates = @(Get-ChildItem -LiteralPath $artifactsPath -Recurse -Filter "$ProjectName.exe" -File -ErrorAction Stop)
    if ($candidates.Count -eq 0) { throw "built_executable_not_found: $ProjectName.exe under $artifactsPath" }
    # Prefer the requested configuration if more than one is present from an earlier run.
    $preferred = $candidates | Where-Object { $_.FullName -match [regex]::Escape($Configuration.ToLowerInvariant()) }
    $chosen = if ($preferred.Count -gt 0) { $preferred[0] } else { $candidates[0] }
    return [IO.Path]::GetFullPath($chosen.FullName)
}

$appExePath = Find-BuiltExecutable 'Glideslope.App'
$proofExePath = Find-BuiltExecutable 'Glideslope.NativeProof'
Write-Output "App:   $appExePath"
Write-Output "Proof: $proofExePath"

$runRoot = Join-Path $logDirectory "native-proof-run-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$runLog = Join-Path $logDirectory 'wp4-run.log'

Write-Output "Running the native window proof against a hidden desktop; owned temp folder: $runRoot"
& $proofExePath --app $appExePath --root $runRoot *> $runLog
$proofExitCode = $LASTEXITCODE
Get-Content -LiteralPath $runLog | Write-Output

# The proof is responsible for deleting its own owned folder and asserting it is gone; this is only a
# defensive backstop in case the proof exited before reaching that step (for example, a build- or
# argument-level failure), so a failed run never leaves the folder behind for the caller to notice.
if (Test-Path -LiteralPath $runRoot) {
    Write-Output "Owned proof folder was not removed by the proof itself; removing it here: $runRoot"
    Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
}

exit $proofExitCode
