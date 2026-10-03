[CmdletBinding()]
param(
    [Parameter(Mandatory, Position=0)][ValidateSet('init', 'trust', 'sign', 'check-keys', 'verify')][string]$Command,
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'MagiDesk Signing/release.key'),
    [string]$Version,
    [string]$OutputDirectory,
    [string[]]$InstallerPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) 'magidesk-signing-build'
& dotnet build (Join-Path $repoRoot 'tools/UpdateSigning/UpdateSigning.csproj') -c Release "-p:BaseOutputPath=$buildRoot\" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Signing tool build failed.' }
$toolArgs = [Collections.Generic.List[string]]::new()
$toolArgs.Add($Command)
$toolArgs.Add($repoRoot)
if ($Command -in @('init', 'trust', 'sign')) { $toolArgs.Add([IO.Path]::GetFullPath($PrivateKeyPath)) }
if ($Command -eq 'verify') {
    if (-not $Version -or -not $OutputDirectory) { throw 'Verify requires -Version and -OutputDirectory containing the signed manifest and installers.' }
    $toolArgs.Add($Version)
    $toolArgs.Add([IO.Path]::GetFullPath($OutputDirectory))
}
if ($Command -eq 'sign') {
    if (-not $Version -or -not $OutputDirectory -or $InstallerPath.Count -lt 1 -or $InstallerPath.Count -gt 2) {
        throw 'Sign requires -Version, -OutputDirectory and one or two -InstallerPath values.'
    }
    $toolArgs.Add($Version)
    $toolArgs.Add([IO.Path]::GetFullPath($OutputDirectory))
    foreach ($file in $InstallerPath) { $toolArgs.Add([IO.Path]::GetFullPath($file)) }
}
& dotnet (Join-Path $buildRoot 'Release/net10.0/UpdateSigning.dll') @toolArgs
if ($LASTEXITCODE -ne 0) { throw 'Signing command failed; no update is approved by this command.' }
