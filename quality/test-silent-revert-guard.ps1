#Requires -Version 7
# quality/silent-revert-guard.ps1 (#4465): a PR whose diff against main deletes lines another merged PR last changed is
# flagged (job summary and warnings, naming deleted test methods), never failed. Offline, in a throwaway git repository.
$ErrorActionPreference = 'Stop'
$guard = Join-Path $PSScriptRoot 'silent-revert-guard.ps1'
$failures = 0
function Expect([bool]$ok, [string]$what) { if (-not $ok) { Write-Host "FAIL: $what"; $script:failures++ } }
$repo = Join-Path ([IO.Path]::GetTempPath()) ('revert-guard-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $repo | Out-Null
function G { & git -C $repo @args; if ($LASTEXITCODE) { throw "git $($args -join ' ') failed" } }
function Commit([string]$subject, [hashtable]$files) {
    foreach ($f in $files.Keys) { $path = Join-Path $repo $f; New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null; [IO.File]::WriteAllText($path, ($files[$f] -join "`n") + "`n") }
    G add -A; G commit -q -m $subject
}
function Run([string]$base, [string]$head, [hashtable]$extra = @{}) {
    $summary = Join-Path $repo '.git' 'guard-summary.md'; Set-Content $summary '' -NoNewline
    $out = @(& $guard -Repo $repo -Base $base -Head $head -Summary $summary @extra *>&1 | ForEach-Object { "$_" })
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $out -join "`n"; Summary = (Get-Content $summary -Raw) }
}
$lite = @('class Lite {', '    int a = 1;', '    int b = 2;', '    int c = 3;', '}')
$tests = @('public class LiteTests {', '    [Fact]', '    public void Holds_the_node() { Assert.True(true); }', '    [Fact]', '    public void Keeps_order() { Assert.True(true); }', '}')
try {
    G init -q -b main; G config user.name 'Guard Test'; G config user.email 'guard-test@example.invalid'; G config core.autocrlf false
    Commit 'Start the project' @{ 'README.md' = @('readme') }
    Commit 'Add LiteDeliver (#100)' @{ 'src/Lite.cs' = $lite; 'tests/LiteTests.cs' = $tests }
    $held = $lite.Clone(); $held[2] = '    int b = 20; // held node'
    $heldTests = $tests[0..4] + @('    [Fact]', '    public async Task Resolves_while_held() { await Task.Yield(); }', '    [Fact]', '    public void Clears_on_release() { Assert.True(true); }', '}')
    Commit 'A node id resolves while another agent is held (#200)' @{ 'src/Lite.cs' = $held; 'tests/LiteTests.cs' = $heldTests }
    # The PR: a stale side wins in its merge of main, so #200's line and its test are gone; it also edits #100's Keeps_order.
    G switch -q -c pr
    $stale = $tests.Clone(); $stale[4] = '    public void Keeps_order() { Assert.Equal(1, 1); }'
    Commit 'Lite: tidy (#300)' @{ 'src/Lite.cs' = $lite; 'tests/LiteTests.cs' = $stale; 'docs/new.md' = @('new') }

    # Main moves on after the PR branched (#500 rewrites the readme): the PR is diffed from where it left main, so #500's
    # change is not one this PR removes.
    G switch -q main
    Commit 'Rewrite the readme for #500 (#500)' @{ 'README.md' = @('readme', 'more') }
    G switch -q pr

    $run = Run main pr
    Expect ($run.Exit -eq 0) "the guard never fails (exit $($run.Exit))"
    Expect ($run.Summary -match '#200' -and $run.Out -match '::warning [^\n]*removes [0-9]+ line\(s\) from #200') "a reverted line of #200 is flagged -> $($run.Out)"
    Expect ($run.Out -match '::warning [^\n]*deletes test Resolves_while_held from #200' -and $run.Summary -match 'Resolves_while_held') "#200's deleted test is named -> $($run.Out)"
    Expect ($run.Summary -match '#100' -and $run.Summary -notmatch 'Keeps_order') "an edited test that still exists is a changed line of #100, not a deleted test -> $($run.Summary)"
    Expect ($run.Summary -notmatch 'docs/new\.md') 'an added file removes nothing'
    Expect ($run.Summary -notmatch '#500') "a change main made after the PR branched is not the PR's -> $($run.Summary)"
    Expect ($run.Summary -match 'deletes test `Resolves_while_held`' -and $run.Summary -match 'deletes test `Clears_on_release`' -and $run.Summary -notmatch 'deletes test ```') "each deleted test is named by its own name -> $($run.Summary)"

    $own = Run main pr @{ Pr = 200 }
    Expect ($own.Summary -notmatch '#200' -and $own.Out -notmatch 'from #200') "a PR's own earlier lines are not flagged -> $($own.Out)"

    G switch -q main; G switch -q -c additive
    Commit 'Only additions (#400)' @{ 'docs/more.md' = @('more') }
    $clean = Run main additive
    Expect ($clean.Exit -eq 0 -and $clean.Out -notmatch '::warning' -and $clean.Summary -match 'removes no lines') "a PR that only adds is quiet -> $($clean.Out) / $($clean.Summary)"

    G switch -q main; G switch -q -c unnumbered
    Commit 'Rewrite the readme' @{ 'README.md' = @('rewritten') }
    $old = Run main unnumbered
    Expect ($old.Summary -match 'no PR number' -and $old.Exit -eq 0) "a line from a commit without a PR number is listed by its commit -> $($old.Summary)"

    $broken = Run 'no-such-ref' pr
    Expect ($broken.Exit -eq 0 -and $broken.Out -match '::warning [^\n]*could not run') "an error is a warning, never a failure -> exit $($broken.Exit): $($broken.Out)"
} finally { Remove-Item -Recurse -Force $repo -ErrorAction SilentlyContinue }

Write-Host $(if ($failures) { "silent-revert guard: $failures failed" } else { 'silent-revert guard: ok' })
if ($failures) { exit 1 }
