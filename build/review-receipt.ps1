#Requires -Version 7
param(
    [Parameter(Mandatory)][ValidateSet('Record', 'Verify')][string]$Action,
    [Parameter(Mandatory)][string]$Ledger,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Head,
    [Parameter(Mandatory)][string]$Evidence
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Head = $Head.ToLowerInvariant()
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$fields = @('head', 'reviewerFamily', 'verdict', 'fileListHash', 'evidenceHash', 'previous')

function Get-Hash([byte[]]$bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
function Get-ReceiptHash($receipt) {
    $payload = [ordered]@{}
    foreach ($field in $fields) { $payload[$field] = $receipt[$field] }
    Get-Hash ($utf8.GetBytes(($payload | ConvertTo-Json -Compress)))
}
function Test-ReviewFields($review) {
    $review -is [Collections.IDictionary] -and
        $review['head'] -is [string] -and $review['head'] -cmatch '^[0-9a-f]{40}$' -and
        $review['reviewerFamily'] -is [string] -and $review['reviewerFamily'] -cmatch '^[A-Za-z][A-Za-z0-9.-]{0,63}$' -and
        $review['verdict'] -is [string] -and $review['verdict'] -cin @('CLEAN', 'FINDINGS')
}
function Get-Key($review) { "$($review['head'])/$($review['reviewerFamily'].ToLowerInvariant())" }

try {
    $evidenceBytes = [IO.File]::ReadAllBytes($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Evidence))
    $review = $utf8.GetString($evidenceBytes) | ConvertFrom-Json -AsHashtable
    if (-not (Test-ReviewFields $review)) { throw 'Evidence requires a full lowercase head, reviewerFamily and CLEAN or FINDINGS verdict.' }
    if ($review['head'] -cne $Head) { throw 'Evidence head differs from the expected head.' }
    if ($review['files'] -isnot [array] -or $review['files'].Count -eq 0) { throw 'Evidence requires a nonempty files array.' }
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $review['files']) {
        if ($file -isnot [string] -or $file -cnotmatch '^[^/\\:\x00-\x1f]+(?:/[^/\\:\x00-\x1f]+)*$' -or
            @($file.Split('/') | Where-Object { $_ -in '.', '..' }).Count -gt 0 -or -not $files.Add($file)) {
            throw 'Evidence files must be unique repository-relative paths with forward slashes and no controls or dot segments.'
        }
    }
    $sorted = [string[]]@($files)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $wanted = [ordered]@{
        head = $Head
        reviewerFamily = $review['reviewerFamily']
        verdict = $review['verdict']
        fileListHash = Get-Hash ($utf8.GetBytes(($sorted -join "`n")))
        evidenceHash = Get-Hash $evidenceBytes
        previous = '0' * 64
    }
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Ledger)
    if ($Action -eq 'Verify' -and -not [IO.File]::Exists($path)) { throw 'Ledger does not exist.' }
    $mode = if ($Action -eq 'Record') { [IO.FileMode]::OpenOrCreate } else { [IO.FileMode]::Open }
    $access = if ($Action -eq 'Record') { [IO.FileAccess]::ReadWrite } else { [IO.FileAccess]::Read }
    $stream = [IO.FileStream]::new($path, $mode, $access, [IO.FileShare]::None)
    try {
        $reader = [IO.StreamReader]::new($stream, $utf8, $false, 1024, $true)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { throw 'Ledger has an incomplete final entry.' }
        $previous = '0' * 64
        $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $match = $null
        $lines = $text.Split("`n")
        for ($i = 0; $i -lt $lines.Count - 1; $i++) {
            try { $entry = $lines[$i] | ConvertFrom-Json -AsHashtable }
            catch { throw "Ledger entry $($i + 1) is not valid JSON." }
            if (-not (Test-ReviewFields $entry) -or $entry.Count -ne $fields.Count + 1) { throw 'Ledger has an invalid receipt schema.' }
            foreach ($field in 'fileListHash', 'evidenceHash', 'previous', 'hash') {
                if ($entry[$field] -isnot [string] -or $entry[$field] -cnotmatch '^[0-9a-f]{64}$') { throw "Ledger has an invalid $field." }
            }
            if ($entry['previous'] -cne $previous -or $entry['hash'] -cne (Get-ReceiptHash $entry)) { throw 'Ledger hash chain is invalid.' }
            if (-not $keys.Add((Get-Key $entry))) { throw 'Ledger contains a duplicate head and family.' }
            $previous = $entry['hash']
            if ((Get-Key $entry) -ceq (Get-Key $wanted)) { $match = $entry }
        }
        if ($Action -eq 'Verify') {
            if ($null -eq $match) { throw 'Ledger has no receipt for this evidence head and reviewer family.' }
            foreach ($field in 'verdict', 'fileListHash', 'evidenceHash') {
                if ($match[$field] -cne $wanted[$field]) { throw "Receipt differs from evidence: $field." }
            }
            Write-Output "Verified $Head ($($wanted.reviewerFamily), $($wanted.verdict))."
        } else {
            if ($null -ne $match) { throw 'Duplicate receipt for this head and reviewer family.' }
            $wanted.previous = $previous
            $wanted['hash'] = Get-ReceiptHash $wanted
            $bytes = $utf8.GetBytes(($wanted | ConvertTo-Json -Compress) + "`n")
            [void]$stream.Seek(0, [IO.SeekOrigin]::End)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
            Write-Output "Recorded $Head ($($wanted.reviewerFamily), $($wanted.verdict))."
        }
    } finally { $stream.Dispose() }
} catch {
    [Console]::Error.WriteLine("review-receipt: $($_.Exception.Message)")
    exit 1
}
