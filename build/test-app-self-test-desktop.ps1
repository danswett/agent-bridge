# A win-arm64 self-test retry after a crash (#2026, PR 3239), against fake desktop boundaries:
# nothing here reads or changes Windows' high contrast or stops a real process.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'app-self-test-desktop.ps1')
function Get-SelfTestHighContrast { $script:hc }
function Set-SelfTestHighContrast([uint32]$Flags) { $script:sets++; if ($script:setWorks) { $script:hc = $Flags } }
function Get-SelfTestAppProcess([string]$Folder) { @($script:left) }
function Stop-SelfTestAppProcess($Process) { if ($script:stopWorks) { $script:left = @($script:left | Where-Object { $_ -ne $Process }) } }
# The app's pass as SelfTest.cs's HighContrastPass runs it: found on already, nothing is turned on, so
# neither the transition nor the switch back off is checked. A crash in the pass skips EndAsync.
function Invoke-FakeApp([switch]$Crash) {
    $turnedOn = -not ($script:hc -band 1)
    if ($turnedOn) { $script:hc = $script:hc -bxor 1 }
    if ($Crash) { $script:left += 'AgentBridge.exe'; return $turnedOn }
    if ($turnedOn) { $script:hc = $script:hc -bxor 1 }
    $turnedOn
}
$state = Join-Path ([IO.Path]::GetTempPath()) "selftest-desktop-$([guid]::NewGuid().ToString('N')).json"
$exe = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'publish') 'AgentBridge.exe'
function Reset-Fake([bool]$SetWorks = $true, [bool]$StopWorks = $true) {
    $script:hc = 2; $script:left = @(); $script:sets = 0
    $script:setWorks = $SetWorks; $script:stopWorks = $StopWorks
    Remove-Item -LiteralPath $state -ErrorAction SilentlyContinue
}
function Invoke-Retry {
    try { Enter-SelfTestDesktop -State $state -Exe $exe | Out-Host; $true }
    catch { Write-Host $_.Exception.Message; $false }
}
$failed = @()
function Test-Case([bool]$Ok, [string]$Name) {
    Write-Host "$(if ($Ok) { 'PASS' } else { 'FAIL' }) $Name"
    if (-not $Ok) { $script:failed += $Name }
}
try {
    Reset-Fake
    Enter-SelfTestDesktop -State $state -Exe $exe | Out-Host
    Test-Case (Invoke-FakeApp -Crash) 'the first attempt turns high contrast on for its pass, then crashes in it'
    $started = Invoke-Retry
    Test-Case ($started -and $script:hc -eq 2 -and $script:left.Count -eq 0) 'a retry after that crash starts with high contrast put back and no app left running'
    Test-Case ($started -and (Invoke-FakeApp)) 'the retry turns high contrast on itself, so the transition and switch-off are checked again'

    Reset-Fake -SetWorks $false
    Enter-SelfTestDesktop -State $state -Exe $exe | Out-Host
    $null = Invoke-FakeApp -Crash
    Test-Case (-not (Invoke-Retry)) 'a retry refuses to start when high contrast cannot be put back'

    Reset-Fake -StopWorks $false
    Enter-SelfTestDesktop -State $state -Exe $exe | Out-Host
    $null = Invoke-FakeApp -Crash
    Test-Case (-not (Invoke-Retry)) 'a retry refuses to start while the crashed app is still running'

    Reset-Fake
    Enter-SelfTestDesktop -State $state -Exe $exe | Out-Host
    $null = Invoke-FakeApp
    Test-Case ((Invoke-Retry) -and $script:sets -eq 0) 'an attempt after a clean one starts without changing anything'

    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'app-self-test.ps1') -Raw
    $enter = $source.IndexOf('Enter-SelfTestDesktop', [StringComparison]::Ordinal)
    Test-Case ($enter -ge 0 -and $enter -lt $source.IndexOf('Start-Process $Exe', [StringComparison]::Ordinal)) 'app-self-test.ps1 checks the desktop before it starts the app'
}
finally { Remove-Item -LiteralPath $state -ErrorAction SilentlyContinue }
if ($failed.Count) { throw "$($failed.Count) desktop check(s) failed" }
