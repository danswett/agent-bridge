#Requires -Version 7
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'quarantine-pr.ps1')
$script:failures = 0
function Assert($Ok, $Name) { if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ } }
$source = "namespace A.B;`npublic class C {`n    [Fact]`n    public void Works() { }`n}"
$script:GhCommand = 'Fake-QuarantineGh'
function Fake-QuarantineGh {
    $a = $args -join ' '
    $body = $input -join "`n"
    $script:calls.Add($a)
    if ($body) { $script:bodies.Add(($body | ConvertFrom-Json)) }
    if ($s.apiFailure -and $a.StartsWith('api repos/r/git/trees --method POST')) { $global:LASTEXITCODE = 1; return }
    switch -Regex ($a) {
        '^variable get' { $script:variableReads++; if ($s.disableAfter -and $variableReads -gt $s.disableAfter) { 'off' } else { $script:s.enabled }; return }
        '^api repos/r/actions/runs/100$' {
            @{ path = '.github/workflows/ci.yml'; event = 'push'; head_branch = $s.branch
                head_repository = @{ full_name = 'r' }; conclusion = 'failure'; run_attempt = $s.attempt
                status = 'completed'; head_sha = 'head' } | ConvertTo-Json; return }
        '^api repos/r/actions/runs/100/attempts/([12])/jobs' {
            $attempt = [int]$Matches[1]
            @{ total_count = $s.jobCount; jobs = @(@{ id = $attempt; name = 'product'; conclusion = $s.jobConclusion }) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/jobs/([12])/logs' { $s.logs[[int]$Matches[1] - 1]; return }
        '^api repos/r/git/ref/heads/main' { @{ object = @{ sha = $s.head } } | ConvertTo-Json; return }
        '^pr list' { ConvertTo-Json -InputObject @($s.prs) -Depth 4; return }
        '^api repos/r/git/matching-refs/heads/(.+)$' {
            $refs = if ($s.refExists) { @(@{ ref = "refs/heads/$($Matches[1])"; object = @{ type = 'commit'; sha = 'created' } }) } else { @() }
            ConvertTo-Json -InputObject @($refs) -Depth 4; return }
        '^api repos/r/git/commits/created$' {
            @{ message = $s.refMessage; parents = @(@{ sha = $s.refParent }); tree = @{ sha = 'ref-tree' } } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/git/trees/ref-tree\?recursive=1$' {
            @{ truncated = $false; tree = @(@{ path = $s.path; type = 'blob'; mode = '100644'; sha = 'current-blob' }) + @($s.extraFiles) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/git/blobs/current-blob$' {
            @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s.refSource)) } | ConvertTo-Json; return }
        '^api repos/r/git/trees/head\?recursive=1' {
            $entries = @(@{ path = $s.path; type = 'blob'; mode = '100644'; sha = 'blob' })
            if ($s.ambiguous) {
                $duplicate = if ($s.path.StartsWith('pwa/')) { $s.path.Replace('pwa/', 'pwa/test/other/') } else { $s.path.Replace('tests/', 'tests/other/') }
                $entries += @{ path = $duplicate; type = 'blob'; mode = '100644'; sha = 'blob2' }
            }
            @{ sha = 'base-tree'; truncated = $s.truncated; tree = $entries } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/git/blobs/blob$' {
            @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s.source)) } | ConvertTo-Json; return }
        '^issue list' { ConvertTo-Json -InputObject @($s.issues) -Depth 4; return }
        '^issue create' { 'https://github.com/r/issues/42'; return }
        '^issue (comment|edit)' { return }
        '^api repos/r/git/trees --method POST --input -$' { $s.refSource = ($body | ConvertFrom-Json).tree[0].content; @{ sha = 'created' } | ConvertTo-Json; return }
        '^api repos/r/git/refs --method POST --input -$' {
            if ($s.refExists) { $global:LASTEXITCODE = 1; return }
            $s.refExists = $true; @{ sha = 'created' } | ConvertTo-Json; return }
        '^api repos/r/git/commits --method POST --input -$' { $s.refMessage = ($body | ConvertFrom-Json).message; @{ sha = 'created' } | ConvertTo-Json; return }
        '^pr create' {
            if ($s.failPrCreate) { $s.failPrCreate = $false; $global:LASTEXITCODE = 1; return }
            'https://github.com/r/pull/43'; return }
        default { throw "Unexpected offline request: $a" }
    }
}
function Scenario($over = @{}, [double]$Hours = 24, [string]$TestName = 'A.B.C.Works', [switch]$Retry) {
    $script:s = @{ enabled = 'on'; branch = 'main'; attempt = 2; head = 'head'; prs = @(); issues = @()
        ambiguous = $false; truncated = $false; source = $source; apiFailure = $false; disableAfter = 0
        path = 'tests/C.cs'; jobCount = 1; jobConclusion = 'failure'; logs = @('  Failed A.B.C.Works [1 ms]', '  Failed A.B.C.Works [2 ms]')
        refExists = $false; refSource = ''; failPrCreate = $false; extraFiles = @(); refParent = 'head'; refMessage = 'Quarantine A.B.C.Works (issue #42)' }
    foreach ($k in $over.Keys) { $script:s[$k] = $over[$k] }
    $script:calls = [Collections.Generic.List[string]]::new()
    $script:bodies = [Collections.Generic.List[object]]::new()
    $script:variableReads = 0
    $errorText = $null; $result = $null; $firstError = $null
    try { $result = Invoke-QuarantinePr -RunId 100 -TestName $TestName -Repository r -Hours $Hours }
    catch { $errorText = $_.Exception.Message }
    if ($Retry) {
        $firstError = $errorText; $errorText = $null
        try { $result = Invoke-QuarantinePr -RunId 100 -TestName $TestName -Repository r -Hours $Hours }
        catch { $errorText = $_.Exception.Message }
    }
    [pscustomobject]@{ Result = $result; Error = $errorText; FirstError = $firstError
        Writes = @($calls | Where-Object { $_ -match '^(issue (create|edit|comment)|pr create)|--method POST' }) }
}
$x = Scenario
Assert (-not $x.Error) "one twice-failing test generates a quarantine: $($x.Error)"
$tree = @($bodies | Where-Object { $_.PSObject.Properties['base_tree'] })
if ($tree.Count -eq 1) {
    $edited = $tree[0].tree[0].content
    $restored = $edited -replace '\[Fact\(Skip = "[^"]+"\)\]', '[Fact]'
    Assert ($tree[0].base_tree -eq 'base-tree' -and $tree[0].tree.Count -eq 1 -and $restored -ceq $source) 'the git tree extends the main tree and changes only one Fact attribute'
    Assert ($edited -match 'quarantine-expires=([^;"]+); issue #42') 'skip reason carries expiry and tracking issue'
    $expiry = [DateTimeOffset]::Parse($Matches[1])
    Assert ($expiry -gt [DateTimeOffset]::UtcNow -and $expiry -le [DateTimeOffset]::UtcNow.AddHours(24)) 'expiry is within 24 hours'
} else { Assert $false 'one file tree was generated' }
Assert (@($calls | Where-Object { $_.StartsWith('pr create ') -and $_.Contains('--draft --label fixes-main') }).Count -eq 1) 'PR is draft and fixes-main'
Assert (@($calls | Where-Object { $_.StartsWith('issue comment ') -and $_.Contains('quarantine-pr: test=A.B.C.Works run=100 attempt=2 job=2') }).Count -eq 1) 'tracking receipt retains the per-test run attempt job shape'
foreach ($change in @(
    @{ enabled = 'off' }, @{ attempt = 1 }, @{ attempt = 3 }, @{ branch = 'feature' }, @{ head = 'newer' },
    @{ jobCount = 101 }, @{ jobCount = 2 }, @{ jobConclusion = 'timed_out' }, @{ jobConclusion = 'cancelled' },
    @{ logs = @('  Failed A.B.C.Works [1 ms]', "  Failed A.B.C.Works [1 ms]`n  Failed A.B.C.Other [1 ms]") },
    @{ logs = @('  Failed A.B.C.Other [1 ms]', '  Failed A.B.C.Works [1 ms]') },
    @{ logs = @('error CS1002', '  Failed A.B.C.Works [1 ms]') },
    @{ logs = @('  Failed A.B.C.Works [1 ms]', 'build failed') },
    @{ ambiguous = $true }, @{ truncated = $true },
    @{ source = $source.Replace('[Fact]', '[Fact(Skip = "existing")]') },
    @{ source = $source.Replace('[Fact]', '[Fact(SkipUnless = nameof(Ready))]') },
    @{ source = $source + "`npublic class Other { }" },
    @{ source = $source.Replace('Works()', 'SomethingElse()') }
)) {
    $x = Scenario $change
    Assert ($x.Error -and $x.Writes.Count -eq 0) ("unsafe evidence refuses before writes: " + ($change | ConvertTo-Json -Compress))
}
$x = Scenario @{} -Hours 25
Assert ($x.Error -and $x.Writes.Count -eq 0) '25-hour request refuses rather than clamps'
$enforcer = 'AgentBridge.Quality.Tests.QuarantineExpiryTests.Quarantines_in_test_sources_have_not_expired'
$log = "  Failed $enforcer [1 ms]"
$x = Scenario @{ path = 'tests/AgentBridge.Quality.Tests/QuarantineExpiryTests.cs'; logs = @($log, $log) } -TestName $enforcer
Assert ($x.Error -like '*cannot quarantine itself*' -and $x.Writes.Count -eq 0) 'expiry enforcement cannot disable itself'
$x = Scenario @{ disableAfter = 1 }
Assert ($x.Error -and $x.Writes.Count -eq 0) 'revocation before the first write leaves no issue or PR'
$x = Scenario @{ apiFailure = $true }
Assert ($x.Error -and @($calls | Where-Object { $_ -like 'pr create*' }).Count -eq 0) 'git API failure is explicit and never opens a PR'
$issue = @{ number = 42; title = 'flaky A.B.C.Works'; body = ''; author = @{ login = 'danswett' } }
$x = Scenario @{ issues = @($issue) }
Assert (-not $x.Error -and @($calls | Where-Object { $_ -like 'issue create*' }).Count -eq 0) 'an existing trusted flake issue is reused'
$deadline = [DateTimeOffset]::UtcNow.AddHours(4).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
$draft = @{ number = 43; url = 'https://github.com/r/pull/43'; isDraft = $true; headRefOid = 'created'
    baseRefName = 'main'; body = "quarantine-pr: test=A.B.C.Works issue=42`nExpiry: $deadline" }
$skipped = $source.Replace('[Fact]', "[Fact(Skip = `"quarantine-expires=$deadline; issue #42`")]")
$reuse = @{ prs = @($draft); refExists = $true; refSource = $skipped }
$x = Scenario $reuse
Assert (-not $x.Error -and $x.Writes.Count -eq 0 -and $x.Result.Pr -eq 'https://github.com/r/pull/43') 'repeat invocation reuses the open draft without writes'
$otherMarker = "`n// quarantine-expires=$deadline; issue #99"
$case = $reuse.Clone(); $case.source = $source + $otherMarker; $case.refSource = $skipped + $otherMarker
$x = Scenario $case
Assert (-not $x.Error -and $x.Result.Reused -and $x.Writes.Count -eq 0) 'reuse validates the changed declaration without mistaking an unchanged expiry marker for its deadline'
foreach ($body in @('quarantine-pr: test=A.B.C.Works issue=42', ($draft.body + "`nExpiry: invalid"))) {
    $case = $reuse.Clone(); $pr = $draft.Clone(); $pr.body = $body; $case.prs = @($pr)
    $x = Scenario $case
    Assert ($x.Error -and $x.Writes.Count -eq 0) 'reuse refuses absent or ambiguous draft deadlines'
}
foreach ($bad in @('', 'invalid', '2020-01-01T00:00:00Z', [DateTimeOffset]::UtcNow.AddHours(25).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))) {
    $case = $reuse.Clone(); $case.refSource = $skipped.Replace($deadline, $bad)
    $x = Scenario $case
    Assert ($x.Error -and $x.Writes.Count -eq 0) "reuse refuses missing, malformed, elapsed or over-cap current Skip expiry: '$bad'"
    $case = $reuse.Clone(); $pr = $draft.Clone(); $pr.body = $draft.body.Replace($deadline, $bad); $case.prs = @($pr)
    $x = Scenario $case
    Assert ($x.Error -and $x.Writes.Count -eq 0) "reuse refuses missing, malformed, elapsed or inconsistent draft expiry: '$bad'"
}
foreach ($bad in @($source, $skipped.Replace('issue #42', 'issue #99'), ($skipped + "`n// unrelated edit"))) {
    $case = $reuse.Clone(); $case.refSource = $bad
    $x = Scenario $case
    Assert ($x.Error -and $x.Writes.Count -eq 0) 'reuse requires the current single Skip-only edit and matching tracking issue'
}
$x = Scenario @{ failPrCreate = $true } -Retry
Assert ($x.FirstError -and -not $x.Error -and $x.Result.Pr -eq 'https://github.com/r/pull/43') "retry resumes an orphan after failed PR creation: $($x.Error)"
Assert (@($calls | Where-Object { $_ -like 'pr create*' }).Count -eq 2 -and
    @($calls | Where-Object { $_ -like 'api repos/r/git/refs --method POST*' }).Count -eq 1) 'retry reaches PR creation without rewriting or recreating the existing ref'
Assert (@($calls | Where-Object { $_ -like 'issue create*' }).Count -eq 1 -and $x.Result.Expiry -and
    $s.refSource.Contains("quarantine-expires=$($x.Result.Expiry); issue #$($x.Result.Issue)")) 'orphan resume preserves its original tracking issue and deadline'
foreach ($bad in @(
    @{ refSource = $source }, @{ refSource = $skipped.Replace($deadline, '2020-01-01T00:00:00Z') },
    @{ refSource = $skipped; refParent = 'older-main' },
    @{ refSource = $skipped; extraFiles = @(@{ path = '.github/workflows/ci.yml'; type = 'blob'; mode = '100644'; sha = 'other' }) }
)) {
    $case = $bad.Clone(); $case.refExists = $true
    $x = Scenario $case
    Assert ($x.Error -and $x.Writes.Count -eq 0) 'unsafe or stale orphan refs refuse without writes'
}
$x = Scenario @{ refExists = $true; refSource = $skipped; disableAfter = 1 }
Assert ($x.Error -and $x.Writes.Count -eq 0) 'revocation before orphan PR creation leaves its ref untouched'
$x = Scenario @{ source = $source.Replace('[Fact]', "[Theory]`n    [InlineData(1)]") }
Assert (-not $x.Error -and @($bodies | Where-Object { $_.PSObject.Properties['base_tree'] })[0].tree[0].content -match '\[Theory\(Skip = "') 'Theory with data retains data and gains one Skip'
$crlf = $source.Replace("`n", "`r`n").Replace('[Fact]', '[Fact(Timeout = 1000)]')
$x = Scenario @{ source = $crlf }
$content = @($bodies | Where-Object { $_.PSObject.Properties['base_tree'] })[0].tree[0].content
Assert (-not $x.Error -and ($content -replace ', Skip = "[^"]+"', '') -ceq $crlf) 'existing arguments and CRLF bytes are preserved'
$pw = "// header`r`ntest('works', async ({ page }) => {`r`n  await page.goto('/');`r`n});`r`n"
$arrow = [char]0x203A
$log = "  1) [webkit] $arrow test/a.spec.mjs:2:1 $arrow works"
$pwCase = @{ path = 'pwa/test/a.spec.mjs'; source = $pw; logs = @($log, $log) }
$x = Scenario $pwCase -TestName 'test/a.spec.mjs:2'
Assert (-not $x.Error) "one Playwright declaration gains skip: $($x.Error)"
if (-not $x.Error) {
    $content = @($bodies | Where-Object { $_.PSObject.Properties['base_tree'] })[0].tree[0].content
    Assert ($content -match ' // quarantine-expires=[^;]+; issue #42') 'Playwright expiry is in the declaration-line comment'
    $restored = ($content -replace 'test\.skip\(', 'test(') -replace ' // quarantine-expires=[^\r\n]+', ''
    Assert ($restored -ceq $pw) 'only the Playwright declaration line changes, preserving CRLF'
}
$case = $pwCase.Clone(); $case.failPrCreate = $true
$x = Scenario $case -TestName 'test/a.spec.mjs:2' -Retry
Assert ($x.FirstError -and -not $x.Error -and $x.Result.Pr -eq 'https://github.com/r/pull/43') 'Playwright orphan retry also validates and resumes the original Skip-only commit'
foreach ($bad in @(
    @{ source = $pw.Replace('test(', 'test.skip(') }, @{ source = $pw.Replace('test(', 'test.fixme(') },
    @{ ambiguous = $true }, @{ source = $pw.Replace("test('works'", "// test('works'") },
    @{ source = $pw.Replace("test('works'", "test('a', () => {}); test('works'") }
)) {
    $case = $pwCase.Clone(); foreach ($k in $bad.Keys) { $case[$k] = $bad[$k] }
    $x = Scenario $case -TestName 'test/a.spec.mjs:2'
    Assert ($x.Error -and $x.Writes.Count -eq 0) 'skipped, ambiguous or nonunique Playwright declarations refuse before writes'
}
if ($script:failures) { throw "$script:failures quarantine check(s) failed" }
'quarantine-pr: all offline checks passed'
