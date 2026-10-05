param([string] $OutputDirectory = 'artifacts\verification\header-visual-proof')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)) }
if (-not $outputRoot.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Header QA screenshots must be written inside this repository.'
}
& dotnet run --project (Join-Path $repoRoot 'src\Glideslope.Shell.Specs\Glideslope.Shell.Specs.csproj') -- --header-visual-proof $outputRoot
if ($LASTEXITCODE -ne 0) { throw 'Header screenshot capture failed.' }
Write-Output "Open and inspect the screenshots in $outputRoot before approving the UI change."
