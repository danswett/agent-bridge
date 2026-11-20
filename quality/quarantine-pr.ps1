#Requires -Version 7
[CmdletBinding()]
param([long]$RunId, [string]$TestName, [string]$Repository = $env:GITHUB_REPOSITORY, [double]$Hours = 24)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'rerun-known-flakes.ps1') -RunId $RunId -Repository $Repository

function Assert-QuarantineEnabled([string]$Repository) {
    if ((Invoke-Gh variable get MAIN_RED_QUARANTINE --repo $Repository) -cne 'on') { throw 'MAIN_RED_QUARANTINE is not on; quarantine generation is disabled.' }
}
function Get-QuarantineApi([string]$Repository, [string]$Path) {
    Invoke-Gh api "repos/$Repository/$Path" | ConvertFrom-Json
}
function Write-QuarantineApi([string]$Repository, [string]$Path, $Body) {
    Assert-QuarantineEnabled $Repository
    $global:LASTEXITCODE = 0
    $response = ($Body | ConvertTo-Json -Depth 8 -Compress) | & $script:GhCommand api "repos/$Repository/$Path" --method POST --input -
    if ($LASTEXITCODE -ne 0) { throw "Quarantine git data request failed: $Path" }
    $response | ConvertFrom-Json
}
function Get-QuarantineEdit([string]$Source, [string]$TestName, [string]$Reason) {
    if ($TestName.Contains(':')) {
        $lineNumber = [int]($TestName -split ':')[-1] - 1
        $lines = [regex]::Split($Source, '(?<=\n)')
        if ($lineNumber -ge $lines.Count) { throw 'Playwright declaration line is absent.' }
        $line = $lines[$lineNumber]; $text = $line.TrimEnd("`r", "`n")
        if ($text -notmatch '^[ \t]*test\(' -or [regex]::Matches($text, '\btest(?:\.\w+)?\s*\(').Count -ne 1) {
            throw 'Playwright declaration is skipped, unsupported or ambiguous.'
        }
        $lines[$lineNumber] = ($text -replace '^([ \t]*)test\(', '$1test.skip(') + " // $Reason" + $line.Substring($text.Length)
        return $lines -join ''
    }
    $parts = $TestName.Split('.'); $method = [regex]::Escape($parts[-1]); $class = [regex]::Escape($parts[-2])
    $ns = [regex]::Escape(($parts[0..($parts.Length - 3)] -join '.'))
    if ([regex]::Matches($Source, '\b(?:class|struct|record)\s+\w+').Count -ne 1 -or
        $Source -notmatch "\bclass\s+$class\b" -or [regex]::Matches($Source, '\bnamespace\s+[\w.]+').Count -ne 1 -or
        $Source -notmatch "\bnamespace\s+$ns\s*[;{]") { throw 'Test type or namespace is ambiguous.' }
    $pattern = '(?m)\[(?<kind>Fact|Theory)(?<args>\([^\]\r\n]*\))?\](?=(?:\r?\n[ \t]*\[(?!Fact|Theory)[^\r\n]+\])*\r?\n[ \t]*public\s+(?:async\s+)?(?:void|Task|ValueTask)(?:<[^>\r\n]+>)?\s+' + $method + '\s*\()'
    $matches = [regex]::Matches($Source, $pattern)
    if ($matches.Count -ne 1) { throw 'Expected exactly one supported xUnit test declaration.' }
    $m = $matches[0]; $arguments = $m.Groups['args'].Value
    if ($arguments -match '\b(?:Skip\w*|Explicit)\s*=') { throw 'Test already skipped or conditionally skipped.' }
    $existing = if ($arguments) { $arguments.Substring(1, $arguments.Length - 2).Trim() } else { '' }
    $prefix = if ($existing) { "$existing, " } else { '' }
    $attribute = '[' + $m.Groups['kind'].Value + '(' + $prefix + 'Skip = "' + $Reason + '")]'
    $Source.Remove($m.Index, $m.Length).Insert($m.Index, $attribute)
}
function Invoke-QuarantinePr([long]$RunId, [string]$TestName, [string]$Repository, [double]$Hours = 24) {
    if ($RunId -le 0 -or $Repository -notmatch '^[\w.-]+(?:/[\w.-]+)?$' -or
        $TestName -notmatch '^(?:[A-Za-z_]\w*(?:\.[A-Za-z_]\w*){2,}|(?:[\w-]+/)*[\w-]+\.spec\.mjs:[1-9]\d*)$' -or
        -not [double]::IsFinite($Hours) -or $Hours -le 0 -or $Hours -gt 24) { throw 'Require run, repository, supported test name and expiry >0 and <=24 hours.' }
    Assert-QuarantineEnabled $Repository
    $run = Get-QuarantineApi $Repository "actions/runs/$RunId"
    if ($run.path -ne '.github/workflows/ci.yml' -or $run.event -ne 'push' -or $run.head_branch -ne 'main' -or
        $run.head_repository.full_name -ne $Repository -or $run.status -ne 'completed' -or
        $run.conclusion -ne 'failure' -or $run.run_attempt -ne 2) { throw 'Require completed failed main CI rerun attempt 2.' }
    $head = (Get-QuarantineApi $Repository 'git/ref/heads/main').object.sha
    if ($head -ne $run.head_sha) { throw 'Main moved since the failing run; refuse stale quarantine.' }
    $receipts = @()
    foreach ($attempt in 1, 2) {
        $page = Get-QuarantineApi $Repository "actions/runs/$RunId/attempts/$attempt/jobs?per_page=100"
        $jobs = @($page.jobs | Where-Object name -NE 'quality-gate')
        if ($page.total_count -gt 100 -or @($page.jobs).Count -ne $page.total_count -or $jobs.Count -eq 0 -or
            @($jobs | Where-Object conclusion -NotIn @('success', 'skipped', 'neutral', 'failure')).Count) { throw 'Incomplete, cancelled or infrastructure failure evidence.' }
        $failed = @($jobs | Where-Object conclusion -EQ 'failure')
        if (-not $failed.Count) { throw 'No failed test job in attempt.' }
        foreach ($job in $failed) {
            $log = (Invoke-Gh api "repos/$Repository/actions/jobs/$($job.id)/logs" --allow-escape-sequences) -join "`n"
            $tests = Get-FailedTests $log
            if ($tests.Count -ne 1 -or $tests[0] -cne $TestName) { throw 'Require the same single named test in both attempts, with no uncovered failed job.' }
            $receipts += "quarantine-pr: test=$TestName run=$RunId attempt=$attempt job=$($job.id)"
        }
    }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($TestName))).Substring(0, 16).ToLowerInvariant()
    $branch = "quarantine/main-red-$hash"; $marker = "quarantine-pr: test=$TestName issue="
    $prs = @(Invoke-Gh pr list --repo $Repository --head $branch --state open --limit 2 --json 'number,url,isDraft,body,baseRefName' | ConvertFrom-Json)
    if ($prs.Count) {
        if ($prs.Count -ne 1 -or -not $prs[0].isDraft -or $prs[0].baseRefName -ne 'main' -or
            -not $prs[0].body.Contains($marker)) { throw 'Existing PR is not the expected draft quarantine.' }
        return [pscustomobject]@{ Pr = $prs[0].url; Reused = $true }
    }
    $tree = Get-QuarantineApi $Repository "git/trees/${head}?recursive=1"
    if ($tree.truncated) { throw 'Incomplete tree; refuse ambiguous test lookup.' }
    $playwright = $TestName.Contains(':')
    $fileName = if ($playwright) { ($TestName -split ':')[0] } else { $TestName.Split('.')[-2] + '.cs' }
    $files = @($tree.tree | Where-Object {
        $_.type -eq 'blob' -and $_.mode -eq '100644' -and $(if ($playwright) {
            $_.path -clike 'pwa/test/*.spec.mjs' -and $_.path.EndsWith("/$fileName", [StringComparison]::Ordinal)
        } else { $_.path -clike 'tests/*' -and ($_.path -split '/')[-1] -ceq $fileName })
    })
    if ($files.Count -ne 1) { throw 'Test file cannot be found uniquely.' }
    $file = $files[0]
    if ($file.path -ceq 'tests/AgentBridge.Quality.Tests/QuarantineExpiryTests.cs') { throw 'Quarantine expiry enforcement cannot quarantine itself.' }
    $blob = Get-QuarantineApi $Repository "git/blobs/$($file.sha)"
    if ($blob.encoding -ne 'base64') { throw 'Unsupported source encoding.' }
    $source = [Text.UTF8Encoding]::new($false, $true).GetString([Convert]::FromBase64String($blob.content))
    $expiry = [DateTimeOffset]::UtcNow.AddHours($Hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    $null = Get-QuarantineEdit $source $TestName "quarantine-expires=$expiry; issue #1"
    $issues = @(Invoke-Gh issue list --repo $Repository --label flaky --state open --limit 200 --json 'number,title,body,author' | ConvertFrom-Json)
    if ($issues.Count -ge 200) { throw 'Incomplete flake issue inventory.' }
    $issue = Find-FlakyIssue $TestName $issues
    $evidence = "Test: $TestName`nSignature: Failed $TestName`nExpiry: $expiry`n" +
        "Run: https://github.com/$Repository/actions/runs/$RunId (attempts 1 and 2)`n" +
        "Owner: Quality, pending fix DRI assignment. To restore: revert this PR.`n" + ($receipts -join "`n")
    Assert-QuarantineEnabled $Repository
    if ($null -eq $issue) {
        $url = Invoke-Gh issue create --repo $Repository --title "Quarantine $TestName (main-red)" --body $evidence --label 'quarantined,flaky,fixes-main,priority:P0'
        if ($url -notmatch '/issues/(\d+)$') { throw 'Tracking issue creation was not confirmed.' }
        $issue = [pscustomobject]@{ number = [int]$Matches[1] }
    } else {
        Invoke-Gh issue edit $issue.number --repo $Repository --add-label 'quarantined,flaky,fixes-main,priority:P0' | Out-Null
    }
    $number = $issue.number; $reason = "quarantine-expires=$expiry; issue #$number"
    $edited = Get-QuarantineEdit $source $TestName $reason
    Assert-QuarantineEnabled $Repository
    Invoke-Gh issue comment $number --repo $Repository --body $evidence | Out-Null
    $newTree = Write-QuarantineApi $Repository 'git/trees' @{ base_tree = $tree.sha; tree = @(@{ path = $file.path; mode = $file.mode; type = 'blob'; content = $edited }) }
    $commit = Write-QuarantineApi $Repository 'git/commits' @{ message = "Quarantine $TestName (issue #$number)"; tree = $newTree.sha; parents = @($head) }
    $null = Write-QuarantineApi $Repository 'git/refs' @{ ref = "refs/heads/$branch"; sha = $commit.sha }
    $body = "$marker$number`n$evidence`nTracking: https://github.com/$Repository/issues/$number`n" +
        "WHOLE vs parent: 2 (parent $head); exception: none`n" +
        'One Skip only. Required: independent Sol and Sonnet exact-head reviews; Quality alone readies, arms and merges. ' +
        'Owner/Chief may close this draft or disable MAIN_RED_QUARANTINE. Main-Green and Chief only; no phone notifications.'
    Assert-QuarantineEnabled $Repository
    $pr = Invoke-Gh pr create --repo $Repository --base main --head $branch --title "Quarantine $TestName (issue #$number)" --body $body --draft --label fixes-main
    [pscustomobject]@{ Pr = $pr; Issue = $number; Expiry = $expiry; Head = $commit.sha; Reused = $false }
}
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-QuarantinePr -RunId $RunId -TestName $TestName -Repository $Repository -Hours $Hours
}
