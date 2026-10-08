[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Installer,
    [string]$InnoCompiler
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
# Fail before producing a release if dependencies drift or scans do not pass.
& (Join-Path $PSScriptRoot 'Test-Security.ps1')
if ($Installer) {
    if (-not $InnoCompiler) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        $candidates = @(
            $(if ($command) { $command.Source }),
            (Join-Path $env:LOCALAPPDATA 'Programs\MagiDesk Build Tools\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
        )
        $InnoCompiler = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    }
    if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
        throw 'Inno Setup compiler not found. Install Inno Setup 6.3+ or pass -InnoCompiler <ISCC.exe>.'
    }
}
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'MagiDesk/MagiDesk.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$') { throw 'Invalid release version in project.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('magidesk-package-' + [guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $stage 'build'
$payload = Join-Path $stage 'MagiDesk'
$archiveRoot = Join-Path $repoRoot 'publish'
New-Item -ItemType Directory -Path $stage, $archiveRoot -Force | Out-Null

& dotnet build (Join-Path $repoRoot 'tools/UpdateSigning/UpdateSigning.csproj') -c Release "-p:BaseOutputPath=$buildRoot\" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Signing tool build failed.' }
& dotnet (Join-Path $buildRoot 'Release/net10.0/UpdateSigning.dll') check-keys $repoRoot
if ($LASTEXITCODE -ne 0) { throw 'Configure a release public key before packaging using scripts/Update-Signing.ps1.' }

# Tests run on the build host; the published runtime may differ.
& dotnet build (Join-Path $repoRoot 'MagiDesk.Tests/MagiDesk.Tests.csproj') -c Release "-p:BaseOutputPath=$buildRoot\" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Release test build failed.' }
$testOutput = & dotnet (Join-Path $buildRoot 'Release/net10.0-windows/MagiDesk.Tests.dll') --headless
$testExit = $LASTEXITCODE
$testOutput | Select-String '^FAIL|^Headless:' | ForEach-Object { Write-Output $_.Line }
if ($testExit -ne 0) { throw 'Headless tests failed; package not created.' }
& dotnet publish (Join-Path $repoRoot 'MagiDesk/MagiDesk.csproj') -c Release -r $Runtime --self-contained true "-p:BaseOutputPath=$buildRoot\" -o $payload --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($name in @('README.md', 'README.zh-CN.md', 'CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $payload
}
# Preserve the actual dependency redistribution notices, including bundled fonts.
$notices = Join-Path $payload 'ThirdPartyNotices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$lock = Get-Content -LiteralPath (Join-Path $repoRoot 'MagiDesk/packages.lock.json') -Raw | ConvertFrom-Json
$packages = @{}
foreach ($framework in $lock.dependencies.PSObject.Properties) {
    foreach ($package in $framework.Value.PSObject.Properties) { $packages[$package.Name] = $package.Value.resolved }
}
foreach ($id in $packages.Keys) {
    $source = Join-Path $packageRoot ($id.ToLowerInvariant() + '/' + $packages[$id])
    foreach ($file in Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '^(LICENSE|ThirdPartyNotices|THIRD-PARTY-NOTICES)' }) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices ($id + '-' + $file.Name))
    }
}
$revision = (& git -C $repoRoot rev-parse HEAD 2>$null)
$dirty = [bool](& git -C $repoRoot status --porcelain 2>$null)
@{ version = $version; runtime = $Runtime; revision = $revision; uncommittedChanges = $dirty; builtAtUtc = [DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'build-info.json') -Encoding UTF8
$archive = Join-Path $archiveRoot "MagiDesk-$version-$Runtime-$stamp.zip"
if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive" }
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
Write-Output "Package: $archive"
Write-Output "SHA256: $hash"
if ($Installer) {
    $packageName = "MagiDesk-$version-$Runtime-Setup-$stamp"
    $setup = Join-Path $archiveRoot ($packageName + '.exe')
    if (Test-Path -LiteralPath $setup) { throw "Installer already exists: $setup" }
    & $InnoCompiler '/Qp' "/DAppVersion=$version" "/DRuntime=$Runtime" "/DPublishDir=$payload" "/DPackageDir=$archiveRoot" "/DPackageName=$packageName" (Join-Path $repoRoot 'installer/MagiDesk.iss')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup)) { throw 'Installer compilation failed.' }
    $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    "$setupHash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ascii
    Write-Output "Installer: $setup"
    Write-Output "Installer SHA256: $setupHash"
}
Write-Output "Staging files retained: $stage"
