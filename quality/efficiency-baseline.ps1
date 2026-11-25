#Requires -Version 7.4
<#
.SYNOPSIS
  Read-only process baseline for #2294; schema and limitations: efficiency-baseline.md.
  Writes only the requested JSON file, creating missing parent directories.
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

function ConvertTo-EfficiencyTarget($Target, [object[]] $Samples) {
    $result = [ordered]@{
        status = 'unavailable'; processId = $Target.processId; buildVersion = $null; sampleCount = 0
        cpuSecondsPerMinute = $null; workingSetBytes = $null; privateBytes = $null; topThreads = $null
        gcAllocationBytesPerSecond = $null; network = $null
        unavailable = @{
            gcAllocationBytesPerSecond = 'No EventPipe/dotnet-counters collector in this process-only slice.'
            network = 'No per-process ETW/proxy or browser HAR/CDP collector in this process-only slice.'
        }
    }
    if (-not $Target.processId) {
        $result.unavailable.process = $Target.unavailable
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
    $startUtc = $UtcNow.InvokeReturnAsIs()
    $samples = @{}
    foreach ($name in 'host', 'desktop', 'pwa') { $samples[$name] = [Collections.Generic.List[object]]::new() }
    $count = 0
    while ($true) {
        foreach ($name in 'host', 'desktop', 'pwa') {
            if ($Targets[$name].processId) {
                $data = $ReadSnapshot.InvokeReturnAsIs($Targets[$name].processId)
                $samples[$name].Add(@{ elapsedSeconds = [double]$Elapsed.InvokeReturnAsIs(); data = $data })
            }
        }
        $count++
        $elapsedSeconds = [double]$Elapsed.InvokeReturnAsIs()
        if ($elapsedSeconds -ge $DurationSeconds) { break }
        $null = $Wait.InvokeReturnAsIs([math]::Min($SampleIntervalSeconds, $DurationSeconds - $elapsedSeconds))
    }
    $endUtc = $UtcNow.InvokeReturnAsIs()
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

function Get-EfficiencyCpuVerdict {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Samples,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $ControlSamples,
        [Parameter(Mandatory)][ValidateScript({ [double]::IsFinite($_) -and $_ -ge 0 })][double] $Budget,
        [ValidateScript({ [double]::IsFinite($_) -and $_ -ge 0 })][double] $MarginFraction = 0.5,
        [ValidateScript({ [double]::IsFinite($_) -and $_ -gt 0 })][double] $MarginFloor = 0.15,
        [ValidateScript({ [double]::IsFinite($_) -and $_ -gt 0 })][double] $SpinMultiple = 20,
        [ValidateScript({ [double]::IsFinite($_) -and $_ -ge 0 })][double] $ControlCeiling = 0.1,
        [bool] $BudgetsApproved = $false,
        [bool] $QuietCheckPassed = $true,
        [object[]] $RerunSamples,
        [object[]] $RerunControlSamples,
        [bool] $RerunQuietCheckPassed = $true
    )
    function Get-CpuMedian([object[]] $Readings) {
        if ($Readings.Count -ne 3) { return $null }
        foreach ($reading in $Readings) {
            if ($reading -isnot [Collections.IDictionary] -or -not $reading.Contains('status') -or $reading.status -ne 'available' -or
                -not $reading.Contains('cpuSecondsPerMinute')) { return $null }
            $value = $reading.cpuSecondsPerMinute
            if ($value -isnot [double] -and $value -isnot [float] -and $value -isnot [decimal] -and
                $value -isnot [int] -and $value -isnot [long]) { return $null }
            if (-not [double]::IsFinite($value) -or $value -lt 0) { return $null }
        }
        return @($Readings | ForEach-Object { [double]$_.cpuSecondsPerMinute } | Sort-Object)[1]
    }
    $margin = [math]::Max($Budget * $MarginFraction, $MarginFloor)
    $ceiling = $SpinMultiple * [math]::Max($Budget, $MarginFloor)
    if (-not [double]::IsFinite($Budget + $margin) -or -not [double]::IsFinite($ceiling)) {
        throw 'CPU thresholds must be finite.'
    }
    $result = [ordered]@{
        verdict = 'not measured'; reason = ''; rerunRequired = $false; attempts = 0
        adjustedCpuSecondsPerMinute = $null; budgetComparison = 'not measured'
        budget = $Budget; margin = $margin; spinCeiling = $ceiling
    }
    $attempts = @(
        @{ samples = $Samples; control = $ControlSamples; quiet = $QuietCheckPassed }
        @{ samples = $RerunSamples; control = $RerunControlSamples; quiet = $RerunQuietCheckPassed }
    )
    for ($index = 0; $index -lt 2; $index++) {
        $attempt = $attempts[$index]; $result.attempts++
        $component = Get-CpuMedian @($attempt.samples)
        $control = Get-CpuMedian @($attempt.control)
        $result.adjustedCpuSecondsPerMinute = $null; $result.budgetComparison = 'not measured'
        if (-not $attempt.quiet) { $result.reason = 'Quiet check failed.' }
        elseif ($null -eq $component -or $null -eq $control) { $result.reason = 'Three available finite CPU samples and control samples are required.' }
        elseif ($control -gt $ControlCeiling) { $result.reason = 'The control is noisy.' }
        else {
            $adjusted = $component - $control
            $result.adjustedCpuSecondsPerMinute = $adjusted
            $result.budgetComparison = if ($adjusted -le $Budget) { 'within budget' }
                elseif ($adjusted -le $Budget + $margin) { 'within margin' } else { 'over margin' }
            if ($adjusted -gt $ceiling) {
                $result.verdict = 'fail'; $result.reason = 'Spin ceiling exceeded.'
                return $result
            }
            if ($adjusted -le $Budget + $margin) {
                $result.verdict = 'pass'; $result.reason = $result.budgetComparison
                return $result
            }
            if ($index -eq 1) {
                $result.verdict = if ($BudgetsApproved) { 'fail' } else { 'pass' }
                $result.reason = if ($BudgetsApproved) { 'Budget plus margin exceeded on the rerun.' }
                    else { 'Budget comparison is report-only until approval.' }
                return $result
            }
            $result.reason = 'Borderline CPU requires one rerun.'
        }
        if ($index -eq 0 -and $null -eq $RerunSamples -and $null -eq $RerunControlSamples) {
            $result.rerunRequired = $true
            return $result
        }
    }
    return $result
}

function Resolve-EfficiencyProcess([int] $ProcessId, [string] $Name,
    [scriptblock] $Find = { param($name) [Diagnostics.Process]::GetProcessesByName($name) }) {
    $result = @{ processId = $null; unavailable = $null }
    if ($ProcessId) { $result.processId = $ProcessId; return $result }
    $hint = if ($Name -eq 'agent-bridge-host') { '-HostProcessId' } else { '-DesktopProcessId' }
    $found = @()
    try {
        $found = @($Find.Invoke($Name))
        if ($found.Count -gt 1) { $result.unavailable = "Multiple $Name processes; specify $hint explicitly." }
        elseif ($found.Count -eq 1) { $result.processId = $found[0].Id }
        else { $result.unavailable = "No $Name process found; specify $hint if it has another name." }
    } catch [ComponentModel.Win32Exception], [InvalidOperationException], [NotSupportedException], [Management.Automation.MethodInvocationException] {
        $failure = $_.Exception.GetBaseException()
        if ($failure -isnot [ComponentModel.Win32Exception] -and $failure -isnot [InvalidOperationException] -and $failure -isnot [NotSupportedException]) { throw }
        $result.unavailable = "Process discovery unavailable: $($failure.Message) Specify $hint explicitly."
    } finally { foreach ($process in $found) { $process.Dispose() } }
    return $result
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { throw '-OutputPath is required.' }
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    $stream = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $targets = @{
            host = Resolve-EfficiencyProcess $HostProcessId 'agent-bridge-host'
            desktop = Resolve-EfficiencyProcess $DesktopProcessId 'AgentBridge'
            pwa = @{ processId = $null; unavailable = 'Browser target requires a separate collector.' }
        }
        $result = Invoke-EfficiencyBaseline -DurationSeconds $DurationSeconds -SampleIntervalSeconds $SampleIntervalSeconds -Targets $targets
        $json = $result | ConvertTo-Json -Depth 20
        if (-not (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json'))) { throw 'Baseline failed schema validation.' }
        $stream.SetLength(0)
        $bytes = [Text.Encoding]::UTF8.GetBytes($json + [Environment]::NewLine); $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}
