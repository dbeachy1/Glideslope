param(
    [Parameter(Mandatory)] [ValidateSet('Sign', 'Verify')] [string] $Action,
    [Parameter(Mandatory)] [string] $SignToolPath,
    [Parameter(Mandatory)] [string] $DlibPath,
    [Parameter(Mandatory)] [string] $MetadataPath,
    [Parameter(Mandatory)] [string[]] $Files
)

$ErrorActionPreference = 'Stop'
foreach ($path in @($SignToolPath, $DlibPath, $MetadataPath) + $Files) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required signing file is missing: $path" }
}

foreach ($file in $Files) {
    if ($Action -eq 'Sign') {
        $signArguments = @('sign', '/v', '/fd', 'SHA256', '/tr', 'http://timestamp.acs.microsoft.com',
            '/td', 'SHA256', '/dlib', $DlibPath, '/dmdf', $MetadataPath, $file)
        $output = & $SignToolPath @signArguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool sign failed for '$file' with exit code $LASTEXITCODE. $output"
        }
    }

    $verifyArguments = @('verify', '/pa', '/all', '/tw', '/v', $file)
    $output = & $SignToolPath @verifyArguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool verify failed for '$file' with exit code $LASTEXITCODE. $output"
    }
}
