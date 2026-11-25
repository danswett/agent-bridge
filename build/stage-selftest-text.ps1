# Stages NAMED diagnostic text from a runner folder for a public artifact: never WER dumps, archives or links (the one place these
# refusals live; ci.yml and full.yml both call it). Throws on any refusal, so the staging step fails and no artifact is uploaded.
# -Names are relative to -Source and are copied, in order, into -Out (a new folder). Absent names are skipped.
#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Out, [Parameter(Mandatory)][string[]]$Names)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Out -ErrorAction Stop | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function Read-DiagnosticText([string]$path) {
    $file = Get-Item -LiteralPath $path
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
foreach ($name in $Names) {
    $path = Join-Path $Source $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $text = Read-DiagnosticText $path
    if ($name.EndsWith('.json')) { $null = ConvertFrom-Json -InputObject $text -ErrorAction Stop }
    $target = Join-Path $Out $name
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    [IO.File]::WriteAllText($target, $text, $utf8)
}
