param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,
    [switch] $Live,
    [switch] $UseRecordedHistory,
    [switch] $AllowExistingRuntimeDesktops,
    [switch] $Overwrite
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$projectPrefix = $projectRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($projectPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be inside this project checkout.'
}

$artifactsPath = Join-Path $projectRoot 'temp\screenshot-capture-artifacts'
$projectFile = Join-Path $projectRoot 'src\Glideslope.ScreenshotCapture\Glideslope.ScreenshotCapture.csproj'
& dotnet build $projectFile --configuration Release --artifacts-path $artifactsPath
if ($LASTEXITCODE -ne 0) {
    throw "Screenshot capture build failed with exit code $LASTEXITCODE."
}

$appHost = Join-Path $artifactsPath 'bin\Glideslope.ScreenshotCapture\release\Glideslope.ScreenshotCapture.exe'
if (-not (Test-Path -LiteralPath $appHost -PathType Leaf)) {
    throw 'The compiled screenshot capture apphost was not produced.'
}
$arguments = @('--output', $outputPath)
if ($Live) { $arguments += '--live' }
if ($UseRecordedHistory) { $arguments += '--use-recorded-history' }
if ($AllowExistingRuntimeDesktops) { $arguments += '--allow-existing-runtime-desktops' }
if ($Overwrite) { $arguments += '--overwrite' }
& $appHost @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Screenshot capture failed with exit code $LASTEXITCODE."
}

$assembly = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $artifactsPath 'bin\Glideslope.App\release\Glideslope.App.dll'))
$version = $assembly.Version.ToString(3)
$expected = @(
    "Codex-Full-Dark-v$version.png",
    "Claude-Mini-Dark-v$version.png",
    "Claude-Full-Light-v$version.png",
    "Gemini-Full-Dark-v$version.png"
)
Add-Type -AssemblyName System.Drawing
foreach ($name in $expected) {
    $file = Join-Path $outputPath $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Expected capture is missing: $name" }
    $image = [System.Drawing.Image]::FromFile($file)
    try {
        if ($image.Width -lt 530 -or $image.Height -lt 270) { throw "Capture has unexpected dimensions: $name ($($image.Width)x$($image.Height))." }
    }
    finally { $image.Dispose() }
}
Write-Output "Captured and checked four versioned PNGs in $outputPath"
