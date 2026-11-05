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
    # 30 s, and again in high contrast on CI, about 25 s) added about 55 s; the live resize pass runs
    # last and is the one a tight limit cuts off (reported as unfinished).
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Result = [IO.Path]::GetFullPath($Result)
if (-not $Logs) { $Logs = Split-Path $Result }
# Absolute: WER, which writes the dumps, runs outside this process and its working folder.
$Logs = [IO.Path]::GetFullPath($Logs)
New-Item -ItemType Directory -Force (Split-Path $Result), $Logs | Out-Null
$appLog = [IO.Path]::Combine($env:LOCALAPPDATA, 'AgentBridge', 'app', 'app.log')
# A crash's minidump, kept by Windows Error Reporting beside the logs: the event alone names no
# module for a fail fast ("unknown"), and the stack that found #240 came only from a dump. On CI's
# throwaway runner only, where HKLM is writable; never on a developer's machine.
$dumps = Join-Path $Logs 'dumps'
if ($env:GITHUB_ACTIONS -eq 'true') {
    $wer = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$(Split-Path $Exe -Leaf)"
    try {
        New-Item -ItemType Directory -Force $dumps | Out-Null
        New-Item $wer -Force | Out-Null
        Set-ItemProperty -Path $wer -Name DumpFolder -Value $dumps -Type ExpandString
        Set-ItemProperty -Path $wer -Name DumpType -Value 1 -Type DWord
    }
    catch { Write-Output "no crash dumps: $($_.Exception.Message)" }
}
$started = Get-Date
$p = Start-Process $Exe -ArgumentList '--self-test', "`"$Result`"" -PassThru
# Held from the start: a Process from Start-Process that is first asked for its exit code after
# it has gone can report it wrongly.
$null = $p.Handle
$finished = $p.WaitForExit($TimeoutSeconds * 1000)
Copy-Item (Join-Path (Split-Path $appLog) '*.log') $Logs -ErrorAction SilentlyContinue
if (-not $finished) { Stop-Process -Id $p.Id -Force }
$wrote = Test-Path $Result
if ($wrote) { Get-Content $Result } else { Write-Output '::error::the self-test wrote no result' }
$code = if ($finished) { $p.ExitCode } else { $null }
# Written part-way through before a check that can end the process (SelfTest.Write): still set,
# the app ended inside that check, whatever the exit code says. Every check before it passed.
$unfinished = if ($wrote) { try { (Get-Content $Result -Raw | ConvertFrom-Json).unfinished } catch { $null } } else { $null }
# Ended with 0 having written nothing, or with a check never finished: it did not pass.
if ($code -eq 0 -and -not $unfinished) { exit $(if ($wrote) { 0 } else { 1 }) }

if ($null -eq $code) { Write-Output "::error::the self-test did not finish in $TimeoutSeconds s" }
elseif ($code -ne 0) {
    $hex = '0x{0:X8}' -f $code
    $known = @{ '0xC0000005' = 'access violation'; '0xC0000409' = 'fail fast (stack buffer overrun or CFG check)'; '0xC000027B' = 'stowed exception (a XAML or WinRT failure)'; '0xE0434352' = 'unhandled .NET exception' }
    $what = if ($known.ContainsKey($hex)) { " ($($known[$hex]))" } else { '' }
    Write-Output "::error::the app ended with exit code $code, $hex$what"
}
if ($unfinished) { Write-Output "::error::the self-test ended during its '$unfinished' check, which never reported: the app did not get as far as saying whether it passed" }
Write-Output '--- the end of the app''s log'
Get-Content $appLog -Tail 30 -ErrorAction SilentlyContinue
Write-Output '--- what Windows recorded'
# The crash's events come a moment after the process ends.
Start-Sleep -Seconds 3
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in 'Application Error', 'Windows Error Reporting', '.NET Runtime' -and $_.Message -match 'AgentBridge' } |
    ForEach-Object { "[$([TimeZoneInfo]::ConvertTimeBySystemTimeZoneId($_.TimeCreated, 'Pacific Standard Time').ToString('yyyy-MM-dd HH:mm:ss')) Pacific] $($_.ProviderName) $($_.Id): $(($_.Message -split "`r?`n" | Where-Object { $_ -match '\S' } | Select-Object -First 12) -join ' | ')" }
# The crashing thread's stack from the dump, by the runner's own cdb when it has one (the Windows
# SDK's Debugging Tools), with Microsoft's public symbols. Bounded: a slow symbol server costs at
# most two minutes of a run that has already failed.
$dump = Get-ChildItem $dumps -Filter *.dmp -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started } | Select-Object -First 1
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$cdb = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Debuggers\$arch\cdb.exe"
if ($dump -and (Test-Path $cdb)) {
    Write-Output "--- the crash, from $($dump.Name)"
    $symbols = Join-Path $Logs 'symbols'
    $analysis = Join-Path $Logs "$($dump.BaseName).txt"
    try {
        $cdbRun = Start-Process $cdb -ArgumentList '-z', "`"$($dump.FullName)`"", '-y', "`"srv*$symbols*https://msdl.microsoft.com/download/symbols`"", '-c', '"!analyze -v; q"' -RedirectStandardOutput $analysis -NoNewWindow -PassThru
        if (-not $cdbRun.WaitForExit(120000)) { Stop-Process -Id $cdbRun.Id -Force; Write-Output 'cdb took too long; the dump is in the artifact' }
        $text = @(Get-Content $analysis -ErrorAction SilentlyContinue)
        $text | Where-Object { $_ -match '^(FAILURE_BUCKET_ID|SYMBOL_NAME|EXCEPTION_CODE_STR|IMAGE_NAME):' }
        $stack = [Array]::IndexOf($text, ($text | Where-Object { $_ -like 'STACK_TEXT:*' } | Select-Object -First 1))
        if ($stack -ge 0) { $text | Select-Object -Skip ($stack + 1) -First 24 | Where-Object { $_ -match '\S' } | ForEach-Object { ($_ -split ' : ')[-1] } }
    }
    catch { Write-Output "cdb did not run: $($_.Exception.Message)" }
    Remove-Item $symbols -Recurse -Force -ErrorAction SilentlyContinue
}
elseif ($dump) { Write-Output "--- the crash's dump is in the artifact: $($dump.Name)" }
exit $(if ($null -eq $code -or $code -eq 0) { 1 } else { $code })
