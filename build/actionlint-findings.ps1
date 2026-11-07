# Judges actionlint's findings for workflow-lint.yml's actionlint step: every finding fails it but
# one, the exception workflow-lint.yml sets next to its actionlint pin. That is actionlint 1.7.12's
# error for concurrency's queue key, which GitHub supports and actionlint doesn't yet (#951), and
# only where ci.yml's publish job sets queue: max. The same error anywhere else, another error
# there, or an exit code or output this can't read fails the step; so does the exception once
# actionlint no longer reports it, so that it is removed.
#
#   actionlint -format '{{json .}}' > findings.json
#   pwsh build/actionlint-findings.ps1 -Findings findings.json -ExitCode <actionlint's> -Allowed <message>
#
# Run where actionlint ran: its file paths are relative to there. Checked by
# build/test-actionlint-findings.ps1.
#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$Findings,
    [Parameter(Mandatory)][int]$ExitCode,
    # Empty for no exception.
    [Parameter(Mandatory)][AllowEmptyString()][string]$Allowed
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ci = '.github/workflows/ci.yml'

# As workflow commands require, so a message quoting a workflow can't end the annotation.
function Format-Property([string]$value) { $value.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A').Replace(':', '%3A').Replace(',', '%2C') }
function Format-Message([string]$value) { $value.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }

function Stop-Unread([string]$why) {
    Write-Output "::error title=actionlint::$(Format-Message $why)"
    exit 1
}

# The lines under the one line that is exactly "<indent><key>:" in [$from, $to), up to the next
# line, not blank or a comment, indented no deeper than it; $null without exactly one such line.
function Get-Block([string[]]$lines, [int]$from, [int]$to, [string]$key, [int]$indent) {
    $header = (' ' * $indent) + "${key}:"
    $at = @(for ($i = $from; $i -lt $to; $i++) { if ($lines[$i].TrimEnd() -ceq $header) { $i } })
    if ($at.Count -ne 1) { return $null }
    $end = $to
    for ($i = $at[0] + 1; $i -lt $to; $i++) {
        if ($lines[$i] -match "^ {0,$indent}[^\s#]") { $end = $i; break }
    }
    @{ From = $at[0] + 1; To = $end }
}

# Where ci.yml's publish job sets queue: max, as actionlint places the key: its line and column.
# $null, and so no exception, unless ci.yml has exactly that line in exactly that place.
function Get-PublishQueue {
    if (-not (Test-Path -LiteralPath $ci -PathType Leaf)) { return $null }
    $lines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $ci))
    $jobs = Get-Block $lines 0 $lines.Count 'jobs' 0
    if (-not $jobs) { return $null }
    $publish = Get-Block $lines $jobs.From $jobs.To 'publish' 2
    if (-not $publish) { return $null }
    $concurrency = Get-Block $lines $publish.From $publish.To 'concurrency' 4
    if (-not $concurrency) { return $null }
    $queue = @(for ($i = $concurrency.From; $i -lt $concurrency.To; $i++) { if ($lines[$i] -match '^\s*queue\s*:') { $i } })
    if ($queue.Count -ne 1 -or $lines[$queue[0]].TrimEnd() -cne '      queue: max') { return $null }
    @{ Line = $queue[0] + 1; Column = 7 }
}

if ($ExitCode -notin 0, 1) { Stop-Unread "actionlint couldn't lint the workflows (exit $ExitCode)" }
if (-not (Test-Path -LiteralPath $Findings -PathType Leaf)) { Stop-Unread "actionlint wrote no findings to $Findings" }
try { $items = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Resolve-Path -LiteralPath $Findings))) -NoEnumerate }
catch { Stop-Unread "actionlint's findings aren't JSON: $($_.Exception.Message)" }
if ($items -isnot [array]) { Stop-Unread "actionlint's findings aren't a JSON array" }
# actionlint exits 1 exactly when it found something.
if (($ExitCode -eq 0) -ne ($items.Count -eq 0)) { Stop-Unread "actionlint exited $ExitCode with $($items.Count) findings" }

$queue = Get-PublishQueue
$let = $false
$failed = 0
foreach ($item in $items) {
    if ($item -isnot [Management.Automation.PSCustomObject]) { Stop-Unread "an actionlint finding isn't a JSON object" }
    $finding = @{}
    foreach ($name in 'message', 'filepath', 'kind', 'line', 'column') {
        $property = $item.PSObject.Properties[$name]
        $value = if ($property) { $property.Value }
        $ok = if ($name -in 'line', 'column') { $value -is [int] -or $value -is [long] } else { $value -is [string] }
        if (-not $ok) { Stop-Unread "an actionlint finding has no $name" }
        $finding[$name] = $value
    }
    $file = "file=$(Format-Property $finding.filepath),line=$($finding.line),col=$($finding.column)"
    if (-not $let -and $queue -and $Allowed -ne '' -and $finding.message -ceq $Allowed -and $finding.kind -ceq 'syntax-check' `
            -and $finding.filepath -ceq $ci -and $finding.line -eq $queue.Line -and $finding.column -eq $queue.Column) {
        $let = $true
        Write-Output "::notice $file,title=actionlint exception (#951)::$(Format-Message $finding.message)"
        continue
    }
    $failed++
    Write-Output "::error $file,title=actionlint $(Format-Property $finding.kind)::$(Format-Message $finding.message)"
}
if ($queue -and $Allowed -ne '' -and -not $let) {
    $failed++
    Write-Output "::error title=actionlint::$(Format-Message "ci.yml's publish sets queue: max, but actionlint didn't report the exception workflow-lint.yml lets through; if actionlint now knows queue, remove the exception (#951)")"
}
if ($failed) { exit 1 }
exit 0
