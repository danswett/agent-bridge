<#
    Whether agent-ha-bridge's per-machine Home Assistant contract still matches the fixtures the
    connector is tested against.

    Records the contract from an agent-ha-bridge checkout with Export-HaContract.ps1 into
    -OutDir (never over the committed fixtures), then compares each fixture with the committed
    one, ignoring only the source commit it was recorded from. Writes into -OutDir:

        upstream.txt   the checkout's commit
        summary.md     what moved, for a person
        contract.diff  a unified diff, pinned -> upstream (when something moved)
        export.log     the exporter's output

    Exit code: 0 the contract is unchanged, 1 it changed, 2 the exporter could not record it
    (a function it ports was renamed or removed, say - which is drift too).

    Read-only against the checkout: the exporter loads functions from its syntax tree and asks
    git for its commit and status, nothing more. Used by .github/workflows/ha-contract-drift.yml;
    runs the same locally:

        pwsh -NoProfile -File tools/ha-contract/Test-HaContractDrift.ps1 `
            -Source C:\Users\me\repos\agent-ha-bridge -OutDir .\drift
#>
#Requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Fixtures = (Join-Path $PSScriptRoot '..\..\tests\AgentBridge.HomeAssistant.Tests\Fixtures')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$names = [ordered]@{
    Output          = 'ha-contract.json'
    DecisionsOutput = 'ha-decisions.json'
    RepliesOutput   = 'ha-replies.json'
    StopsOutput     = 'ha-stops.json'
    LaunchOutput    = 'ha-launch.json'
    NotifyOutput    = 'ha-notify.json'
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
$Fixtures = (Resolve-Path -LiteralPath $Fixtures).Path
$Source = (Resolve-Path -LiteralPath $Source).Path
foreach ($dir in 'recorded', 'pinned', 'upstream') {
    $path = Join-Path $OutDir $dir
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

$upstream = (& git -C $Source rev-parse HEAD).Trim()
Set-Content -LiteralPath (Join-Path $OutDir 'upstream.txt') -Value $upstream -NoNewline

$exporter = Join-Path $PSScriptRoot 'Export-HaContract.ps1'
$arguments = @('-NoProfile', '-File', $exporter, '-Source', $Source)
foreach ($key in $names.Keys) { $arguments += @("-$key", (Join-Path $OutDir "recorded\$($names[$key])")) }
$log = & pwsh @arguments 2>&1 | ForEach-Object { "$_" }
$exported = $LASTEXITCODE
Set-Content -LiteralPath (Join-Path $OutDir 'export.log') -Value ($log -join "`n")

# The commit a fixture was recorded from: always different upstream, never the contract.
function Get-PinnedCommit([string]$Text) {
    if ($Text -match '"commit":\s*"([0-9a-f]{40})"') { return $Matches[1] }
    ''
}
function ConvertTo-Comparable([string]$Text) {
    $Text = $Text -replace "`r`n", "`n"
    $Text = $Text -replace '"commit":\s*"[0-9a-f]{40}"', '"commit": "<commit>"'
    $Text -replace '"dirty":\s*(true|false)', '"dirty": "<dirty>"'
}

$pinnedCommit = Get-PinnedCommit (Get-Content -LiteralPath (Join-Path $Fixtures 'ha-contract.json') -Raw)
$summary = [Collections.Generic.List[string]]::new()
$summary.Add("agent-ha-bridge ``$upstream`` against the fixtures pinned at ``$pinnedCommit``.")
$summary.Add('')

if ($exported -ne 0) {
    $tail = @($log | Select-Object -Last 40) -join "`n"
    $summary.Add('**The exporter could not record the contract** (exit ' + $exported + '): a function it ports was probably renamed, removed or changed shape. Its output ends:')
    $summary.Add('')
    $summary.Add('```')
    $summary.Add($tail)
    $summary.Add('```')
    Set-Content -LiteralPath (Join-Path $OutDir 'summary.md') -Value ($summary -join "`n")
    Write-Host ($summary -join "`n")
    exit 2
}

$changed = [Collections.Generic.List[string]]::new()
foreach ($name in $names.Values) {
    $pinned = ConvertTo-Comparable (Get-Content -LiteralPath (Join-Path $Fixtures $name) -Raw)
    $recorded = ConvertTo-Comparable (Get-Content -LiteralPath (Join-Path $OutDir "recorded\$name") -Raw)
    [IO.File]::WriteAllText((Join-Path $OutDir "pinned\$name"), $pinned, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $OutDir "upstream\$name"), $recorded, [Text.UTF8Encoding]::new($false))
    if ($pinned -cne $recorded) { $changed.Add($name) }
}

if ($changed.Count -eq 0) {
    $summary.Add('The contract is unchanged.')
    Set-Content -LiteralPath (Join-Path $OutDir 'summary.md') -Value ($summary -join "`n")
    Write-Host ($summary -join "`n")
    exit 0
}

# Relative paths, so the diff reads pinned/<fixture> -> upstream/<fixture> wherever it ran.
Push-Location $OutDir
try { $diff = & git -c core.autocrlf=false -c core.safecrlf=false --no-pager diff --no-index --no-color --stat=120 --patch -- pinned upstream 2>$null | ForEach-Object { "$_" } }
finally { Pop-Location }
Set-Content -LiteralPath (Join-Path $OutDir 'contract.diff') -Value ($diff -join "`n")

$summary.Add("**The contract changed** in $($changed.Count) fixture(s): " + (($changed | ForEach-Object { "``$_``" }) -join ', ') + '.')
$summary.Add('')
$summary.Add('Re-pin as docs/ha-connector.md ("Keeping up with agent-ha-bridge") describes: regenerate the fixtures from this commit, implement what moved, and land it as its own PR.')
Set-Content -LiteralPath (Join-Path $OutDir 'summary.md') -Value ($summary -join "`n")
Write-Host ($summary -join "`n")
Write-Host ($diff -join "`n")
exit 1
