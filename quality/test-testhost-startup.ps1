#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$workflow = Get-Content -Raw (Join-Path $PSScriptRoot '..\.github\workflows\ci.yml')
if ($workflow -notmatch 'quality/testhost-startup.ps1') { throw 'Ubuntu must run the bounded testhost-startup wrapper under coverage.' }
. (Join-Path $PSScriptRoot 'testhost-startup.ps1')
function dotnet {
    $script:calls += ,@($args)
    $global:LASTEXITCODE = if ($script:case -eq 'success' -or ($script:calls.Count -gt 1 -and $script:case -ne 'twice')) { 0 } else { 1 }
    $folder = $args[([array]::IndexOf($args, '--results-directory') + 1)]
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $failed = if ($script:case -eq 'failed') { 1 } else { 0 }
    $executed = if ($script:case -eq 'late') { 1 } else { 0 }
    $project = Join-Path $folder 'Example.Tests'
    if ($script:calls.Count -eq 1) { $script:initialProject = $project }
    $message = if ($script:case -eq 'unknown') { 'Unknown runner error' } else { "Testhost process for source(s) '$(Join-Path $project 'bin\Release\net10.0\Example.Tests.dll')' exited with error: Process path: dotnet." }
    if ($script:case -ne 'missing') { "<TestRun><ResultSummary outcome='Failed'><Counters failed='$failed' executed='$executed'/><RunInfos><RunInfo><Text>$message</Text></RunInfo></RunInfos></ResultSummary></TestRun>" | Set-Content (Join-Path $folder 'run.trx') }
    if ($script:case -in 'mixed', 'passed') {
        $otherFailed = [int]($script:case -eq 'mixed')
        "<TestRun><ResultSummary outcome='Completed'><Counters failed='$otherFailed' executed='10'/></ResultSummary></TestRun>" | Set-Content (Join-Path $folder 'other.trx')
    }
    if ($script:case -eq 'build') { 'error MSB1234: build failed' }
    'Test Run Aborted.'
}
foreach ($script:case in 'success', 'crash', 'twice', 'failed', 'late', 'unknown', 'missing', 'build', 'mixed', 'passed') {
    $dir = Join-Path ([IO.Path]::GetTempPath()) "testhost-startup-$([guid]::NewGuid())"
    $script:calls = @()
    try {
        $code = Invoke-TesthostStartup -Target selected.slnf -Filter 'Category!=Process' -Results $dir 6> "$dir.log"
        $retry = $script:case -in 'crash', 'twice', 'passed'
        $expected = if ($script:case -in 'success', 'crash', 'passed') { 0 } else { 1 }
        if ($code -ne $expected -or $script:calls.Count -ne (1 + [int]$retry)) { throw "$script:case must preserve failure and retry only a zero-test startup crash, once." }
        foreach ($call in $script:calls) {
            if ('-m:2' -notin $call -or 'Category!=Process' -notin $call -or '--blame-crash' -notin $call) { throw 'Concurrency, filters and crash diagnostics must survive every attempt.' }
            if (($call -join ' ') -notmatch '--blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none') { throw 'The hang watchdog must stay unchanged.' }
        }
        if ($retry -and ($script:calls[1][1] -ne $script:initialProject -or (Get-Content "$dir.log" -Raw) -notmatch '::warning::.*1/1')) { throw 'Retry only the aborted assembly, and log it loudly.' }
        "PASS: $script:case"
    } finally { if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse }; Remove-Item -LiteralPath "$dir.log" -ErrorAction SilentlyContinue }
}
