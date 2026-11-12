#Requires -Version 7.4
<#
.SYNOPSIS
  Read-only process baseline for #2294; schema and limitations: efficiency-baseline.md.
.EXAMPLE
  pwsh quality/efficiency-baseline.ps1 -DurationSeconds 30 -SampleIntervalSeconds 2 -OutputPath out/baseline.json
#>
[CmdletBinding()]
param(
    [ValidateRange(0.01, 86400)][double] $DurationSeconds = 30,
    [ValidateRange(0.01, 3600)][double] $SampleIntervalSeconds = 2,
    [string] $OutputPath,
    [ValidateRange(0, 2147483647)][int] $HostProcessId = 0,
    [ValidateRange(0, 2147483647)][int] $DesktopProcessId = 0
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-EfficiencySnapshot([int] $ProcessId) {
    $process = $null
    try {
        $process = [Diagnostics.Process]::GetProcessById($ProcessId)
        $result = @{
            identity = "$ProcessId/$($process.StartTime.ToUniversalTime().Ticks)"
            cpuSeconds = $process.TotalProcessorTime.TotalSeconds
            workingSetBytes = $process.WorkingSet64; privateBytes = $process.PrivateMemorySize64
            buildVersion = $null; threads = $null; unavailable = @{}
        }
        try { $result.buildVersion = $process.MainModule.FileVersionInfo.ProductVersion }
        catch [ComponentModel.Win32Exception], [InvalidOperationException], [NotSupportedException] {
            $result.unavailable.buildVersion = $_.Exception.Message
        }
        if ([string]::IsNullOrWhiteSpace($result.buildVersion)) { $result.unavailable.buildVersion = 'Executable has no readable product version.' }
        try {
            $result.threads = @(
                foreach ($thread in $process.Threads) {
                    try {
                        @{ id = $thread.Id; identity = "$($thread.Id)/$($thread.StartTime.ToUniversalTime().Ticks)"
                            cpuSeconds = $thread.TotalProcessorTime.TotalSeconds }
                    } finally { $thread.Dispose() }
                }
            )
        } catch [ComponentModel.Win32Exception], [InvalidOperationException], [NotSupportedException] {
            $result.threads = $null
            $result.unavailable.topThreads = "Thread counters unavailable: $($_.Exception.Message)"
        }
        return $result
    } catch [ArgumentException], [ComponentModel.Win32Exception], [InvalidOperationException], [NotSupportedException] {
        return @{ unavailable = @{ process = "Process unavailable: $($_.Exception.Message)" } }
    } finally { if ($null -ne $process) { $process.Dispose() } }
}

function ConvertTo-EfficiencyTarget($ProcessId, [object[]] $Samples) {
    $result = [ordered]@{
        status = 'unavailable'; processId = $ProcessId; buildVersion = $null; sampleCount = 0
        cpuSecondsPerMinute = $null; workingSetBytes = $null; privateBytes = $null; topThreads = $null
        gcAllocationBytesPerSecond = $null; network = $null
        unavailable = @{
            gcAllocationBytesPerSecond = 'No EventPipe/dotnet-counters collector in this process-only slice.'
            network = 'No per-process ETW/proxy or browser HAR/CDP collector in this process-only slice.'
        }
    }
    if (-not $ProcessId) {
        $result.unavailable.process = 'Target not found, or browser target requires a separate collector.'
        return $result
    }
    $valid = @($Samples | Where-Object { -not $_.data.unavailable.ContainsKey('process') })
    $result.sampleCount = $valid.Count
    if ($valid.Count -ne $Samples.Count) {
        $result.status = if ($valid.Count) { 'partial' } else { 'unavailable' }
        $result.unavailable.process = ($Samples | Where-Object { $_.data.unavailable.ContainsKey('process') } | Select-Object -First 1).data.unavailable.process
        return $result
    }
    if ($valid.Count -lt 2 -or @($valid.data.identity | Sort-Object -Unique).Count -ne 1) {
        $result.unavailable.process = 'Insufficient samples, or process identity changed during observation.'
        return $result
    }
    $first = $valid[0]; $last = $valid[-1]
    $seconds = $last.elapsedSeconds - $first.elapsedSeconds
    $cpu = $last.data.cpuSeconds - $first.data.cpuSeconds
    if ($seconds -le 0 -or $cpu -lt 0) {
        $result.unavailable.process = 'Elapsed time or CPU counters moved backwards.'
        return $result
    }
    $result.status = 'available'
    $result.cpuSecondsPerMinute = $cpu * 60 / $seconds
    $result.workingSetBytes = $last.data.workingSetBytes
    $result.privateBytes = $last.data.privateBytes
    $result.buildVersion = $last.data.buildVersion
    foreach ($key in $last.data.unavailable.Keys) { $result.unavailable[$key] = $last.data.unavailable[$key] }
    if ($null -eq $first.data.threads -or $null -eq $last.data.threads) {
        $result.unavailable.topThreads = 'Thread counters unavailable at an interval endpoint.'
    } else {
        $before = @{}
        foreach ($thread in $first.data.threads) { $before[$thread.identity] = $thread.cpuSeconds }
        $result.topThreads = @(
            @(foreach ($thread in $last.data.threads) {
                if ($before.ContainsKey($thread.identity) -and $thread.cpuSeconds -ge $before[$thread.identity]) {
                    @{ id = $thread.id; cpuSeconds = $thread.cpuSeconds - $before[$thread.identity] }
                }
            }) | Sort-Object { $_.cpuSeconds } -Descending | Select-Object -First 10
        )
    }
    return $result
}

function Invoke-EfficiencyBaseline {
    param(
        [ValidateRange(0.01, 86400)][double] $DurationSeconds,
        [ValidateRange(0.01, 3600)][double] $SampleIntervalSeconds,
        [Parameter(Mandatory)][hashtable] $Targets,
        [scriptblock] $ReadSnapshot = { param($processId) Get-EfficiencySnapshot $processId },
        [scriptblock] $Elapsed = { $watch.Elapsed.TotalSeconds },
        [scriptblock] $Wait = { param($seconds) Start-Sleep -Seconds $seconds },
        [scriptblock] $UtcNow = { [datetime]::UtcNow }
    )
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $startUtc = & $UtcNow
    $samples = @{}
    foreach ($name in 'host', 'desktop', 'pwa') { $samples[$name] = [Collections.Generic.List[object]]::new() }
    $count = 0
    while ($true) {
        foreach ($name in 'host', 'desktop', 'pwa') {
            if ($Targets[$name]) {
                $data = & $ReadSnapshot $Targets[$name]
                $samples[$name].Add(@{ elapsedSeconds = [double](& $Elapsed); data = $data })
            }
        }
        $count++
        $elapsedSeconds = [double](& $Elapsed)
        if ($elapsedSeconds -ge $DurationSeconds) { break }
        & $Wait ([math]::Min($SampleIntervalSeconds, $DurationSeconds - $elapsedSeconds))
    }
    $endUtc = & $UtcNow
    $targetsResult = [ordered]@{}
    foreach ($name in 'host', 'desktop', 'pwa') {
        $targetsResult[$name] = ConvertTo-EfficiencyTarget $Targets[$name] $samples[$name].ToArray()
    }
    return [ordered]@{
        schemaVersion = 1; machine = [Environment]::MachineName; os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        startUtc = $startUtc.ToUniversalTime().ToString('o'); endUtc = $endUtc.ToUniversalTime().ToString('o')
        elapsedSeconds = $elapsedSeconds; sampleCount = $count; targets = $targetsResult
    }
}

function Resolve-EfficiencyProcess([int] $ProcessId, [string] $Name) {
    if ($ProcessId) { return $ProcessId }
    $found = [Diagnostics.Process]::GetProcessesByName($Name)
    try {
        if ($found.Count -gt 1) { throw "Multiple $Name processes; specify the target process ID explicitly." }
        if ($found.Count -eq 1) { return $found[0].Id }
        return $null
    } finally { foreach ($process in $found) { $process.Dispose() } }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { throw '-OutputPath is required.' }
    $targets = @{
        host = Resolve-EfficiencyProcess $HostProcessId 'agent-bridge-host'
        desktop = Resolve-EfficiencyProcess $DesktopProcessId 'AgentBridge'
        pwa = $null
    }
    $result = Invoke-EfficiencyBaseline -DurationSeconds $DurationSeconds -SampleIntervalSeconds $SampleIntervalSeconds -Targets $targets
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path)
    $json = $result | ConvertTo-Json -Depth 20
    if (-not (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json'))) { throw 'Baseline failed schema validation.' }
    Set-Content -LiteralPath $path -Value $json -Encoding utf8
}
