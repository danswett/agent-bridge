#Requires -Version 7
# quality/stale-pwa.ps1 (#4369 Fix 3, Part of #4104): the stale-PWA alarm's decision, on files, offline. Nothing here touches the network.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'stale-pwa.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("stale-pwa-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$failures = 0
$newestSha = 'a' * 40
$servedSha = 'b' * 40
$newestAt = [DateTimeOffset]::Parse('2026-11-24T10:00:00Z')

function Config([string]$version, [string]$builtAt) {
    "export const RELAY = `"https://relay.slipgates.com`";`nexport const APP_VERSION = `"$version`";`nexport const BUILT_AT = `"$builtAt`";`nexport const FEEDBACK_KEY_ID = `"`";`n"
}
function Newest([string]$sha = $newestSha, [string]$date = $newestAt.ToString('o')) { (@{ sha = $sha; date = $date } | ConvertTo-Json -Compress) }
# Run <config text or $null for no file> <newest json or $null> [extra args]; returns code, output and the step summary.
function Run($config, $newest, [string[]]$more = @()) {
    $c = Join-Path $work 'config.js'; $n = Join-Path $work 'newest.json'; $s = Join-Path $work 'summary.md'
    Remove-Item $c, $n, $s -ErrorAction SilentlyContinue
    if ($null -ne $config) { [IO.File]::WriteAllText($c, $config) }
    if ($null -ne $newest) { [IO.File]::WriteAllText($n, $newest) }
    $env:GITHUB_STEP_SUMMARY = $s
    $out = & pwsh -NoProfile -File $script -ConfigPath $c -NewestPath $n @more 2>&1 | Out-String
    $code = $LASTEXITCODE; Remove-Item Env:GITHUB_STEP_SUMMARY
    [pscustomobject]@{ Code = $code; Out = $out; Summary = $(if (Test-Path $s) { Get-Content $s -Raw } else { '' }) }
}
function Expect([string]$name, [bool]$ok, $r = $null) {
    if ($ok) { Write-Output "ok: $name" } else { Write-Output "FAIL: $name"; if ($r) { Write-Output ($r.Out.Trim()) }; $script:failures++ }
}
function Ago([double]$hours) { $newestAt.AddHours(-$hours).ToString('o') }

# fresh and within the limit
$r = Run (Config $newestSha (Ago 0)) (Newest)
Expect 'the served build is main''s newest PWA change: fine' ($r.Code -eq 0 -and $r.Out -match '::notice' -and $r.Out -match 'newest') $r
$r = Run (Config $servedSha (Ago 23.9)) (Newest)
Expect '23.9 hours behind is within the 24-hour limit' ($r.Code -eq 0 -and $r.Out -match '::notice') $r
$r = Run (Config $servedSha (Ago 24)) (Newest)
Expect 'exactly 24 hours behind is not yet stale (the limit is "more than")' ($r.Code -eq 0) $r
$r = Run (Config $servedSha (Ago 25)) (Newest)
Expect 'more than 24 hours behind FAILS loudly, naming both builds and the lag' ($r.Code -eq 1 -and $r.Out -match '::error' -and $r.Out -match $servedSha.Substring(0, 8) -and $r.Out -match $newestSha.Substring(0, 8) -and $r.Out -match '25\.0 h') $r
$r = Run (Config $servedSha ($newestAt.AddHours(-24).AddSeconds(-1).ToString('o'))) (Newest)
Expect 'a second past 24 hours is stale' ($r.Code -eq 1) $r
$r = Run (Config $servedSha (Ago -3)) (Newest)
Expect 'a served build newer than the newest PWA change (a later main commit deployed) is fine' ($r.Code -eq 0) $r
$r = Run (Config $servedSha (Ago 2)) (Newest) @('-MaxAgeHours', '1')
Expect 'the limit is a parameter' ($r.Code -eq 1) $r
$r = Run (Config $servedSha '2026-11-12T05:58:00+00:00') (Newest)
Expect 'the 12-day-old build of the incident would have alarmed' ($r.Code -eq 1 -and $r.Out -match '::error' -and $r.Out -match '12\.\d h|28\d\.\d h|29\d\.\d h') $r
Expect 'a time zone offset is understood, not read as UTC' ((Run (Config $servedSha ($newestAt.AddHours(-23).ToOffset([TimeSpan]::FromHours(-8)).ToString('o'))) (Newest)).Code -eq 0)

# fail closed: the served build cannot be read
$r = Run $null (Newest); Expect 'no config.js file fails closed' ($r.Code -eq 1 -and $r.Out -match '::error') $r
$r = Run '' (Newest); Expect 'an empty config.js fails closed' ($r.Code -eq 1 -and $r.Out -match '::error') $r
$r = Run "<html>404</html>`n" (Newest); Expect 'a page that is not config.js fails closed' ($r.Code -eq 1 -and $r.Out -match '::error') $r
foreach ($bad in 'dev', 'ABCDEF' + ('a' * 34), ('a' * 39), ('a' * 41), ('g' * 40), '') {
    $r = Run (Config $bad (Ago 1)) (Newest)
    Expect "APP_VERSION '$bad' (not a 40-hex commit) fails closed" ($r.Code -eq 1 -and $r.Out -match '::error') $r
}
foreach ($bad in '', 'garbage', '2026-13-45T99:00:00Z', 'yesterday') {
    $r = Run (Config $servedSha $bad) (Newest)
    Expect "BUILT_AT '$bad' fails closed" ($r.Code -eq 1 -and $r.Out -match '::error') $r
}
# fail closed: main's newest PWA change cannot be read
$r = Run (Config $servedSha (Ago 1)) $null; Expect 'no newest-commit file fails closed' ($r.Code -eq 1 -and $r.Out -match '::error') $r
$r = Run (Config $servedSha (Ago 1)) ''; Expect 'an empty newest-commit file fails closed' ($r.Code -eq 1) $r
$r = Run (Config $servedSha (Ago 1)) 'not json'; Expect 'newest-commit JSON that does not parse fails closed' ($r.Code -eq 1) $r
$r = Run (Config $servedSha (Ago 1)) (Newest ('a' * 12)); Expect 'a short newest sha fails closed' ($r.Code -eq 1) $r
$r = Run (Config $servedSha (Ago 1)) (Newest $newestSha 'soon'); Expect 'an unparseable newest date fails closed' ($r.Code -eq 1) $r
$r = Run (Config $servedSha (Ago 1)) '[]'; Expect 'no PWA-changing commit at all (an empty list) fails closed, never reads as fresh' ($r.Code -eq 1) $r

# what a hostile config.js can do: nothing, because values are validated before they are printed
$r = Run (Config "$('a' * 40)`n::error title=pwned::x" (Ago 1)) (Newest)
Expect 'a value cannot inject a workflow command: nothing unvalidated is echoed' ($r.Code -eq 1 -and $r.Out -notmatch 'pwned') $r
$r = Run ("export const APP_VERSION = `"$servedSha`";`nexport const BUILT_AT = `"2026-11-24T00:00:00Z`n::warning::x`";`n") (Newest)
Expect 'a BUILT_AT with a newline in it fails closed' ($r.Code -eq 1 -and $r.Out -notmatch '::warning::x') $r

# the step summary says what happened, on both outcomes
$r = Run (Config $servedSha (Ago 1)) (Newest); Expect 'the step summary has a line when fine' ($r.Summary -match 'stale-PWA') $r
$r = Run (Config $servedSha (Ago 50)) (Newest); Expect 'the step summary has a line when stale' ($r.Summary -match 'stale-PWA' -and $r.Summary -match '50\.0 h') $r

# read-only, offline, no secrets
$text = Get-Content $script -Raw
Expect 'the script makes no request and reads no token (the workflow does the two GETs)' ($text -notmatch 'Invoke-(Web|Rest)Request|\bcurl\b|\bgh\b|\bgit\b|GH_TOKEN|GITHUB_TOKEN|secrets\.') $null

Remove-Item -Recurse -Force $work
if ($failures) { Write-Output "::error::$failures of the stale-PWA alarm's checks failed"; exit 1 }
