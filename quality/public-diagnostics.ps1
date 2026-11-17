#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Out,
    [switch]$Installed
)
$ErrorActionPreference = 'Stop'
$limit = 64KB
$utf8 = [Text.UTF8Encoding]::new($false, $true)

function Assert-UnlinkedDiagnosticPath([IO.FileSystemInfo]$item) {
    while ($null -ne $item) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked diagnostic path refused'
        }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}

function Read-DiagnosticText([string]$path) {
    $file = Get-Item -LiteralPath $path
    Assert-UnlinkedDiagnosticPath $file
    $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'Read')
    try {
        if ($stream.Length -gt $limit) { throw 'Diagnostic input exceeds the 64 KiB public limit' }
        # Bound the read too: a growing input cannot defeat the length check.
        $bytes = [byte[]]::new($limit + 1)
        $count = $stream.ReadAtLeast($bytes, $bytes.Length, $false)
        if ($count -gt $limit) { throw 'Diagnostic input exceeds the 64 KiB public limit' }
        $text = $utf8.GetString($bytes, 0, $count).TrimStart([char]0xFEFF)
    } finally { $stream.Dispose() }
    if ($text -match '[\x00-\x08\x0B\x0C\x0E-\x1F]' -or $text -match '^(MDMP|PAGEDUMP|PAGEDU64|PK\x03\x04|\x7FELF)') {
        throw 'Binary diagnostic input refused'
    }
    return $text
}

function ConvertTo-PublicDiagnosticValue($value) {
    if ($value -is [System.Collections.IDictionary]) {
        $safe = [ordered]@{}
        foreach ($key in $value.Keys) {
            # Unknown property names can themselves contain user text or credentials.
            if ($key -cnotin @('schemaVersion', 'failed', 'passed', 'processId', 'exitCode',
                    'events', 'eventId', 'recordId', 'provider', 'message', 'stderr', 'captureErrors',
                    'checks', 'failures', 'unfinished', 'version', 'runtime', 'commit', 'runId',
                    'files', 'size', 'sha256', 'path', 'installer', 'archives', 'installedFiles', 'name')) { continue }
            $safe[$key] = ConvertTo-PublicDiagnosticValue $value[$key]
        }
        return $safe
    }
    if ($value -is [array]) {
        return ,@(foreach ($child in $value) { ,(ConvertTo-PublicDiagnosticValue $child) })
    }
    # Blacklists cannot reliably distinguish runner/user text from diagnostic messages.
    if ($value -is [string] -or $value -is [datetime] -or $value -is [datetimeoffset]) { return '[redacted]' }
    return $value
}

$outPath = [IO.Path]::GetFullPath($Out)
Assert-UnlinkedDiagnosticPath ([IO.DirectoryInfo]::new($outPath))
# A fresh directory prevents stale or unreviewed content joining the upload.
New-Item -ItemType Directory -Path $outPath -ErrorAction Stop | Out-Null
$names = if ($Installed) {
    @('installer-input.json', 'installed/result.json', 'installed/app-exit-cause.json',
        'installed/app.stderr.log', 'installed/app.log', 'installed/setup.log',
        'installed/distribution-evidence.json', 'installed/app-crash-analysis.txt')
} else {
    @('result.json', 'app-exit-cause.json', 'app.stderr.log', 'app.log', 'app-crash-analysis.txt')
}
foreach ($name in $names) {
    $path = Join-Path $Source $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $text = Read-DiagnosticText $path
    if ($name.EndsWith('.json')) {
        $value = ConvertFrom-Json -InputObject $text -AsHashtable -Depth 16 -ErrorAction Stop
        if ($value -isnot [System.Collections.IDictionary]) { throw 'Diagnostic JSON must be an object' }
        $text = ConvertTo-Json -InputObject (ConvertTo-PublicDiagnosticValue $value) -Depth 16 -Compress
    } else {
        $code = if ($name.EndsWith('app-crash-analysis.txt')) {
            [regex]::Match($text, '(?m)^EXCEPTION_CODE_STR:[ \t]*(?:0x)?([0-9a-fA-F]{8})[ \t]*\r?$')
        }
        $text = 'Free text withheld for public CI diagnostics.'
        if ($code -and $code.Success) { $text += "`nEXCEPTION_CODE_STR: 0x" + $code.Groups[1].Value }
    }
    if ($utf8.GetByteCount($text) -gt $limit) { throw 'Diagnostic output exceeds the 64 KiB public limit' }
    $target = Join-Path $outPath $name
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $text, $utf8)
}
