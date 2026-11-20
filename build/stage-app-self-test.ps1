#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$out = Join-Path $Root 'public-selftest'
New-Item -ItemType Directory -Path $out -ErrorAction Stop | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function Read-DiagnosticText([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    for ($item = $file.Directory; $null -ne $item; $item = $item.Parent) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked diagnostic directory refused' }
    }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked diagnostic input refused' }
    $text = $utf8.GetString([IO.File]::ReadAllBytes($file.FullName)).TrimStart([char]0xFEFF)
    if ($text -match '[\x00-\x08\x0B\x0C\x0E-\x1F]' -or $text -match '^(MDMP|PAGEDUMP|PAGEDU64|PK\x03\x04|\x7FELF)') {
        throw 'Binary diagnostic input refused'
    }
    return $text
}
foreach ($attempt in 'selftest', 'selftest-retry') {
    $source = Join-Path $Root $attempt
    $destination = $out
    if ($attempt -eq 'selftest-retry') {
        $destination = Join-Path $out 'retry'
        New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
    }
    foreach ($name in 'result.json', 'app-exit-cause.json', 'app.stderr.log', 'app.log', 'app-crash-analysis.txt') {
        $path = Join-Path $source $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $text = Read-DiagnosticText $path
        if ($name.EndsWith('.json')) { $null = ConvertFrom-Json -InputObject $text -ErrorAction Stop }
        [IO.File]::WriteAllText((Join-Path $destination $name), $text, $utf8)
    }
}
