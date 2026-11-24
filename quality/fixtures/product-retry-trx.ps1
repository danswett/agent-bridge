# VSTest TRX layout: https://github.com/microsoft/vstest/blob/main/docs/report.md
# Synthesized, not copied test output. Counts and adapter diagnostics reproduce
# the Protocol TRX (1339 passed, 2 skipped) and earlier Host TRX (3450 passed)
# retained by hosted run 44395492752. Both have one genuine failed test.
function Write-AdapterTrx([string]$Directory, [string]$FailedName, [int]$Passed = 1339, [int]$Skipped = 2) {
    New-Item -ItemType Directory -Force $Directory | Out-Null
    $results = [Text.StringBuilder]::new()
    $definitions = [Text.StringBuilder]::new()
    $entries = [Text.StringBuilder]::new()
    $list = '8c84fa94-04c1-424b-9868-57a2d4851a1d'
    $total = 1 + $Passed + $Skipped
    $executed = 1 + $Passed
    for ($i = 0; $i -lt $total; $i++) {
        $id = [guid]::NewGuid().ToString()
        $execution = [guid]::NewGuid().ToString()
        $name = if ($i -eq 0) { $FailedName } else { "Example.Tests.Case$i" }
        $name = [Security.SecurityElement]::Escape($name)
        $method = if ($i -eq 0) { 'Theory' } else { "Case$i" }
        $outcome = if ($i -eq 0) { 'Failed' } elseif ($i -ge $executed) { 'NotExecuted' } else { 'Passed' }
        $output = if ($i -eq 0) { '<Output><ErrorInfo><Message>Assertion failed.</Message></ErrorInfo></Output>' } else { '' }
        [void]$results.Append(@"
<UnitTestResult executionId="$execution" testId="$id" testName="$name" computerName="runner"
 duration="00:00:00.001" startTime="2026-11-18T21:25:00Z" endTime="2026-11-18T21:25:01Z"
 testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" outcome="$outcome" testListId="$list"
 relativeResultsDirectory="$execution">$output</UnitTestResult>
"@)
        [void]$definitions.Append(@"
<UnitTest name="$name" storage="Example.Tests.dll" id="$id"><Execution id="$execution" />
<TestMethod codeBase="Example.Tests.dll" adapterTypeName="executor://xunit/VsTestRunner3/netcore/"
 className="Example.Tests" name="$method" /></UnitTest>
"@)
        [void]$entries.Append("<TestEntry testId='$id' executionId='$execution' testListId='$list' />")
    }
    $failedText = [Security.SecurityElement]::Escape("[xUnit.net 00:03:16.68]     $FailedName [FAIL]")
    @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="$([guid]::NewGuid())" name="synthetic adapter run" runUser="runner"
 xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Times creation="2026-11-18T21:24:48Z" queuing="2026-11-18T21:24:48Z"
 start="2026-11-18T21:24:48Z" finish="2026-11-18T21:28:05Z" />
<Results>$results</Results><TestDefinitions>$definitions</TestDefinitions>
<TestEntries>$entries</TestEntries><TestLists><TestList name="Results Not in a List" id="$list" /></TestLists>
<ResultSummary outcome="Failed">
<Counters total="$total" executed="$executed" passed="$Passed" failed="1" error="0" timeout="0" aborted="0"
 inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0"
 warning="0" completed="0" inProgress="0" pending="0" />
<RunInfos><RunInfo computerName="runner" outcome="Error" timestamp="2026-11-18T21:28:00Z"><Text>$failedText</Text></RunInfo>
<RunInfo computerName="runner" outcome="Warning" timestamp="2026-11-18T21:28:04Z">
<Text>Data collector 'Blame' message: All tests finished running, Sequence file will not be generated.</Text>
</RunInfo></RunInfos></ResultSummary></TestRun>
"@ | Set-Content -LiteralPath (Join-Path $Directory 'runner_net10.0[2].trx') -Encoding utf8
}
