#Requires -Version 7
# Offline checks of quality/post-receipt.ps1: argument refusals and a dry run with a throwaway key.
# Nothing here reaches GitHub or touches the real App key.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'post-receipt.ps1'
$head = 'a' * 40
$comment = 'https://github.com/danswett/agent-bridge/pull/7#issuecomment-123'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("post-receipt-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$failures = 0

function Assert([bool]$Ok, [string]$Name) {
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}

function Invoke-Script([hashtable]$Overrides, [string]$Key) {
    $a = @{ Pr = 7; Head = $head; CommentUrl = $comment; Receipt = 'independent-review=claude-sonnet-5.5', 'independent-review-2=gemini-3.8-flash' }
    foreach ($k in $Overrides.Keys) { $a[$k] = $Overrides[$k] }
    $argList = @('-NoProfile', '-File', $script, '-DryRun', '-KeyPath', $Key)
    foreach ($k in $a.Keys) { $argList += "-$k"; $argList += @($a[$k]) -join ',' }
    $out = & pwsh @argList 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $out }
}

try {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $pem = $rsa.ExportRSAPrivateKeyPem()
    $key = Join-Path $tmp 'test.pem'
    Set-Content -Path $key -Value $pem -NoNewline
    $keyBody = ($pem -split "`n")[1].Trim()

    $r = Invoke-Script @{} $key
    Assert ($r.Exit -eq 0) 'dry run exits 0'
    Assert ($r.Out -match 'independent-review = success: Reviewed by claude-sonnet-5\.5; read in full, no findings') 'dry run names the first receipt'
    Assert ($r.Out -match 'independent-review-2 = success: Reviewed by gemini-3\.8-flash') 'dry run names the second receipt'
    Assert ($r.Out -match 'aaaaaaaa \(PR 7\)') 'dry run shows the head'
    Assert (-not $r.Out.Contains($keyBody) -and $r.Out -notmatch 'PRIVATE KEY') 'dry run never prints key material'

    foreach ($case in @(
            @{ Name = 'a short head'; Over = @{ Head = 'abc1234' } },
            @{ Name = 'an uppercase head'; Over = @{ Head = 'A' * 40 } },
            @{ Name = 'a comment on another PR'; Over = @{ CommentUrl = 'https://github.com/danswett/agent-bridge/pull/8#issuecomment-1' } },
            @{ Name = 'a comment on another repo'; Over = @{ CommentUrl = 'https://github.com/evil/agent-bridge/pull/7#issuecomment-1' } },
            @{ Name = 'a non-review context'; Over = @{ Receipt = 'merge-rule=claude-sonnet-5.5' } },
            @{ Name = 'a context named twice'; Over = @{ Receipt = 'independent-review=a', 'independent-review=b' } },
            @{ Name = 'a model with spaces'; Over = @{ Receipt = 'independent-review=claude sonnet' } },
            @{ Name = 'a long summary'; Over = @{ Summary = 'x' * 61 } })) {
        $r = Invoke-Script $case.Over $key
        Assert ($r.Exit -ne 0 -and $r.Out -notmatch 'jwt ok') "refuses $($case.Name)"
    }

    $r = Invoke-Script @{} (Join-Path $tmp 'missing.pem')
    Assert ($r.Exit -ne 0 -and $r.Out -match 'No App key found' -and $r.Out -notmatch 'jwt ok') 'refuses a missing key'
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures) { "$failures failure(s)"; exit 1 }
'post-receipt: all checks passed'
