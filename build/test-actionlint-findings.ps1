# Checks build/actionlint-findings.ps1, which lets exactly one actionlint finding through
# workflow-lint.yml's actionlint step: actionlint 1.7.12's error for concurrency's queue key, at
# ci.yml publish's queue: max (#951). Against made-up repositories and actionlint output under
# out/test-actionlint-findings: actionlint itself doesn't run. workflow-lint.yml runs this with
# the exception it sets, which must be exactly that error.
#
#   pwsh build/test-actionlint-findings.ps1
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$filter = Join-Path $PSScriptRoot 'actionlint-findings.ps1'
$scratch = Join-Path $PSScriptRoot '..' 'out' 'test-actionlint-findings'
# actionlint 1.7.12's message for the key (its parse.go, parseConcurrency).
$queueError = 'unexpected key "queue" for "concurrency" section. expected one of "cancel-in-progress", "group"'
# In workflow-lint.yml, the exception it sets; run by hand, the one it should be.
$allowed = if ($env:GITHUB_ACTIONS) { "$env:ACTIONLINT_QUEUE_EXCEPTION" } else { $queueError }
$failures = 0

# A ci.yml with queue on publish (line 12, column 7) and, to try the same error elsewhere, on the
# root (line 4) and another job (line 9).
$ciLines = @(
    'name: CI'                              # 1
    'concurrency:'                          # 2
    '  group: ci'                           # 3
    '  queue: max'                          # 4
    'jobs:'                                 # 5
    '  build:'                              # 6
    '    concurrency:'                      # 7
    '      group: build'                    # 8
    '      queue: max'                      # 9
    '  publish:'                            # 10
    '    concurrency:'                      # 11
    '      queue: max'                      # 12
    '      group: publish-prerelease'       # 13
    '      cancel-in-progress: false'       # 14
    '    runs-on: windows-latest'           # 15
)

function Finding([string]$message = $queueError, [string]$file = '.github/workflows/ci.yml', [int]$line = 12, [int]$column = 7, [string]$kind = 'syntax-check') {
    [ordered]@{ message = $message; filepath = $file; line = $line; column = $column; kind = $kind; snippet = ''; end_column = $column }
}

# Runs the filter in a fresh repository holding $ci as its ci.yml (none if $null), on $output as
# actionlint's (JSON of it unless already a string), and returns its exit code and annotations.
function Invoke-Filter($output, [int]$exitCode = 1, [AllowNull()][string[]]$ci = $ciLines, [string]$exception = $allowed) {
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $scratch '.github' 'workflows') | Out-Null
    if ($null -ne $ci) { Set-Content -LiteralPath (Join-Path $scratch '.github' 'workflows' 'ci.yml') -Value $ci }
    $file = Join-Path $scratch 'findings.json'
    if ($null -ne $output) {
        $text = if ($output -is [string]) { $output } else { ConvertTo-Json -InputObject @($output) -Depth 3 }
        Set-Content -LiteralPath $file -Value $text -NoNewline
    }
    Push-Location $scratch
    try { $annotations = & pwsh -NoProfile -File $filter -Findings $file -ExitCode $exitCode -Allowed $exception 2>&1 }
    finally { Pop-Location }
    @{ Code = $LASTEXITCODE; Annotations = @($annotations | ForEach-Object { "$_" }) }
}

function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "PASS $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}

function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }

function Assert-Passes($result) {
    Assert ($result.Code -eq 0) "exit $($result.Code): $($result.Annotations -join ' | ')"
    Assert (-not ($result.Annotations -match '^::error')) "errors: $($result.Annotations -join ' | ')"
}

function Assert-Fails($result, [string]$like) {
    Assert ($result.Code -eq 1) "exit $($result.Code): $($result.Annotations -join ' | ')"
    Assert ([bool]($result.Annotations -like "::error*$like*")) "no error like '$like': $($result.Annotations -join ' | ')"
}

Check 'The exception workflow-lint.yml sets is exactly actionlint 1.7.12''s queue error' {
    Assert ($allowed -ceq $queueError) "ACTIONLINT_QUEUE_EXCEPTION is '$allowed'"
}

Check 'The queue error at publish''s queue: max passes, as a notice' {
    $result = Invoke-Filter @(Finding)
    Assert-Passes $result
    Assert ([bool]($result.Annotations -like '::notice file=.github/workflows/ci.yml,line=12,col=7,title=actionlint exception (#951)::unexpected key "queue"*')) "annotations: $($result.Annotations -join ' | ')"
}

Check 'No findings with no queue in ci.yml passes' {
    Assert-Passes (Invoke-Filter '[]' -exitCode 0 -ci ($ciLines | Where-Object { $_ -notmatch 'queue' }))
}

Check 'With no exception, no findings passes and the queue error fails' {
    Assert-Passes (Invoke-Filter '[]' -exitCode 0 -exception '')
    Assert-Fails (Invoke-Filter @(Finding) -exception '') 'title=actionlint syntax-check::unexpected key "queue"'
}

Check 'The same error on another job''s concurrency fails' {
    Assert-Fails (Invoke-Filter @((Finding), (Finding -line 9))) 'line=9,col=7,title=actionlint syntax-check::unexpected key "queue"'
}

Check 'The same error on the root concurrency fails' {
    Assert-Fails (Invoke-Filter @((Finding), (Finding -line 4 -column 3))) 'line=4,col=3,title=actionlint syntax-check::unexpected key "queue"'
}

Check 'The same error in another workflow fails' {
    Assert-Fails (Invoke-Filter @((Finding), (Finding -file '.github/workflows/relay.yml'))) 'file=.github/workflows/relay.yml,line=12,col=7'
}

Check 'Another error, or another kind, at publish''s queue fails' {
    Assert-Fails (Invoke-Filter @(Finding -message 'unexpected key "queue" for "concurrency" section')) 'line=12,col=7,title=actionlint syntax-check::'
    Assert-Fails (Invoke-Filter @(Finding -kind 'expression')) 'line=12,col=7,title=actionlint expression::'
}

Check 'The exception with any other finding fails' {
    Assert-Fails (Invoke-Filter @((Finding), (Finding -message 'label "x" is unknown' -line 15 -column 14 -kind 'runner-label'))) 'line=15,col=14,title=actionlint runner-label::label "x" is unknown'
}

Check 'The error twice at publish''s queue lets only one through' {
    Assert-Fails (Invoke-Filter @((Finding), (Finding))) 'line=12,col=7,title=actionlint syntax-check::'
}

Check 'Without exactly queue: max at publish''s concurrency, nothing is let through' {
    Assert-Fails (Invoke-Filter @(Finding) -ci $null) 'title=actionlint syntax-check::unexpected key "queue"'
    Assert-Fails (Invoke-Filter @(Finding) -ci ($ciLines -replace '^      queue: max$', '      queue:  max')) 'title=actionlint syntax-check::unexpected key "queue"'
    # The publish job's line moved under another job: line 12 is no longer publish's.
    Assert-Fails (Invoke-Filter @(Finding) -ci ($ciLines -replace '^  publish:$', '  publish-later:')) 'title=actionlint syntax-check::unexpected key "queue"'
}

Check 'Once actionlint no longer reports publish''s queue, the exception fails, to be removed' {
    Assert-Fails (Invoke-Filter '[]' -exitCode 0) 'remove the exception (#951)'
}

Check 'Output that can''t be read fails' {
    foreach ($output in 'not json', '{"message":"x"}', '', 'null', '["x"]', '[{"message":"x","filepath":"a","kind":"k","line":"1","column":1}]') {
        Assert-Fails (Invoke-Filter $output) 'title=actionlint::'
    }
    Assert-Fails (Invoke-Filter $null) 'wrote no findings'
}

Check 'actionlint failing to run, or an exit code its findings contradict, fails' {
    Assert-Fails (Invoke-Filter @(Finding) -exitCode 2) "couldn't lint the workflows (exit 2)"
    Assert-Fails (Invoke-Filter '[]' -exitCode 3) "couldn't lint the workflows (exit 3)"
    Assert-Fails (Invoke-Filter '[]' -exitCode 1) 'exited 1 with 0 findings'
    Assert-Fails (Invoke-Filter @(Finding) -exitCode 0) 'exited 0 with 1 findings'
}

if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
