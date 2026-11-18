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
    switch -Regex ($a) {
        '^variable get' { $script:s.enabled; return }
        '^api repos/r/actions/runs/100$' {
            @{ path = '.github/workflows/ci.yml'; event = 'push'; head_branch = $s.branch
                head_repository = @{ full_name = 'r' }; conclusion = 'failure'; run_attempt = $s.attempt
                status = 'completed'; head_sha = 'head' } | ConvertTo-Json; return }
        '^api repos/r/actions/runs/100/attempts/([12])/jobs' {
            $attempt = [int]$Matches[1]
            @{ total_count = 1; jobs = @(@{ id = $attempt; name = 'product'; conclusion = 'failure' }) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/jobs/([12])/logs' { $s.logs[[int]$Matches[1] - 1]; return }
        '^api repos/r/git/ref/heads/main' { @{ object = @{ sha = $s.head } } | ConvertTo-Json; return }
        '^pr list' { ConvertTo-Json -InputObject @($s.prs) -Depth 4; return }
        '^api repos/r/git/trees/head\?recursive=1' {
            $entries = @(@{ path = 'tests/C.cs'; type = 'blob'; mode = '100644'; sha = 'blob' })
            if ($s.ambiguous) { $entries += @{ path = 'tests/other/C.cs'; type = 'blob'; mode = '100644'; sha = 'blob2' } }
            @{ truncated = $s.truncated; tree = $entries } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/git/blobs/blob$' {
            @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s.source)) } | ConvertTo-Json; return }
        '^issue list' { ConvertTo-Json -InputObject @($s.issues) -Depth 4; return }
        '^issue create' { 'https://github.com/r/issues/42'; return }
        '^issue (comment|edit)' { return }
        '^api repos/r/git/(trees|commits|refs) --method POST --input -$' { @{ sha = 'created' } | ConvertTo-Json; return }
        '^pr create' { 'https://github.com/r/pull/43'; return }
        default { throw "Unexpected offline request: $a" }
    }
}
function Scenario($over = @{}, [double]$Hours = 24) {
    $script:s = @{ enabled = 'on'; branch = 'main'; attempt = 2; head = 'head'; prs = @(); issues = @()
        ambiguous = $false; truncated = $false; source = $source; logs = @('  Failed A.B.C.Works [1 ms]', '  Failed A.B.C.Works [2 ms]') }
    foreach ($k in $over.Keys) { $script:s[$k] = $over[$k] }
    $script:calls = [Collections.Generic.List[string]]::new()
    $script:bodies = [Collections.Generic.List[object]]::new()
    $errorText = $null; $result = $null
    try { $result = Invoke-QuarantinePr -RunId 100 -TestName 'A.B.C.Works' -Repository r -Hours $Hours }
    catch { $errorText = $_.Exception.Message }
    [pscustomobject]@{ Result = $result; Error = $errorText
        Writes = @($calls | Where-Object { $_ -match '^(issue (create|edit|comment)|pr create)|--method POST' }) }
}
$x = Scenario
Assert (-not $x.Error) "one twice-failing test generates a quarantine: $($x.Error)"
$tree = @($bodies | Where-Object { $_.PSObject.Properties['base_tree'] })
if ($tree.Count -eq 1) {
    $edited = $tree[0].tree[0].content
    $restored = $edited -replace '\[Fact\(Skip = "[^"]+"\)\]', '[Fact]'
    Assert ($tree[0].tree.Count -eq 1 -and $restored -ceq $source) 'the git tree changes only one Fact attribute'
    Assert ($edited -match 'quarantine-expires=([^;"]+); issue #42') 'skip reason carries expiry and tracking issue'
    $expiry = [DateTimeOffset]::Parse($Matches[1])
    Assert ($expiry -gt [DateTimeOffset]::UtcNow -and $expiry -le [DateTimeOffset]::UtcNow.AddHours(24)) 'expiry is within 24 hours'
} else { Assert $false 'one file tree was generated' }
Assert (@($calls | Where-Object { $_ -match '^pr create .*--draft' -and $_ -match '--label fixes-main' }).Count -eq 1) 'PR is draft and fixes-main'
Assert (@($calls | Where-Object { $_ -match '^issue comment .*quarantine-pr: test=A.B.C.Works run=100 attempt=2 job=2' }).Count -eq 1) 'tracking receipt retains the per-test run attempt job shape'
foreach ($change in @(
    @{ enabled = 'off' }, @{ attempt = 1 }, @{ attempt = 3 }, @{ branch = 'feature' }, @{ head = 'newer' },
    @{ logs = @('  Failed A.B.C.Works [1 ms]', "  Failed A.B.C.Works [1 ms]`n  Failed A.B.C.Other [1 ms]") },
    @{ logs = @('  Failed A.B.C.Other [1 ms]', '  Failed A.B.C.Works [1 ms]') },
    @{ logs = @('error CS1002', '  Failed A.B.C.Works [1 ms]') },
    @{ logs = @('  Failed A.B.C.Works [1 ms]', 'build failed') },
    @{ ambiguous = $true }, @{ truncated = $true },
    @{ source = $source.Replace('[Fact]', '[Fact(Skip = "existing")]') },
    @{ source = $source.Replace('[Fact]', '[Fact(SkipUnless = nameof(Ready))]') },
    @{ source = $source.Replace('Works()', 'SomethingElse()') }
)) {
    $x = Scenario $change
    Assert ($x.Error -and $x.Writes.Count -eq 0) ("unsafe evidence refuses before writes: " + ($change | ConvertTo-Json -Compress))
}
$x = Scenario @{} -Hours 25
Assert ($x.Error -and $x.Writes.Count -eq 0) '25-hour request refuses rather than clamps'
$issue = @{ number = 42; title = 'flaky A.B.C.Works'; body = ''; author = @{ login = 'danswett' } }
$x = Scenario @{ issues = @($issue) }
Assert (-not $x.Error -and @($calls | Where-Object { $_ -like 'issue create*' }).Count -eq 0) 'an existing trusted flake issue is reused'
$x = Scenario @{ prs = @(@{ number = 43; url = 'https://github.com/r/pull/43'; isDraft = $true
    baseRefName = 'main'; body = 'quarantine-pr: test=A.B.C.Works issue=42' }) }
Assert (-not $x.Error -and $x.Writes.Count -eq 0 -and $x.Result.Pr -eq 'https://github.com/r/pull/43') 'repeat invocation reuses the open draft without writes'
$x = Scenario @{ source = $source.Replace('[Fact]', "[Theory]`n    [InlineData(1)]") }
Assert (-not $x.Error -and @($bodies | Where-Object { $_.PSObject.Properties['base_tree'] })[0].tree[0].content -match '\[Theory\(Skip = "') 'Theory with data retains data and gains one Skip'
if ($script:failures) { throw "$script:failures quarantine check(s) failed" }
'quarantine-pr: all offline checks passed'
