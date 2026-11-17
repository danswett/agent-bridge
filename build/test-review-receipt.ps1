#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tool = Join-Path $PSScriptRoot 'review-receipt.ps1'
$fixtures = Join-Path $PSScriptRoot 'fixtures' 'review-receipt'
$clean = Join-Path $fixtures 'clean.json'
$findings = Join-Path $fixtures 'findings.json'
$head = 'a' * 40
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("review-receipt-" + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$failures = 0

function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "PASS $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Invoke-Receipt([string]$action, [string]$ledger, [string]$evidence = $clean, [string]$expectedHead = $head) {
    $output = & pwsh -NoProfile -File $tool -Action $action -Ledger $ledger -Head $expectedHead -Evidence $evidence 2>&1
    @{ Code = $LASTEXITCODE; Text = ($output -join "`n") }
}
function Passes($result) { Assert ($result.Code -eq 0) $result.Text }
function Refuses($result, [string]$reason) {
    Assert ($result.Code -ne 0 -and $result.Text -match $reason) "Expected refusal '$reason': $($result.Text)"
}

try {
    Check 'Recording binds the head, reviewer family, verdict and file list to evidence' {
        $ledger = Join-Path $scratch 'record.jsonl'
        Passes (Invoke-Receipt Record $ledger)
        $receipt = Get-Content -LiteralPath $ledger -Raw | ConvertFrom-Json
        $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes("AGENTS.md`nbuild/review-receipt.ps1"))).ToLowerInvariant()
        Assert ($receipt.head -ceq $head -and $receipt.reviewerFamily -ceq 'OpenAI' -and
            $receipt.verdict -ceq 'CLEAN' -and $receipt.fileListHash -ceq $expectedHash -and
            $receipt.evidenceHash -ceq (Get-FileHash -LiteralPath $clean).Hash.ToLowerInvariant()) 'Receipt lost its evidence binding'
    }
    Check 'Verification succeeds without changing the ledger' {
        $ledger = Join-Path $scratch 'verify.jsonl'
        Passes (Invoke-Receipt Record $ledger)
        $before = (Get-FileHash -LiteralPath $ledger).Hash
        Passes (Invoke-Receipt Verify $ledger)
        Assert ((Get-FileHash -LiteralPath $ledger).Hash -ceq $before) 'Verification wrote to the ledger'
    }
    Check 'A different expected head is refused on record and verify' {
        $ledger = Join-Path $scratch 'head.jsonl'
        Refuses (Invoke-Receipt Record $ledger $clean ('b' * 40)) 'head'
        Assert (-not (Test-Path -LiteralPath $ledger)) 'Wrong-head record created a ledger'
        Passes (Invoke-Receipt Record $ledger)
        Refuses (Invoke-Receipt Verify $ledger $clean ('b' * 40)) 'head'
        $otherHead = Join-Path $scratch 'other-head.json'
        [IO.File]::WriteAllText($otherHead, [IO.File]::ReadAllText($clean).Replace($head, ('b' * 40)))
        Refuses (Invoke-Receipt Verify $ledger $otherHead ('b' * 40)) 'no receipt'
    }
    Check 'A duplicate head and family is refused without changing prior bytes' {
        $ledger = Join-Path $scratch 'duplicate.jsonl'
        Passes (Invoke-Receipt Record $ledger)
        $before = (Get-FileHash -LiteralPath $ledger).Hash
        Refuses (Invoke-Receipt Record $ledger) 'Duplicate'
        Assert ((Get-FileHash -LiteralPath $ledger).Hash -ceq $before) 'Duplicate altered the ledger'
    }
    Check 'Another family appends without rewriting the existing receipt' {
        $ledger = Join-Path $scratch 'append.jsonl'
        Passes (Invoke-Receipt Record $ledger)
        $before = [IO.File]::ReadAllText($ledger)
        Passes (Invoke-Receipt Record $ledger $findings)
        Assert ([IO.File]::ReadAllText($ledger).StartsWith($before, [StringComparison]::Ordinal)) 'Append rewrote history'
        Assert (@(Get-Content -LiteralPath $ledger).Count -eq 2) 'Append did not add one receipt'
        Passes (Invoke-Receipt Verify $ledger)
        Passes (Invoke-Receipt Verify $ledger $findings)
    }
    Check 'Tampered ledger entries prevent both verification and further recording' {
        $ledger = Join-Path $scratch 'tampered.jsonl'
        Passes (Invoke-Receipt Record $ledger)
        $text = [IO.File]::ReadAllText($ledger).Replace('"CLEAN"', '"FINDINGS"')
        [IO.File]::WriteAllText($ledger, $text)
        Refuses (Invoke-Receipt Verify $ledger) 'Ledger'
        Refuses (Invoke-Receipt Record $ledger $findings) 'Ledger'
        Assert ([IO.File]::ReadAllText($ledger) -ceq $text) 'Refusal altered corrupt history'
    }
    Check 'Changed review evidence does not verify an existing receipt' {
        $ledger = Join-Path $scratch 'evidence.jsonl'
        $evidence = Join-Path $scratch 'changed.json'
        Passes (Invoke-Receipt Record $ledger)
        [IO.File]::WriteAllText($evidence, [IO.File]::ReadAllText($clean).Replace('AGENTS.md', 'CONTRIBUTING.md'))
        Refuses (Invoke-Receipt Verify $ledger $evidence) 'evidence'
    }
    Check 'Missing or truncated ledgers fail verification instead of passing empty history' {
        $ledger = Join-Path $scratch 'truncated.jsonl'
        Refuses (Invoke-Receipt Verify $ledger) 'ledger'
        Passes (Invoke-Receipt Record $ledger)
        $text = [IO.File]::ReadAllText($ledger)
        [IO.File]::WriteAllText($ledger, $text.Substring(0, $text.Length - 2))
        Refuses (Invoke-Receipt Verify $ledger) 'Ledger'
    }
    Check 'Incomplete evidence and ambiguous file lists are refused before creating a ledger' {
        $ledger = Join-Path $scratch 'invalid.jsonl'
        $evidence = Join-Path $scratch 'invalid.json'
        foreach ($change in @(
            { param($r) $r.Remove('reviewerFamily') },
            { param($r) $r.verdict = 'UNKNOWN' },
            { param($r) $r.files = @() },
            { param($r) $r.files = @('AGENTS.md', 'AGENTS.md') },
            { param($r) $r.files = @('../AGENTS.md') },
            { param($r) $r.files = @("one`ntwo") }
        )) {
            $review = Get-Content -LiteralPath $clean -Raw | ConvertFrom-Json -AsHashtable
            & $change $review
            [IO.File]::WriteAllText($evidence, ($review | ConvertTo-Json -Compress))
            Refuses (Invoke-Receipt Record $ledger $evidence) 'Evidence'
            Assert (-not (Test-Path -LiteralPath $ledger)) 'Invalid evidence created a ledger'
        }
    }
}
finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
