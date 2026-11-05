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

  Where api.nuget.org is blocked, set AGENT_BRIDGE_NUGET_SOURCE to a mirror's index.json: every
  restore the run does then fetches packages and audit data from it (quality/README.md §4).

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

# Opt-in, for machines that can't reach api.nuget.org: restore packages and run NuGet's audit
# through a mirror (quality/nuget-mirror.ps1 says why both). Unset, nothing here changes.
$mirrorConfig = $null
if ($env:AGENT_BRIDGE_NUGET_SOURCE) {
    . (Join-Path $PSScriptRoot 'nuget-mirror.ps1')
    # Under obj/, which git ignores, so it can't be committed by mistake.
    $mirrorConfig = Join-Path $root 'tools/AgentBridge.Quality/obj/nuget.mirror.config'
    New-NuGetMirrorConfig -Source $env:AGENT_BRIDGE_NUGET_SOURCE -Path $mirrorConfig
    # Not the URL itself: a mirror's URL can carry a token, and this output gets pasted into PRs.
    if (-not $Json) { Write-Host "Restoring packages and auditing them through the mirror in AGENT_BRIDGE_NUGET_SOURCE." -ForegroundColor DarkGray }
}

$restore = @()
if ($mirrorConfig) { $restore += "-p:RestoreConfigFile=$mirrorConfig" }
$build = dotnet build $project -c Release --nologo -v q @restore
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

function Get-Skip($s) {
    $command = "$($s.command)".Trim()
    if ($s.status -eq 'planned') { return 'planned, not built yet' }
    if ($s.runner -ne 'local' -and -not ($s.runner -eq 'hosted' -and $Ring -ge 1)) { return "runs on a $($s.runner) runner" }
    if (-not $command) { return 'no command in the catalog' }
    if ($command -match '^see\s') { return "the command is prose: $command" }
    # Every program the command runs, so "dotnet build ... && npx playwright test" is skipped,
    # not failed, on a machine without Node. "cd" is a shell builtin, and "#" starts a comment.
    $programs = ($command -replace '\s#.*$', '') -split '&&|;' |
        ForEach-Object { ($_.Trim() -split '\s+')[0] } |
        Where-Object { $_ -and $_ -ne 'cd' }
    foreach ($program in $programs) {
        if (-not (Get-Command $program -ErrorAction SilentlyContinue)) { return "needs $program, which isn't on PATH" }
    }
    return $null
}

$platform = $selection.platform
Write-Host "Ring $Ring on $platform, mode $($selection.mode): $($selection.suites.Count) suite(s) selected from $($selection.files.Count) changed file(s)."
if ($selection.unclaimed.Count -gt 0) {
    Write-Host "No rule claims $($selection.unclaimed -join ', '), so the whole ring runs." -ForegroundColor Yellow
}
# Warnings only (#929): a changed test file that waits in real time or reads the real clock, before
# any suite runs, whichever are selected. It never changes the exit code, even when it can't run.
try {
    . (Join-Path $PSScriptRoot 'realtime-lint.ps1')
    Get-RealTimeTestWarning -Root $root -Changed @($selection.files | Where-Object { $_ }) |
        ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}
catch {
    Write-Host "The real-time lint (quality/realtime-lint.ps1) couldn't run: $($_.Exception.Message)" -ForegroundColor DarkYellow
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
    # The selector adds ring 0's test filter (in-process tests only); see RingCommand.cs.
    $command = $s.runCommand
    Write-Host ""
    Write-Host "== $($s.id): $command" -ForegroundColor Cyan
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location $root
    try {
        # A child shell, so a "cd" in the command stays there, and so a failing program in
        # "a; b" fails the suite rather than being masked by b's success. That preference needs
        # pwsh 7.4, hence the #Requires. The mirror's config reaches every restore in the suite
        # through MSBuild, which reads environment variables as properties; set only in the
        # child, so it never outlives this run in the caller's session.
        $prelude = if ($mirrorConfig) { "`$env:RestoreConfigFile = '$([System.Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($mirrorConfig))'; " } else { '' }
        pwsh -NoProfile -NonInteractive -Command "`$ErrorActionPreference = 'Stop'; `$PSNativeCommandUseErrorActionPreference = `$true; $prelude$command" | Out-Host
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
