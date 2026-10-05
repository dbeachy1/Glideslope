param(
    [string] $OutputDirectory = '..\..\artifacts\packages\windows',
    [string] $SignToolPath,
    [string] $DlibPath,
    [string] $MetadataPath
)

$ErrorActionPreference = 'Stop'
$signingValues = @($SignToolPath, $DlibPath, $MetadataPath)
$signedBuild = @($signingValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
if ($signedBuild -and @($signingValues | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw 'Signed builds require all three parameters: SignToolPath, DlibPath, and MetadataPath.'
}
if ($signedBuild) {
    foreach ($path in $signingValues) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required signing file is missing: $path" }
    }
    $SignToolPath = [IO.Path]::GetFullPath($SignToolPath)
    $DlibPath = [IO.Path]::GetFullPath($DlibPath)
    $MetadataPath = [IO.Path]::GetFullPath($MetadataPath)
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$appProject = Join-Path $repoRoot 'src\Glideslope.App\Glideslope.App.csproj'
$buildProject = Join-Path $PSScriptRoot 'Glideslope.InstallerBuild.csproj'
$installerScript = Join-Path $PSScriptRoot 'glideslope.iss'
# The installer puts this marker beside Glideslope.App.exe; without it the
# installed app could neither register nor reconcile start at sign-in.
$installedMarker = Join-Path $PSScriptRoot 'glideslope-installed.marker'
if (-not (Test-Path -LiteralPath $installedMarker -PathType Leaf)) { throw "The installed-copy marker is missing: $installedMarker" }
$outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
$versionOutput = & dotnet msbuild $appProject -getProperty:Version
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the authoritative Glideslope.App version.' }
$appVersion = ($versionOutput | Out-String).Trim()
if ($appVersion -notmatch '^[0-9A-Za-z.+~-]+$') { throw "Invalid app version from Glideslope.App.csproj: $appVersion" }

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "Glideslope.Packaging-WindowsBuild-$([guid]::NewGuid().ToString('N'))"
$ownerMarker = [guid]::NewGuid().ToString('N')
$markerPath = Join-Path $tempRoot '.owner'
$savedVersion = $env:GLIDESLOPE_APP_VERSION
$savedPublishDir = $env:GLIDESLOPE_PUBLISH_DIR
$savedSignedBuild = $env:GLIDESLOPE_SIGNED_BUILD
$savedSignedUninstallerDir = $env:GLIDESLOPE_SIGNED_UNINSTALLER_DIR
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Set-Content -LiteralPath $markerPath -Value $ownerMarker -NoNewline
    $publishDir = Join-Path $tempRoot 'publish'
    New-Item -ItemType Directory -Path $publishDir | Out-Null
    # --artifacts-path keeps intermediate output inside this temporary root, away from source bin/obj folders.
    # The win-x64 profile keeps native libraries beside the exe (IncludeNativeLibrariesForSelfExtract=false);
    # verify-launcher.ps1 checks this packaging requirement.
    & dotnet publish $appProject --configuration Release --runtime win-x64 `
        --artifacts-path (Join-Path $tempRoot 'build') `
        --self-contained true -p:PublishProfile=win-x64 -p:PublishDir="$publishDir\" `
        -p:DebugSymbols=false -p:DebugType=None `
        "-p:Version=$appVersion" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Windows app publish failed with exit code $LASTEXITCODE." }

    & (Join-Path $PSScriptRoot 'verify-launcher.ps1') -PublishDir $publishDir -AppVersion $appVersion
    if ($LASTEXITCODE -ne 0) { throw 'Windows launcher proof failed.' }
    & (Join-Path $PSScriptRoot 'verify-startup-command.ps1') -PublishDir $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Frozen Windows startup-command proof failed.' }

    & dotnet restore $buildProject --locked-mode --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned project-local Inno Setup compiler package.' }
    $compilerOutput = & dotnet msbuild $buildProject -getProperty:InnoSetupCompiler
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the pinned Inno Setup compiler path.' }
    $compiler = ($compilerOutput | Out-String).Trim()
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Pinned Inno Setup compiler is missing: $compiler" }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $outputFile = Join-Path $outputRoot "Glideslope-Setup-$appVersion-x64.exe"
    $manifestFile = "$outputFile.files.json"
    $hashFile = "$outputFile.sha256"
    foreach ($path in @($outputFile,$manifestFile,$hashFile)) {
        if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite existing package artifact: $path" }
    }

    if ($signedBuild) {
        # This publish is single-file for managed application code. Its only Glideslope-owned PE is
        # the app executable; neighboring native DLLs belong to dependencies and keep their signatures.
        $ownedExecutable = Join-Path $publishDir 'Glideslope.App.exe'
        if (-not (Test-Path -LiteralPath $ownedExecutable -PathType Leaf)) { throw "Glideslope app executable is missing: $ownedExecutable" }
        & (Join-Path $PSScriptRoot 'Invoke-WindowsArtifactSigning.ps1') -Action Sign `
            -SignToolPath $SignToolPath -DlibPath $DlibPath -MetadataPath $MetadataPath -Files @($ownedExecutable)
        $signingHelper = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Invoke-WindowsArtifactSigning.ps1'))
        $powerShellExe = [IO.Path]::GetFullPath((Join-Path $PSHOME 'powershell.exe'))
        if (-not (Test-Path -LiteralPath $powerShellExe -PathType Leaf)) {
            $powerShellExe = [IO.Path]::GetFullPath((Join-Path $PSHOME 'pwsh.exe'))
        }
        if (-not (Test-Path -LiteralPath $powerShellExe -PathType Leaf)) { throw 'Could not locate PowerShell to run the Inno signing callback.' }
        $signedUninstallerDir = Join-Path $tempRoot 'signed-uninstaller'
        New-Item -ItemType Directory -Path $signedUninstallerDir | Out-Null
        $env:GLIDESLOPE_SIGNED_BUILD = '1'
        $env:GLIDESLOPE_SIGNED_UNINSTALLER_DIR = $signedUninstallerDir
        $powerShellForInno = $powerShellExe.Replace('$', '$$')
        $helperForInno = $signingHelper.Replace('$', '$$')
        $signToolForInno = $SignToolPath.Replace('$', '$$')
        $dlibForInno = $DlibPath.Replace('$', '$$')
        $metadataForInno = $MetadataPath.Replace('$', '$$')
        $signCommand = '$q' + $powerShellForInno + '$q -NoProfile -File $q' + $helperForInno + '$q -Action Sign -SignToolPath $q' + $signToolForInno + '$q -DlibPath $q' + $dlibForInno + '$q -MetadataPath $q' + $metadataForInno + '$q -Files $f'
        $arguments = @('/Qp', "/O$outputRoot", "/FGlideslope-Setup-$appVersion-x64", "/Sazurecodesign=$signCommand", $installerScript)
    }
    else {
        $env:GLIDESLOPE_SIGNED_BUILD = $null
        $env:GLIDESLOPE_SIGNED_UNINSTALLER_DIR = $null
        $arguments = @('/Qp', "/O$outputRoot", "/FGlideslope-Setup-$appVersion-x64", $installerScript)
    }

    $env:GLIDESLOPE_APP_VERSION = $appVersion
    $env:GLIDESLOPE_PUBLISH_DIR = $publishDir
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $compiler
    $startInfo.WorkingDirectory = $tempRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void] $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'Could not start the pinned Inno Setup compiler.' }
    $compilerStdoutTask = $process.StandardOutput.ReadToEndAsync()
    $compilerStderrTask = $process.StandardError.ReadToEndAsync()
    $compilerPid = $process.Id
    $compilerStarted = $process.StartTime.ToUniversalTime()
    if (-not $process.WaitForExit(120000)) {
        $running = Get-Process -Id $compilerPid -ErrorAction Stop
        if ($running.StartTime.ToUniversalTime() -ne $compilerStarted -or
            [IO.Path]::GetFullPath($running.Path) -ne [IO.Path]::GetFullPath($compiler)) {
            throw 'Timed-out compiler identity changed; refusing to stop it.'
        }
        $process.Kill($true)
        if (-not $process.WaitForExit(10000)) { throw 'Owned Inno Setup compiler process tree did not exit.' }
        throw 'Inno Setup compilation exceeded 120 seconds.'
    }
    $compilerExitCode = $process.ExitCode
    $compilerStdout = $compilerStdoutTask.GetAwaiter().GetResult()
    $compilerStderr = $compilerStderrTask.GetAwaiter().GetResult()
    $process.Dispose()
    if ($compilerExitCode -ne 0) { throw "Inno Setup returned exit code $compilerExitCode. Output: $compilerStdout $compilerStderr" }
    if (-not (Test-Path -LiteralPath $outputFile -PathType Leaf)) { throw "Inno Setup did not produce the expected installer: $outputFile" }

    if ($signedBuild) {
        & (Join-Path $PSScriptRoot 'Invoke-WindowsArtifactSigning.ps1') -Action Verify `
            -SignToolPath $SignToolPath -DlibPath $DlibPath -MetadataPath $MetadataPath -Files @($outputFile)
        if ($LASTEXITCODE -ne 0) { throw 'Setup or uninstaller signature verification failed.' }
    }

    # Windows SDK reference publishes may copy dependency symbols into the frozen output;
    # the package manifest and installer intentionally exclude all PDBs.
    $publishFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse | Where-Object Extension -ne '.pdb')
    $manifest = $publishFiles | ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($publishDir.TrimEnd('\').Length + 1).Replace('\','/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestFile -Encoding utf8
    $hash = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $hashFile -Value "$hash *$(Split-Path -Leaf $outputFile)" -Encoding ascii
    Write-Output "Built $outputFile from Glideslope.App version $appVersion."
    Write-Output "Manifest: $manifestFile"
    Write-Output "SHA-256: $hash"
}
finally {
    $env:GLIDESLOPE_APP_VERSION = $savedVersion
    $env:GLIDESLOPE_PUBLISH_DIR = $savedPublishDir
    $env:GLIDESLOPE_SIGNED_BUILD = $savedSignedBuild
    $env:GLIDESLOPE_SIGNED_UNINSTALLER_DIR = $savedSignedUninstallerDir
    if (Test-Path -LiteralPath $tempRoot) {
        $canonicalTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $canonicalRoot = [IO.Path]::GetFullPath($tempRoot)
        if (-not $canonicalRoot.StartsWith($canonicalTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $canonicalRoot).StartsWith('Glideslope.Packaging-WindowsBuild-', [StringComparison]::Ordinal) -or
            -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
            (Get-Content -LiteralPath $markerPath -Raw) -ne $ownerMarker) {
            throw "Refusing to remove a Windows package build temp root without its ownership marker: $canonicalRoot"
        }
        Remove-Item -LiteralPath $canonicalRoot -Recurse
        if (Test-Path -LiteralPath $canonicalRoot) { throw "Windows package build cleanup failed: $canonicalRoot" }
    }
}
