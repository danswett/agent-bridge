# Runs the Windows app's self-test (`AgentBridge.exe --self-test <result>`, src/AgentBridge.App/
# SelfTest.cs) as CI does, prints its result and ends with its exit code. When the app ends any
# other way than with 0, says how: the exit code as an NTSTATUS, the end of the app's log and what
# Windows recorded (the faulting module and offset). A native crash leaves no managed exception
# and no crash log; once it showed in the job only as "exit code 1" (#240). On CI, a crash's
# minidump is kept beside the logs and its stack printed (#242).
#
#   pwsh build/app-self-test.ps1 -Exe out/app/publish/AgentBridge.exe -Result $env:RUNNER_TEMP/selftest/result.json
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Result,
    # Where the app's logs are copied; the result's folder unless given.
    [string]$Logs,
    # Arm64 CI took 146 s of 180 before #800's sign-in captures (every state in Light and Dark, about
    # 30 s, and again in high contrast on CI, about 25 s) added about 55 s, and the progress gallery
    # (ui.md §6.5) adds four passes of about 25 s each; the live resize pass runs last and is the one
    # a tight limit cuts off (reported as unfinished).
    [int]$TimeoutSeconds = 360
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Result = [IO.Path]::GetFullPath($Result)
if (-not $Logs) { $Logs = Split-Path $Result }
# Absolute: WER, which writes the dumps, runs outside this process and its working folder.
$Logs = [IO.Path]::GetFullPath($Logs)
New-Item -ItemType Directory -Force (Split-Path $Result), $Logs | Out-Null
$appLog = [IO.Path]::Combine($env:LOCALAPPDATA, 'AgentBridge', 'app', 'app.log')
# A crash's minidump, kept by Windows Error Reporting outside published results: the event names no
# module for a fail fast ("unknown"), and the stack that found #240 came only from a dump. On CI's
# throwaway runner only, where HKLM is writable; never on a developer's machine.
$dumps = $null
$wer = $null
if ($env:GITHUB_ACTIONS -eq 'true') {
    $dumps = [IO.Path]::GetFullPath((Join-Path $env:RUNNER_TEMP "agent-bridge-wer-$([guid]::NewGuid().ToString('N'))"))
    for ($dir = [IO.DirectoryInfo]::new($dumps); $null -ne $dir; $dir = $dir.Parent) {
        if ($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked WER scratch directory refused'
        }
    }
    # Cleanup can be interrupted. Isolation, not deletion, keeps dumps out of artifact roots.
    foreach ($root in @($Logs, (Split-Path $Result), $env:GITHUB_WORKSPACE,
            (Join-Path $env:RUNNER_TEMP 'selftest'), (Join-Path $env:RUNNER_TEMP 'arm64-loop'))) {
        if (-not $root) { continue }
        $root = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
        if ($dumps.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $dumps.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase)) {
            throw 'WER scratch must be outside published results and workspace'
        }
    }
    $wer = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$(Split-Path $Exe -Leaf)"
    try {
        New-Item -ItemType Directory -Force $dumps | Out-Null
        New-Item $wer -Force | Out-Null
        Set-ItemProperty -Path $wer -Name DumpFolder -Value $dumps -Type ExpandString
        Set-ItemProperty -Path $wer -Name DumpType -Value 1 -Type DWord
    }
    catch { Write-Output "no crash dumps: $($_.Exception.Message)" }
    Write-Output 'WER dumps are isolated from publication inputs, including if cleanup is interrupted'
}
try {
$started = Get-Date
$stderrPath = Join-Path $Logs 'app.stderr.log'
$evidencePath = Join-Path $Logs 'app-exit-cause.json'
$p = Start-Process $Exe -ArgumentList '--self-test', "`"$Result`"" -RedirectStandardError $stderrPath -PassThru
# Held from the start: a Process from Start-Process that is first asked for its exit code after
# it has gone can report it wrongly.
$null = $p.Handle
$finished = $p.WaitForExit($TimeoutSeconds * 1000)
try { Copy-Item (Join-Path (Split-Path $appLog) '*.log') $Logs }
catch { Write-Output "::error::app log capture failed: $($_.Exception.Message)" }
if (-not $finished) { Stop-Process -Id $p.Id -Force }
$wrote = Test-Path $Result
if ($wrote) { Get-Content $Result } else { Write-Output '::error::the self-test wrote no result' }
$code = if ($finished) { $p.ExitCode } else { $null }
Write-Output "app process $($p.Id): exit code $code (finished=$finished)"
# Written part-way through before a check that can end the process (SelfTest.Write): still set,
# the app ended inside that check, whatever the exit code says. Every check before it passed.
$report = if ($wrote) { try { Get-Content $Result -Raw | ConvertFrom-Json } catch { Write-Output "::error::self-test result capture failed: $($_.Exception.Message)" | Out-Host; $null } } else { $null }
$unfinished = if ($report) { $report.unfinished } else { $null }
$evidence = [ordered]@{
    processId = $p.Id; exitCode = $code
    failed = ($null -eq $code -or $code -ne 0 -or -not $wrote -or [bool]$unfinished)
    stderr = ''; events = @(); captureErrors = @()
    selfTest = @{ unfinished = $unfinished; resizeDiagnostics = @() }
    xamlFailure = $null
}
foreach ($capture in @(
        @{ extension = '.resize-state.json'; field = 'resize state' },
        @{ extension = '.xaml-failure.json'; field = 'XAML exception' })) {
    $path = [IO.Path]::ChangeExtension($Result, $capture.extension)
    if (-not (Test-Path -LiteralPath $path)) { continue }
    try {
        $detail = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($detail.processId -ne $p.Id) { throw [IO.InvalidDataException]::new('capture does not belong to the app process') }
        if ($capture.extension -eq '.resize-state.json') { $evidence.selfTest.resizeDiagnostics = @($detail.states) }
        else { $evidence.xamlFailure = $detail }
    }
    catch {
        $evidence.captureErrors += "$($capture.field) capture failed: $($_.Exception.Message)"
        Write-Output "::error::$($evidence.captureErrors[-1])"
    }
}
try { $evidence.stderr = [IO.File]::ReadAllText($stderrPath) }
catch {
    $evidence.captureErrors += "app stderr capture failed: $($_.Exception.Message)"
    Write-Output "::error::$($evidence.captureErrors[-1])"
}
Write-Output '--- app stderr'
Write-Output $evidence.stderr
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath
# Ended with 0 having written nothing, or with a check never finished: it did not pass.
if (-not $evidence.failed) { exit 0 }

if ($null -eq $evidence.xamlFailure) { Write-Output '--- no managed XAML exception captured; a native fail-fast may bypass the event; cause remains unproven' }
else { Write-Output ($evidence.xamlFailure | ConvertTo-Json -Depth 6) }
Write-Output ($evidence.selfTest | ConvertTo-Json -Depth 6)
if ($null -eq $code) { Write-Output "::error::the self-test did not finish in $TimeoutSeconds s" }
elseif ($code -ne 0) {
    $hex = '0x{0:X8}' -f $code
    $known = @{ '0xC0000005' = 'access violation'; '0xC0000409' = 'fail fast (stack buffer overrun or CFG check)'; '0xC000027B' = 'stowed exception (a XAML or WinRT failure)'; '0xE0434352' = 'unhandled .NET exception' }
    $what = if ($known.ContainsKey($hex)) { " ($($known[$hex]))" } else { '' }
    Write-Output "::error::the app ended with exit code $code, $hex$what"
}
if ($unfinished) { Write-Output "::error::the self-test ended during its '$unfinished' check, which never reported: the app did not get as far as saying whether it passed" }
Write-Output '--- the end of the app''s log'
try { Get-Content $appLog -Tail 30 }
catch { Write-Output "::error::app log read failed: $($_.Exception.Message)" }
Write-Output '--- what Windows recorded'
# The crash's events come a moment after the process ends.
Start-Sleep -Seconds 3
try {
    $events = @(Get-WinEvent -FilterHashtable @{
        LogName = 'Application'; StartTime = $started; EndTime = (Get-Date)
        ProviderName = @('Application Error', 'Windows Error Reporting', '.NET Runtime')
    } -ErrorAction Stop)
    foreach ($event in $events) {
        $xml = [xml]$event.ToXml()
        # Application Error's Execution PID belongs to the reporter, not the faulting app.
        $pidNode = $xml.SelectSingleNode("//*[local-name()='EventData']/*[local-name()='Data'][@Name='ProcessId' or @Name='ProcessID']")
        $eventPid = $null
        if ($null -ne $pidNode) {
            $value = $pidNode.InnerText
            $eventPid = if ($value -like '0x*') { [Convert]::ToUInt32($value.Substring(2), 16) } else { [uint32]$value }
        }
        elseif ($event.ProviderName -eq '.NET Runtime') {
            $execution = $xml.SelectSingleNode("/*[local-name()='Event']/*[local-name()='System']/*[local-name()='Execution']")
            if ($null -ne $execution) { $eventPid = [uint32]$execution.GetAttribute('ProcessID') }
        }
        # No PID means no attribution: a WER/app-name match alone could be Setup or the host.
        if ($eventPid -ne $p.Id) { continue }
        $message = $event.Message
        if ($message -notmatch [regex]::Escape((Split-Path $Exe -Leaf))) { continue }
        $time = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId($event.TimeCreated, 'Pacific Standard Time').ToString('yyyy-MM-dd HH:mm:ss')
        $evidence.events += @{
            processId = $eventPid; provider = $event.ProviderName; eventId = $event.Id
            recordId = $event.RecordId; timePacific = $time; message = $message
        }
        Write-Output "[$time Pacific] app PID $eventPid, $($event.ProviderName) $($event.Id): $message"
    }
}
catch {
    $evidence.captureErrors += "app-process event capture failed: $($_.FullyQualifiedErrorId): $($_.Exception.Message)"
    Write-Output "::error::$($evidence.captureErrors[-1])"
}
if ($evidence.events.Count -eq 0) { Write-Output '::error::no Windows event cause correlated to the app process was captured' }
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath
# The crashing thread's stack from the dump, by the runner's own cdb when it has one (the Windows
# SDK's Debugging Tools), with Microsoft's public symbols. Bounded: a slow symbol server costs at
# most two minutes of a run that has already failed.
$dump = if ($dumps) { Get-ChildItem $dumps -Filter *.dmp -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started } | Select-Object -First 1 }
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$cdb = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Debuggers\$arch\cdb.exe"
if ($dump -and (Test-Path $cdb)) {
    Write-Output "--- the crash, from $($dump.Name)"
    $symbols = Join-Path $dumps 'symbols'
    $analysis = Join-Path $Logs 'app-crash-analysis.txt'
    try {
        $cdbRun = Start-Process $cdb -ArgumentList '-z', "`"$($dump.FullName)`"", '-y', "`"srv*$symbols*https://msdl.microsoft.com/download/symbols`"", '-c', '".exr -1; .ecxr; k; !analyze -v; q"' -RedirectStandardOutput $analysis -NoNewWindow -PassThru
        if (-not $cdbRun.WaitForExit(120000)) { Stop-Process -Id $cdbRun.Id -Force; Write-Output 'cdb took too long; only partial text analysis may be published' }
        $text = @(Get-Content $analysis -ErrorAction SilentlyContinue)
        $text | Where-Object { $_ -match '^(FAILURE_BUCKET_ID|SYMBOL_NAME|EXCEPTION_CODE_STR|IMAGE_NAME):' }
        $stack = [Array]::IndexOf($text, ($text | Where-Object { $_ -like 'STACK_TEXT:*' } | Select-Object -First 1))
        if ($stack -ge 0) { $text | Select-Object -Skip ($stack + 1) -First 24 | Where-Object { $_ -match '\S' } | ForEach-Object { ($_ -split ' : ')[-1] } }
    }
    catch { Write-Output "cdb did not run: $($_.Exception.Message)" }
    Remove-Item $symbols -Recurse -Force -ErrorAction SilentlyContinue
}
elseif ($dump) { Write-Output "--- no debugger available; the process dump will not be published" }
}
finally {
    try {
        if ($wer -and (Test-Path -LiteralPath $wer) -and
            (Get-ItemPropertyValue -Path $wer -Name DumpFolder -ErrorAction Stop) -eq $dumps) {
            Remove-Item -LiteralPath $wer -Force -ErrorAction Stop
        }
    }
    catch { Write-Warning 'WER registration cleanup failed; dump scratch remains outside publication inputs' }
    try {
        if ($dumps -and (Test-Path -LiteralPath $dumps)) {
            Remove-Item -LiteralPath $dumps -Recurse -Force -ErrorAction Stop
        }
    }
    catch { Write-Warning 'WER dump cleanup failed; isolated scratch must never be published' }
}
exit $(if ($null -eq $code -or $code -eq 0) { 1 } else { $code })
