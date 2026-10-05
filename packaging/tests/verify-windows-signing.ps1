$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "Glideslope.WindowsSigningTest-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$ownerToken = [guid]::NewGuid().ToString('N')
$ownerMarker = Join-Path $tempRoot '.owner'
Set-Content -LiteralPath $ownerMarker -Value $ownerToken -NoNewline
$savedTestLog = $env:GLIDESLOPE_SIGNING_TEST_LOG
$savedTestFailure = $env:GLIDESLOPE_SIGNING_TEST_FAILURE
try {
    $signTool = Join-Path $tempRoot 'mock-signtool.ps1'
    $dlib = Join-Path $tempRoot 'mock.dlib'
    $metadata = Join-Path $tempRoot 'metadata.json'
    $app = Join-Path $tempRoot 'Glideslope.App.exe'
    $log = Join-Path $tempRoot 'calls.txt'
    foreach ($path in @($dlib, $metadata, $app)) { Set-Content -LiteralPath $path -Value 'test' }
@'
$action = [string] $args[0]
[IO.File]::AppendAllText($env:GLIDESLOPE_SIGNING_TEST_LOG, ($args -join ' ') + [Environment]::NewLine)
if ($env:GLIDESLOPE_SIGNING_TEST_FAILURE -eq $action) { exit 9 }
exit 0
'@ | Set-Content -LiteralPath $signTool

    $env:GLIDESLOPE_SIGNING_TEST_LOG = $log
    $env:GLIDESLOPE_SIGNING_TEST_FAILURE = ''
    $helper = Join-Path $repoRoot 'packaging\windows\Invoke-WindowsArtifactSigning.ps1'
    & $helper -Action Sign -SignToolPath $signTool -DlibPath $dlib -MetadataPath $metadata -Files @($app)
    & $helper -Action Verify -SignToolPath $signTool -DlibPath $dlib -MetadataPath $metadata -Files @($app)
    $calls = @(Get-Content -LiteralPath $log)
    if ($calls.Count -ne 3 -or $calls[0] -notmatch '^sign .*\/fd SHA256 .*\/tr http://timestamp\.acs\.microsoft\.com .*\/td SHA256' -or
        $calls[1] -notmatch '^verify .*\/pa .*\/all .*\/tw' -or $calls[2] -notmatch '^verify .*\/pa .*\/all .*\/tw') {
        throw "Signing call order or required algorithms/options were wrong: $($calls -join '; ')"
    }

    foreach ($action in @('sign', 'verify')) {
        Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
        $env:GLIDESLOPE_SIGNING_TEST_FAILURE = $action
        $failed = $false
        try { & $helper -Action ([cultureinfo]::InvariantCulture.TextInfo.ToTitleCase($action)) -SignToolPath $signTool -DlibPath $dlib -MetadataPath $metadata -Files @($app) }
        catch { $failed = $_.Exception.Message -match "SignTool $action failed.*exit code 9" }
        if (-not $failed) { throw "The signing helper did not report and stop on a SignTool $action failure." }
        $failureCalls = @(Get-Content -LiteralPath $log)
        if ($failureCalls.Count -ne 1 -or -not $failureCalls[0].StartsWith("$action ")) { throw "Unexpected calls after SignTool $action failure: $($failureCalls -join '; ')" }
    }

    $failed = $false
    try { & (Join-Path $repoRoot 'packaging\windows\build-installer.ps1') -SignToolPath $signTool }
    catch { $failed = $_.Exception.Message -match 'require all three parameters' }
    if (-not $failed) { throw 'The build did not reject a partial signing configuration before starting.' }

    Write-Output 'Windows signing configuration, sign/verify order, SHA-256/RFC 3161 options, and failure handling passed.'
}
finally {
    if ($null -eq $savedTestLog) { Remove-Item Env:\GLIDESLOPE_SIGNING_TEST_LOG -ErrorAction SilentlyContinue }
    else { $env:GLIDESLOPE_SIGNING_TEST_LOG = $savedTestLog }
    if ($null -eq $savedTestFailure) { Remove-Item Env:\GLIDESLOPE_SIGNING_TEST_FAILURE -ErrorAction SilentlyContinue }
    else { $env:GLIDESLOPE_SIGNING_TEST_FAILURE = $savedTestFailure }
    if (Test-Path -LiteralPath $tempRoot) {
        $canonicalTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $canonicalRoot = [IO.Path]::GetFullPath($tempRoot)
        if (-not $canonicalRoot.StartsWith($canonicalTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $canonicalRoot).StartsWith('Glideslope.WindowsSigningTest-', [StringComparison]::Ordinal) -or
            -not (Test-Path -LiteralPath $ownerMarker -PathType Leaf) -or (Get-Content -LiteralPath $ownerMarker -Raw) -ne $ownerToken) {
            throw "Refusing to remove signing-test files without the verified owned temp root: $canonicalRoot"
        }
        Remove-Item -LiteralPath $canonicalRoot -Recurse
        if (Test-Path -LiteralPath $canonicalRoot) { throw "Signing-test cleanup failed: $canonicalRoot" }
    }
}
