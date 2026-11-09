# Offline checks for quality/red-minutes.ps1: a fixed run list, no GitHub.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'red-minutes.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("red-minutes-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$fail = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ }
}
function Run($runs, $hours = 24) {
    $f = Join-Path $tmp 'runs.json'
    $runs | ConvertTo-Json -Depth 4 | Set-Content $f
    pwsh -NoProfile -File $script -RunsFile $f -Now '2026-11-09T08:00:00Z' -Hours $hours -Json | ConvertFrom-Json
}
function NewRun($conclusion, $at, $sha = 'aaaaaaaaaaaa') {
    [pscustomobject]@{ conclusion = $conclusion; updated_at = $at; head_sha = $sha; html_url = "https://example.test/$sha" }
}
try {
    $green = Run @(NewRun 'success' '2026-11-08T00:00:00Z')
    Check 'all green is zero red minutes' ($green.red_minutes -eq 0 -and $green.spells -eq 0 -and $green.over_target.Count -eq 0)

    # One 20 minute spell inside the window; a cancelled run in the middle neither ends nor starts anything.
    $one = Run @(
        (NewRun 'success' '2026-11-08T00:00:00Z'),
        (NewRun 'failure' '2026-11-09T01:00:00Z' 'bbbbbbbbbbbb'),
        (NewRun 'cancelled' '2026-11-09T01:10:00Z'),
        (NewRun 'success' '2026-11-09T01:20:00Z'))
    Check 'a cancelled run does not end a spell' ($one.red_minutes -eq 20 -and $one.spells -eq 1 -and $one.longest_spell_min -eq 20)
    Check 'the spell names its first red sha' ($one.spell_list[0].first_red_sha -eq 'bbbbbbbbbbbb')
    Check 'a 20 minute spell is over the 30 minute rule only if longer' ($one.over_target -notcontains 'longest spell')

    # A spell still open at Now counts to Now and breaks the longest-spell target.
    $open = Run @((NewRun 'success' '2026-11-08T00:00:00Z'), (NewRun 'failure' '2026-11-09T06:00:00Z'))
    Check 'an open spell counts to now' ($open.red_minutes -eq 120 -and $open.over_target -contains 'longest spell')

    # A spell that began before the window counts only its part inside it.
    $edge = Run @((NewRun 'failure' '2026-11-08T07:00:00Z'), (NewRun 'success' '2026-11-08T08:30:00Z'))
    Check 'a spell crossing the window start is clipped' ($edge.red_minutes -eq 30)
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail) { throw "$fail check(s) failed" }
'all red-minutes checks passed'
