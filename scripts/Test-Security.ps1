[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

function Assert-SecretScan([int]$Result, [string]$Report, [string]$Scope) {
    if ($Result -eq 0) { return }
    if (Test-Path -LiteralPath $Report) {
        # Locations only: do not write matching source text or credential values to CI logs.
        $findings = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
        $findings | Select-Object RuleID, File, StartLine, Fingerprint | Format-List | Out-Host
    }
    throw "Secret scan of $Scope failed. Review findings before publishing."
}

foreach ($project in @('MagiDesk', 'MagiDesk.Tests', 'tools/UpdateSigning')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "$project/packages.lock.json"))) {
        throw "Missing dependency lock: $project/packages.lock.json"
    }
}
# Force a fresh audit even when the assets file is already up to date.
& dotnet restore (Join-Path $repoRoot 'MagiDesk.slnx') --locked-mode --force --no-http-cache --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Locked restore / dependency audit failed.' }

# Fixed version AND digest; never execute a mutable latest download.
$version = '8.30.1'
$archiveHash = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('magidesk-security-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $archive = Join-Path $scratch 'gitleaks.zip'
    Invoke-WebRequest "https://github.com/gitleaks/gitleaks/releases/download/v$version/gitleaks_${version}_windows_x64.zip" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
        throw 'Gitleaks download checksum mismatch.'
    }
    $toolDir = Join-Path $scratch 'tool'
    Expand-Archive -LiteralPath $archive -DestinationPath $toolDir
    $tool = Join-Path $toolDir 'gitleaks.exe'
    $historyReport = Join-Path $scratch 'history.json'
    $config = Join-Path $repoRoot '.gitleaks.toml'
    & $tool git $repoRoot --config=$config --log-opts=--all --redact=100 --no-banner --exit-code=1 --report-format=json --report-path=$historyReport
    Assert-SecretScan $LASTEXITCODE $historyReport 'Git history'

    # Include current edits and new source files, without copying ignored build output.
    # This also catches a secret introduced since the latest commit.
    $source = Join-Path $scratch 'source'
    New-Item -ItemType Directory -Path $source | Out-Null
    $files = & git -C $repoRoot -c core.quotepath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate working tree for secret scanning.' }
    foreach ($relative in ($files | Sort-Object -Unique)) {
        $original = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
        if (-not $original.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Invalid path in working-tree inventory.'
        }
        if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { continue } # Deleted file
        if ((Get-Item -LiteralPath $original).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Review linked file before scanning: $relative"
        }
        $target = Join-Path $source $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $original -Destination $target
    }
    $workingReport = Join-Path $scratch 'working.json'
    & $tool dir $source --config=$config --redact=100 --no-banner --exit-code=1 --report-format=json --report-path=$workingReport
    Assert-SecretScan $LASTEXITCODE $workingReport 'working tree'
    Write-Output 'Security checks passed: locked dependencies, vulnerability audit, Git history and working-tree secrets.'
}
finally {
    # Remove only the uniquely created directory under TEMP, never a computed repo path.
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($tempRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedScratch) -match '^magidesk-security-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
