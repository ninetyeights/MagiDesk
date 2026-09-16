param(
    [switch]$CheckOnly,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer was not found.' }
$instances = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
if (!$instances) { throw 'No Visual Studio installation with x64 C++ tools was found.' }
$instance = @($instances)[0]
$major = ([version]$instance.installationVersion).Major
$generator = switch ($major) {
    17 { 'Visual Studio 17 2022' }
    18 { 'Visual Studio 18 2026' }
    default { throw "Unsupported Visual Studio version: $major" }
}
$cmake = Join-Path $instance.installationPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { throw 'Install C++ CMake tools for Windows in Visual Studio Installer.' }
if ($Destination -and !(Test-Path -LiteralPath $Destination -PathType Container)) {
    throw 'Destination must be an existing directory containing the application.'
}
Write-Output "Visual Studio: $($instance.installationPath)"
Write-Output "Generator: $generator"
& $cmake --version
if ($LASTEXITCODE -ne 0) { throw 'CMake could not start.' }
if ($CheckOnly) { return }

# Separate caches for VS versions; all build outputs stay outside the repository.
$buildDirectory = Join-Path $env:TEMP "MagiDesk-DesktopDrawProbe-vs$major"
& $cmake -S $PSScriptRoot -B $buildDirectory -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$($instance.installationPath)"
if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed: $LASTEXITCODE" }
& $cmake --build $buildDirectory --config Release
if ($LASTEXITCODE -ne 0) { throw "Native probe build failed: $LASTEXITCODE" }
$library = Join-Path $buildDirectory 'Release\MagiDeskDesktopDrawProbe.dll'
if (!(Test-Path -LiteralPath $library)) { throw 'Build completed without the expected DLL.' }
Write-Output "Built: $library"
if ($Destination) {
    Copy-Item -LiteralPath $library -Destination $Destination
    Write-Output "Copied to: $Destination"
}
