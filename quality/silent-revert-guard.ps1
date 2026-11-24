#Requires -Version 7
# The silent-revert guard (#4465): every line a PR deletes against main is blamed on main, and a line last changed by
# another merged PR is flagged in the job summary and as a warning ("this PR removes N lines from #NNNN"), a deleted
# test method by name. #3968's merge of main took its stale side and deleted #4126's code and tests together, and CI
# stayed green. It only flags: whatever it finds, and any error, is a warning, and it always exits 0.
#
#   pwsh quality/silent-revert-guard.ps1 [-Base HEAD^1] [-Head HEAD] [-Pr <this PR's number>]
#
# The diff runs from the merge base of -Base and -Head. In CI's pull_request checkout HEAD is the PR merged into main, so
# that is HEAD^1, main, and the diff is what the PR changes.
# Lines older than the history fetched are blamed on its boundary commit, which names no PR.
param([string]$Base = 'HEAD^1', [string]$Head = 'HEAD', [string]$Repo = '.', [int]$Pr = 0,
    [string]$Summary = $env:GITHUB_STEP_SUMMARY, [int]$MaxWarnings = 10)
$ErrorActionPreference = 'Stop'
function Invoke-Git { $out = & git -C $Repo @args 2>$null; if ($LASTEXITCODE) { throw "git $($args[0]) failed ($LASTEXITCODE)" }; $out }
function Note([string[]]$lines) { if ($Summary) { Add-Content -LiteralPath $Summary -Value $lines } else { $lines | Write-Host } }
try {
    $headSha = [string](Invoke-Git rev-parse --verify --quiet "$Head^{commit}")
    # From where the PR left main (in CI's merge checkout, HEAD^1 itself): main's later changes are not the PR's, and a stale
    # side a merge of main took (#3968) still shows as a deletion.
    $baseSha = [string](Invoke-Git merge-base ([string](Invoke-Git rev-parse --verify --quiet "$Base^{commit}")) $headSha)
    # Deleted lines per file, by their line number on the base, with their text (--unified=0: a hunk's old lines are all deletions).
    $deleted = [ordered]@{}; $file = $null; $line = 0; $left = 0
    foreach ($text in @(Invoke-Git diff --no-color --no-renames --no-ext-diff --unified=0 $baseSha $headSha)) {
        if ($left -gt 0 -and $text.StartsWith('-')) { $deleted[$file].Add([pscustomobject]@{ Line = $line; Text = $text.Substring(1) }); $line++; $left--; continue }
        if ($text.StartsWith('diff --git ')) { $file = $null; $left = 0; continue }
        if ($text -match '^--- a/(.+)$') { $file = $Matches[1]; continue }
        if ($text -match '^@@ -(\d+)(?:,(\d+))? ' -and $file) {
            $line = [int]$Matches[1]; $left = if ($Matches.ContainsKey(2)) { [int]$Matches[2] } else { 1 }
            if ($left -and -not $deleted.Contains($file)) { $deleted[$file] = [Collections.Generic.List[object]]::new() }
        }
    }
    # Who last changed each deleted line on the base, and which PR that commit merged.
    $found = [Collections.Generic.List[object]]::new()
    foreach ($f in $deleted.Keys) {
        $lines = $deleted[$f]; $byLine = @{}; foreach ($d in $lines) { $byLine[$d.Line] = $d }
        $ranges = @(); $start = $lines[0].Line; $prev = $start
        foreach ($d in @($lines | Select-Object -Skip 1)) { if ($d.Line -ne $prev + 1) { $ranges += "$start,$prev"; $start = $d.Line }; $prev = $d.Line }
        $ranges += "$start,$prev"
        $sha = $null
        foreach ($b in @(Invoke-Git blame --line-porcelain @($ranges | ForEach-Object { '-L'; $_ }) $baseSha -- $f)) {
            if ($b -match '^([0-9a-f]{40}) \d+ (\d+)') { $sha = $Matches[1]; $found.Add([pscustomobject]@{ File = $f; Sha = $sha; Text = $byLine[[int]$Matches[2]].Text }) }
        }
    }
    $subjects = @{}
    $shas = @($found.Sha | Sort-Object -Unique)
    if ($shas) { foreach ($s in @(Invoke-Git log --no-walk --format='%H%x09%s' @shas)) { $h, $subject = $s -split "`t", 2; $subjects[$h] = $subject } }
    $byPr = [ordered]@{}
    foreach ($x in $found) {
        $subject = [string]$subjects[$x.Sha]
        $number = if ($subject -match '\(#(\d+)\)\s*$') { [int]$Matches[1] } elseif ($subject -match '^Merge pull request #(\d+)') { [int]$Matches[1] } else { 0 }
        if ($Pr -and $number -eq $Pr) { continue }
        $key = if ($number) { "#$number" } else { "no PR number: $($x.Sha.Substring(0, 8))" }
        if (-not $byPr.Contains($key)) { $byPr[$key] = [Collections.Generic.List[object]]::new() }
        $byPr[$key].Add($x)
    }
    if (-not $byPr.Count) { Note 'Silent-revert guard: this PR removes no lines from other merged PRs.'; return }
    # A deleted test method: its declaration is among the deleted lines and its name is nowhere in the file on the head.
    $tests = foreach ($key in $byPr.Keys) {
        foreach ($x in $byPr[$key]) {
            if ($x.File -notmatch 'Tests?\.cs$' -or $x.Text -notmatch '^\s*public\s+(?:static\s+)?(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(') { continue }
            $name = $Matches[1]
            $now = try { (Invoke-Git show "${headSha}:$($x.File)") -join "`n" } catch { '' }
            if ($now -notmatch "\b$([regex]::Escape($name))\s*\(") { [pscustomobject]@{ Pr = $key; File = $x.File; Name = $name } }
        }
    }
    $report = @('### Silent-revert guard: lines this PR removes from other merged PRs', '',
        'Not a failure: check each is meant to go (#4465). A modified line counts too, as its old text is removed.', '')
    foreach ($t in $tests) { $report += "- **deletes test ``$($t.Name)``** ($($t.File)) from $($t.Pr)" }
    foreach ($key in $byPr.Keys) { $report += "- removes $($byPr[$key].Count) line(s) from ${key}: $((@($byPr[$key].File | Sort-Object -Unique)) -join ', ')" }
    Note $report
    $warnings = @(foreach ($t in $tests) { "::warning file=$($t.File),title=Silent-revert guard::this PR deletes test $($t.Name) from $($t.Pr)" }) +
        @(foreach ($key in $byPr.Keys) { $first = $byPr[$key][0]; "::warning file=$($first.File),title=Silent-revert guard::this PR removes $($byPr[$key].Count) line(s) from $key" })
    $warnings | Select-Object -First $MaxWarnings | Write-Host
    if ($warnings.Count -gt $MaxWarnings) { Write-Host "Silent-revert guard: $($warnings.Count - $MaxWarnings) more in the job summary" }
} catch {
    Write-Host "::warning title=Silent-revert guard::could not run: $($_.Exception.Message)"
}
$global:LASTEXITCODE = 0
exit 0
