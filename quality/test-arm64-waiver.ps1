# Offline checks for quality/arm64-waiver.ps1: fixed needs and jobs JSON, an injected clock and
# switch, no GitHub. A check reads the ids the script waives; none means the gate stays red.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'arm64-waiver.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("arm64-waiver-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$fail = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ }
}
function Leg([string]$name, $conclusion) { @{ name = $name; conclusion = $conclusion; html_url = "https://example.test/$name" } }
function Pages($legs, $total = $legs.Count) { ConvertTo-Json -InputObject @(@{ total_count = $total; jobs = @($legs) }) -Depth 5 }
function Waived($needs, [string]$jobsJson, [string]$setting = 'on', [string]$now = '2026-11-13T22:00:00Z') {
    $n = Join-Path $tmp 'needs.json'; $j = Join-Path $tmp 'jobs.json'; $o = Join-Path $tmp 'out.json'
    Remove-Item $o -ErrorAction SilentlyContinue
    ConvertTo-Json -InputObject $needs -Depth 5 | Set-Content $n
    if ($jobsJson) { Set-Content $j $jobsJson } else { Remove-Item $j -ErrorAction SilentlyContinue }
    $a = @('-NeedsFile', $n, '-JobsFile', $j, '-OutFile', $o, '-Now', $now)
    if ($setting) { $a += '-Setting', $setting }
    pwsh -NoProfile -File $script @a | Out-Null
    (@(Get-Content -Raw $o | ConvertFrom-Json) -join ',')
}
$appFailed = @{ app = @{ result = 'failure' } }
$arm64Red = Pages @((Leg 'app (win-x64)' 'success'), (Leg 'app (win-arm64)' 'failure'))
try {
    Check 'a failed app (win-arm64) is waived while the switch is on and before expiry' ((Waived $appFailed $arm64Red) -eq 'app')
    Check 'a failed product (windows-11-arm) is waived' ((Waived @{ product = @{ result = 'failure' } } (Pages @((Leg 'product (ubuntu-latest)' 'success'), (Leg 'product (windows-11-arm)' 'failure')))) -eq 'product')
    $all = Pages @((Leg 'app (win-arm64)' 'failure'), (Leg 'app-installer (win-arm64)' 'failure'), (Leg 'app-installer (win-x64)' 'success'))
    Check 'app-installer (win-arm64) is waived and only the job that failed' ((Waived ([ordered]@{ app = @{ result = 'failure' }; 'app-installer' = @{ result = 'failure' } }) $all) -eq 'app,app-installer')
    Check 'a job id is not matched by another job whose name starts with it' ((Waived @{ app = @{ result = 'failure' } } (Pages @((Leg 'app-installer (win-arm64)' 'failure')))) -eq '')

    Check 'one second before 2026-11-20T08:00:00Z it still waives' ((Waived $appFailed $arm64Red 'on' '2026-11-20T07:59:59Z') -eq 'app')
    Check 'at exactly 2026-11-20T08:00:00Z it waives nothing' ((Waived $appFailed $arm64Red 'on' '2026-11-20T08:00:00Z') -eq '')
    Check 'a local-offset clock past the expiry waives nothing' ((Waived $appFailed $arm64Red 'on' '2026-11-20T00:00:01-08:00') -eq '')
    foreach ($setting in '', 'off', 'ON', 'on ', 'true') {
        Check "switch '$setting' waives nothing" ((Waived $appFailed $arm64Red $setting) -eq '')
    }

    Check 'a failed app (win-x64) is never waived' ((Waived $appFailed (Pages @((Leg 'app (win-x64)' 'failure'), (Leg 'app (win-arm64)' 'success')))) -eq '')
    Check 'an arm64 failure beside an x64 failure waives nothing' ((Waived $appFailed (Pages @((Leg 'app (win-x64)' 'failure'), (Leg 'app (win-arm64)' 'failure')))) -eq '')
    Check 'any other failed job waives nothing, even a nameless one' ((Waived $appFailed (Pages @((Leg 'app (win-arm64)' 'failure'), (Leg '' 'failure')))) -eq '')
    Check 'a failed light job beside the arm64 failure waives nothing' ((Waived @{ app = @{ result = 'failure' }; light = @{ result = 'failure' } } (Pages @((Leg 'app (win-arm64)' 'failure'), (Leg 'light' 'failure')))) -eq '')
    foreach ($conclusion in 'cancelled', 'timed_out', 'skipped') {
        Check "a waived leg that is $conclusion is not waived" ((Waived $appFailed (Pages @((Leg 'app (win-x64)' 'success'), (Leg 'app (win-arm64)' $conclusion)))) -eq '')
    }
    Check 'a renamed leg is not waived' ((Waived $appFailed (Pages @((Leg 'app (win-arm64 )' 'failure')))) -eq '')
    foreach ($sibling in 'skipped', 'neutral', $null, 'cancelled', 'timed_out') {
        Check "arm64 failed beside an app (win-x64) leg that is '$sibling' waives nothing" ((Waived $appFailed (Pages @((Leg 'app (win-x64)' $sibling), (Leg 'app (win-arm64)' 'failure')))) -eq '')
    }
    Check 'app-installer (win-arm64) failed beside a skipped x64 leg waives nothing' ((Waived @{ 'app-installer' = @{ result = 'failure' } } (Pages @((Leg 'app-installer (win-x64)' 'skipped'), (Leg 'app-installer (win-arm64)' 'failure')))) -eq '')
    Check 'product (windows-11-arm) failed beside a skipped or unfinished leg waives nothing' ((Waived @{ product = @{ result = 'failure' } } (Pages @((Leg 'product (ubuntu-latest)' 'skipped'), (Leg 'product (windows-latest)' $null), (Leg 'product (windows-11-arm)' 'failure')))) -eq '')
    Check 'only the job whose other legs all succeeded is waived' ((Waived ([ordered]@{ app = @{ result = 'failure' }; 'app-installer' = @{ result = 'failure' } }) (Pages @((Leg 'app (win-x64)' 'success'), (Leg 'app (win-arm64)' 'failure'), (Leg 'app-installer (win-x64)' 'skipped'), (Leg 'app-installer (win-arm64)' 'failure')))) -eq 'app')
    Check 'a cancelled app is not downgraded' ((Waived @{ app = @{ result = 'cancelled' } } $arm64Red) -eq '')

    Check 'a missing jobs file waives nothing' ((Waived $appFailed '') -eq '')
    Check 'an unparseable answer waives nothing' ((Waived $appFailed 'not json') -eq '')
    Check 'an answer without a jobs list waives nothing' ((Waived $appFailed '{"message":"Resource not accessible"}') -eq '')
    Check 'a partial job list waives nothing' ((Waived $appFailed (Pages @((Leg 'app (win-arm64)' 'failure')) 2)) -eq '')

    $ci = Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'ci.yml')
    $code = Get-Content -Raw $script
    foreach ($name in 'app (win-arm64)', 'app-installer (win-arm64)', 'product (windows-11-arm)') {
        Check "the waived name '$name' is in the script" ($code.Contains("'$name'"))
    }
    Check 'ci.yml still names the legs the script waives' ($ci.Contains('name: app (${{ matrix.runtime }})') -and $ci.Contains('name: app-installer (${{ matrix.runtime }})') -and $ci.Contains('runtime: win-arm64') -and $ci.Contains('"windows-11-arm"'))
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail) { throw "$fail check(s) failed" }
'all arm64-waiver checks passed'
