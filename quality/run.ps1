#Requires -Version 7.4
<#
.SYNOPSIS
  Ring 0, the inner loop: run the suites a change reaches, on this machine (quality/README.md §2, §4).

.DESCRIPTION
  Selects suites from quality/catalog.yml with tools/AgentBridge.Quality, from the diff against the
  merge base with -Base plus uncommitted and untracked files, then runs each selected suite's
  command if it runs on this OS and on this kind of runner: local suites always, hosted ones from
  ring 1. Suites whose command is prose ("see ci.yml ..."), planned, or needs a tool this machine
  lacks are skipped with a note, never failed. Prints pass, fail and time per suite, and exits 1
  if any failed.

.EXAMPLE
  pwsh quality/run.ps1                      # what this change reaches, on this OS
  pwsh quality/run.ps1 -Ring 1              # what the PR gate would run here
  pwsh quality/run.ps1 -Explain             # the selection and why, without running
  pwsh quality/run.ps1 -Suite pwa-unit,host # these suites, whatever changed
  pwsh quality/run.ps1 -Explain -Base 3ca1646~1 -Head 3ca1646   # what a past commit reached
#>
[CmdletBinding()]
param(
    [ValidateRange(0, 4)]
    [int] $Ring = 0,
    [string[]] $Suite,
    [switch] $Explain,
    [string] $Base = 'origin/main',
    # A commit to select for instead of the working tree; uncommitted files are then left out.
    [string] $Head,
    # An explicit list of changed files instead of git's.
    [string[]] $Files,
    # Print the selection as JSON (the form CI reads) without running.
    [switch] $Json
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools/AgentBridge.Quality/AgentBridge.Quality.csproj'
$tool = Join-Path $root 'tools/AgentBridge.Quality/bin/Release/net10.0/agent-bridge-quality.dll'

$build = dotnet build $project -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $build | Out-Host
    throw "Building the selector (tools/AgentBridge.Quality) failed."
}

$selectArgs = @('select', '--repo', $root, '--ring', $Ring, '--platform', 'this', '--base', $Base)
if ($Head) { $selectArgs += @('--head', $Head) }
if ($Files) { $selectArgs += @('--files', ($Files -join ',')) }
if ($Suite) { $selectArgs += @('--suite', ($Suite -join ',')) }

if ($Explain) {
    dotnet $tool @selectArgs --explain
    exit $LASTEXITCODE
}

$selectionJson = (dotnet $tool @selectArgs) -join "`n"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Json) {
    $selectionJson
    exit 0
}
$selection = $selectionJson | ConvertFrom-Json

# Ring 0 is in-process tests only (catalog.yml's header, #375): Category=Process starts real
# processes, Category=Network binds sockets, ports or pipes, and Category=HomeAssistantIntegration
# needs a disposable HA. Untagged tests are in-process.
$quickTerms = @('Category!=Process', 'Category!=Network', 'Category!=HomeAssistantIntegration')

function Get-Skip($s) {
    $command = "$($s.command)".Trim()
    if ($s.status -eq 'planned') { return 'planned, not built yet' }
    if ($s.runner -ne 'local' -and -not ($s.runner -eq 'hosted' -and $Ring -ge 1)) { return "runs on a $($s.runner) runner" }
    if (-not $command) { return 'no command in the catalog' }
    if ($command -match '^see\s') { return "the command is prose: $command" }
    # The first program the command runs, after any leading "cd <dir> &&".
    $program = ($command -replace '^cd\s+\S+\s*&&\s*', '' -split '\s+')[0]
    if (-not (Get-Command $program -ErrorAction SilentlyContinue)) { return "needs $program, which isn't on PATH" }
    return $null
}

function Get-RingCommand([string] $command) {
    if ($Ring -ne 0 -or $command -notmatch '^\s*dotnet\s+test\b') { return $command }
    if ($command -notmatch '--filter\s+"([^"]*)"') {
        return "$command --filter `"$($quickTerms -join '&')`""
    }
    $existing = $Matches[1]
    # A plain AND of terms merges, so homeassistant's own HomeAssistantIntegration term isn't
    # repeated; anything with | or parentheses is kept whole and ANDed.
    $merged = if ($existing -match '[|()]') { @("($existing)") + $quickTerms }
              else { @($existing -split '&' | ForEach-Object Trim) + $quickTerms | Select-Object -Unique }
    return $command -replace '--filter\s+"[^"]*"', "--filter `"$($merged -join '&')`""
}

$platform = $selection.platform
Write-Host "Ring $Ring on $platform, mode $($selection.mode): $($selection.suites.Count) suite(s) selected from $($selection.files.Count) changed file(s)."
if ($selection.unclaimed.Count -gt 0) {
    Write-Host "No rule claims $($selection.unclaimed -join ', '), so the whole ring runs." -ForegroundColor Yellow
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($s in $selection.skipped) {
    $results.Add([pscustomobject]@{ Suite = $s.id; Result = 'skipped'; Seconds = $null; Note = $s.why })
}
foreach ($s in $selection.suites) {
    $skip = Get-Skip $s
    if ($skip) {
        $results.Add([pscustomobject]@{ Suite = $s.id; Result = 'skipped'; Seconds = $null; Note = $skip })
        continue
    }
    $command = Get-RingCommand $s.command
    Write-Host ""
    Write-Host "== $($s.id): $command" -ForegroundColor Cyan
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location $root
    try {
        # A child shell, so a "cd" in the command stays there, and so a failing program in
        # "a; b" fails the suite rather than being masked by b's success. That preference needs
        # pwsh 7.4, hence the #Requires.
        pwsh -NoProfile -NonInteractive -Command "`$ErrorActionPreference = 'Stop'; `$PSNativeCommandUseErrorActionPreference = `$true; $command" | Out-Host
        $exit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $watch.Stop()
    $results.Add([pscustomobject]@{
        Suite   = $s.id
        Result  = if ($exit -eq 0) { 'pass' } else { 'FAIL' }
        Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        Note    = if ($exit -eq 0) { '' } else { "exit $exit" }
    })
}

Write-Host ""
$results | Format-Table -AutoSize Suite, Result, Seconds, Note | Out-Host
$failed = @($results | Where-Object Result -eq 'FAIL')
$ran = @($results | Where-Object Result -ne 'skipped')
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) of $($ran.Count) suite(s) failed: $($failed.Suite -join ', ')." -ForegroundColor Red
    exit 1
}
Write-Host "$($ran.Count) suite(s) passed, $($results.Count - $ran.Count) skipped." -ForegroundColor Green
exit 0
