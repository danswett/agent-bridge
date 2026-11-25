# FROZEN ORACLE (delete at 6b): ci.yml's inline "Stage named installed self-test text" step as it was before build/stage-selftest-text.ps1,
# verbatim except that the source and output folders and the name list come from the environment (STAGE_SOURCE, STAGE_OUT, STAGE_NAMES, ';' separated).
# Only the equivalence test runs it; nothing else may, and it must not gain logic.
$ErrorActionPreference = 'Stop'
$source = $env:STAGE_SOURCE
$out = $env:STAGE_OUT
New-Item -ItemType Directory -Path $out -ErrorAction Stop | Out-Null
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
foreach ($name in ($env:STAGE_NAMES -split ';')) {
  $path = Join-Path $source $name
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
  $text = Read-DiagnosticText $path
  if ($name.EndsWith('.json')) { $null = ConvertFrom-Json -InputObject $text -ErrorAction Stop }
  $target = Join-Path $out $name
  New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
  [IO.File]::WriteAllText($target, $text, $utf8)
}
