# Runs the Windows app's self-test (`AgentBridge.exe --self-test <result>`, src/AgentBridge.App/
# SelfTest.cs) as CI does, prints its result and ends with its exit code. When the app ends any
# other way than with 0, says how: the exit code as an NTSTATUS, the end of the app's log and what
# Windows recorded (the faulting module and offset). A native crash leaves no managed exception
# and no crash log; once it showed in the job only as "exit code 1" (#240).
#
#   pwsh build/app-self-test.ps1 -Exe out/app/publish/AgentBridge.exe -Result $env:RUNNER_TEMP/selftest/result.json
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Result,
    # Where the app's logs are copied; the result's folder unless given.
    [string]$Logs,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Result = [IO.Path]::GetFullPath($Result)
if (-not $Logs) { $Logs = Split-Path $Result }
New-Item -ItemType Directory -Force (Split-Path $Result), $Logs | Out-Null
$appLog = [IO.Path]::Combine($env:LOCALAPPDATA, 'AgentBridge', 'app', 'app.log')
$started = Get-Date
$p = Start-Process $Exe -ArgumentList '--self-test', "`"$Result`"" -PassThru
# Held from the start: a Process from Start-Process that is first asked for its exit code after
# it has gone can report it wrongly.
$null = $p.Handle
$finished = $p.WaitForExit($TimeoutSeconds * 1000)
Copy-Item (Join-Path (Split-Path $appLog) '*.log') $Logs -ErrorAction SilentlyContinue
if (-not $finished) { Stop-Process -Id $p.Id -Force }
if (Test-Path $Result) { Get-Content $Result } else { Write-Error 'the self-test wrote no result' -ErrorAction Continue }
$code = if ($finished) { $p.ExitCode } else { $null }
if ($code -eq 0) { exit 0 }

if ($null -eq $code) { Write-Output "::error::the self-test did not finish in $TimeoutSeconds s" }
else {
    $hex = '0x{0:X8}' -f $code
    $known = @{ '0xC0000005' = 'access violation'; '0xC0000409' = 'fail fast (stack buffer overrun or CFG check)'; '0xC000027B' = 'stowed exception (a XAML or WinRT failure)'; '0xE0434352' = 'unhandled .NET exception' }
    $what = if ($known.ContainsKey($hex)) { " ($($known[$hex]))" } else { '' }
    Write-Output "::error::the app ended with exit code $code, $hex$what"
}
Write-Output '--- the end of the app''s log'
Get-Content $appLog -Tail 30 -ErrorAction SilentlyContinue
Write-Output '--- what Windows recorded'
# The crash's events come a moment after the process ends.
Start-Sleep -Seconds 3
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in 'Application Error', 'Windows Error Reporting', '.NET Runtime' -and $_.Message -match 'AgentBridge' } |
    ForEach-Object { "[$($_.TimeCreated.ToString('o'))] $($_.ProviderName) $($_.Id): $(($_.Message -split "`r?`n" | Where-Object { $_ -match '\S' } | Select-Object -First 12) -join ' | ')" }
exit $(if ($null -eq $code) { 1 } else { $code })
