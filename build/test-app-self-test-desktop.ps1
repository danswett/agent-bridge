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
$checks = 0
function Test-Case([bool]$Ok, [string]$Name) {
    $script:checks++
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
$fixture = Join-Path ([IO.Path]::GetTempPath()) "selftest-text-$([guid]::NewGuid().ToString('N'))"
try {
    foreach ($attempt in 'selftest', 'selftest-retry') {
        $folder = Join-Path $fixture $attempt
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        foreach ($name in 'result.json', 'app-exit-cause.json', 'app.stderr.log', 'app.log', 'app-crash-analysis.txt') {
            $text = if ($name.EndsWith('.json')) { "{`"attempt`":`"$attempt`"}" } else { "$attempt diagnostic" }
            Set-Content -LiteralPath (Join-Path $folder $name) -Value $text -NoNewline
        }
        Set-Content -LiteralPath (Join-Path $folder 'private.dmp') -Value 'MDMP private'
        Set-Content -LiteralPath (Join-Path $folder 'private.zip') -Value 'private archive'
    }
    $stage = Join-Path $PSScriptRoot 'stage-app-self-test.ps1'
    $stageError = $null
    try { & $stage -Root $fixture } catch { $stageError = $_.Exception.Message }
    foreach ($attempt in 'selftest', 'selftest-retry') {
        $out = Join-Path $fixture 'public-selftest'
        if ($attempt -eq 'selftest-retry') { $out = Join-Path $out 'retry' }
        $files = @(Get-ChildItem -LiteralPath $out -File -ErrorAction SilentlyContinue)
        $matching = @($files | Where-Object {
            $expected = if ($_.Name.EndsWith('.json')) { "{`"attempt`":`"$attempt`"}" } else { "$attempt diagnostic" }
            [string]::Equals((Get-Content -LiteralPath $_.FullName -Raw), $expected, [StringComparison]::Ordinal)
        })
        Test-Case ($null -eq $stageError -and $files.Count -eq 5 -and $matching.Count -eq 5) "$attempt stages its own five named text files, never dumps or archives ($stageError)"
    }
    Remove-Item -LiteralPath (Join-Path $fixture 'public-selftest') -Recurse -ErrorAction SilentlyContinue
    Set-Content -LiteralPath (Join-Path (Join-Path $fixture 'selftest-retry') 'app.log') -Value "bad$([char]0)binary"
    $refused = $false
    try { & $stage -Root $fixture } catch { $refused = $_.Exception.Message -like '*Binary diagnostic input refused*' }
    Test-Case $refused 'binary text in the retry fails staging closed'
}
finally { Remove-Item -LiteralPath $fixture -Recurse }
if ($failed.Count) { throw "$($failed.Count) desktop check(s) failed" }
Write-Host "selftest desktop/text: $checks checks passed"
