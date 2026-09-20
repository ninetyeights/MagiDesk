param(
    [string]$TraceTool = "$env:TEMP\MagiDesk-ProfilingTools\dotnet-trace.exe",
    [string]$OutputDirectory = "$env:TEMP\MagiDesk-StartupTrace"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$statusPath = Join-Path $OutputDirectory 'status.txt'
try {
    if (!(Test-Path -LiteralPath $TraceTool)) { throw 'dotnet-trace is not installed.' }
    $existing = @(Get-CimInstance Win32_Process -Filter "Name = 'MagiDesk.exe'" | ForEach-Object ProcessId)
    'Waiting for a new MagiDesk main process. Timeout: 15 minutes.' | Set-Content $statusPath
    $deadline = [DateTime]::UtcNow.AddMinutes(15)
    $target = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $target = Get-CimInstance Win32_Process -Filter "Name = 'MagiDesk.exe'" |
            Where-Object { $_.ProcessId -notin $existing -and $_.CommandLine -and $_.CommandLine -notmatch '--desktop-' } |
            Sort-Object CreationDate | Select-Object -First 1
        if ($target) { break }
        Start-Sleep -Milliseconds 250
    }
    if (!$target) { throw 'Timed out waiting for a new main process.' }
    $tracePath = Join-Path $OutputDirectory ("startup-{0}-{1}.nettrace" -f $target.ProcessId, (Get-Date -Format 'yyyyMMdd-HHmmss'))
    "Collecting 20 seconds from PID $($target.ProcessId). Output: $tracePath" | Set-Content $statusPath
    & $TraceTool collect --process-id $target.ProcessId --profile dotnet-sampled-thread-time,dotnet-common --buffersize 64 --duration 00:00:00:20 --format Speedscope --output $tracePath *> (Join-Path $OutputDirectory 'collector.log')
    if ($LASTEXITCODE -ne 0) { throw "Collector exit code: $LASTEXITCODE. See collector.log." }
    "Completed. PID $($target.ProcessId). Output: $tracePath" | Set-Content $statusPath
}
catch {
    "Failed: $($_.Exception.Message)" | Set-Content $statusPath
    exit 1
}
