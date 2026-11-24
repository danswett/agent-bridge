# Probe how a Claude Code (or Codex) console takes typed text, with SYNTHETIC marker text only, in a session
# of your own, never the owner's or another agent's: nothing is submitted (no Enter), the box is read back
# from the screen and then cleared (docs/host-replies.md, "Long text (Claude)").
#
#   pwsh -NoProfile -Command "& ops/tools/claude-typing-probe.ps1 -TargetPid <your claude pid> -Injector <agent-bridge-injector.exe> -Sizes 400,1000,4500"
#
# Build the injector once: dotnet build src/AgentBridge.Injector -c Release. For each size it types the
# marker text as one burst and as 100-unit chunks 40 ms apart, and prints whether the box shows a
# "[Pasted text]" placeholder and which markers it shows. The box shows only its last rows, so the proof of
# a whole arrival is the last marker and no placeholder; the head is the first row shown.
[CmdletBinding()]
param([Parameter(Mandatory)][int]$TargetPid, [Parameter(Mandatory)][string]$Injector, [int[]]$Sizes = @(400, 1000, 4500), [int]$Chunk = 100, [int]$PauseMs = 40)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ("typing-probe-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $work
function B64($t) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($t)) }
function Inject([string]$script, [string[]]$more = @()) {
    Set-Content "$work/in" $script -NoNewline -Encoding utf8NoBOM
    $p = Start-Process $Injector -ArgumentList (@("$TargetPid") + $more) -NoNewWindow -PassThru -RedirectStandardInput "$work/in" -RedirectStandardOutput "$work/out" -RedirectStandardError "$work/err"
    if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force; throw 'the injector did not finish' }
    @(Get-Content "$work/out")
}
function Screen { @(Inject '' '--screen' | Where-Object { $_ -like 'row *' } | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($_ -split ' ', 2)[1])) }) }
function Clear-Box {
    $b = B64 (([string][char]21) * 4)
    1..4 | ForEach-Object { $null = Inject ("agent-bridge-keys 2`n" + ((1..40 | ForEach-Object { "send $b 0 0 80" }) -join "`n") + "`n") }
}
function Probe([string]$label, [string[]]$pieces, [int]$pause) {
    $lines = @($pieces | ForEach-Object { "send $(B64 $_) 0 0 $pause" })
    $null = Inject ("agent-bridge-keys 2`n" + ($lines -join "`n") + "`n")
    Start-Sleep -Milliseconds 800
    $rows = Screen
    # Only the input box: the marker rows just above the last rule (a long box scrolls its top rule off the window),
    # each without its two-column indent, rejoined because a marker wraps at the window's edge.
    $last = @(0..($rows.Count - 1) | Where-Object { $rows[$_] -match '^[─]{10,}' }) | Select-Object -Last 1
    $joined = ''; $placeholder = $false
    for ($r = $last - 1; $null -ne $last -and $r -ge [Math]::Max(0, $last - 30); $r--) {
        if ($rows[$r] -match 'Pasted text #') { $placeholder = $true; continue }
        if ($rows[$r] -notmatch '^(>|\s)\s?[\[\]0-9-]+$' -and $rows[$r] -notmatch '^>\s*$') { break }
        $joined = $rows[$r].Substring(2) + $joined
    }
    $markers = [regex]::Matches($joined, '\[(\d{4})\]') | ForEach-Object { [int]$_.Groups[1].Value }
    '{0,-14} placeholder={1,-5} first={2,4} last={3,4} markers={4}' -f $label, $placeholder, ($markers | Select-Object -First 1), ($markers | Select-Object -Last 1), @($markers).Count
    Clear-Box
}
try {
    foreach ($size in $Sizes) {
        $text = (-join (1..([Math]::Ceiling($size / 7)) | ForEach-Object { '[{0:0000}]-' -f $_ })).Substring(0, $size)
        "-- $size characters, last marker [{0:0000}]" -f ([Math]::Ceiling($size / 7))
        Probe 'one burst' @($text) 0
        Probe "chunks $Chunk/$PauseMs" @(for ($i = 0; $i -lt $text.Length; $i += $Chunk) { $text.Substring($i, [Math]::Min($Chunk, $text.Length - $i)) }) $PauseMs
    }
}
finally { Clear-Box; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
