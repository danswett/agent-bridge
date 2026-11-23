# Asserts build/app-self-test.ps1 is valid PowerShell and that -Shard does what it says (#1755): passes --self-test-shard to
# the app, accepts a result of that shard and refuses another's. The app is a fake .cmd writing a result, so the run is Windows
# only; the parse check runs anywhere.
#   pwsh build/test-app-self-test-shard.ps1
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script = Join-Path $PSScriptRoot 'app-self-test.ps1'
$failures = @()
function Expect([string]$What, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { Write-Output "ok: $What" } else { $script:failures += "$What $Detail"; Write-Output "FAILED: $What $Detail" }
}

$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
Expect 'app-self-test.ps1 parses' (@($errors).Count -eq 0) (@($errors | ForEach-Object { $_.Message }) -join '; ')

if ($IsWindows) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('self-test-shard-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $dir | Out-Null
    try {
        # %~2 is the result path and %~4 the shard the app was asked for; $Writes names the shard it claims to have run.
        function New-FakeApp([string]$Name, [string]$Writes) {
            $path = Join-Path $dir "$Name.cmd"
            $shard = if ($Writes) { $Writes } else { '%~4' }
            @('@echo off', "echo {`"shard`":`"$shard`",`"stages`":[`"layout`"],`"stagesAll`":[`"layout`"],`"passed`":[`"a`"],`"failed`":[],`"unfinished`":null}> `"%~2`"", 'exit /b 0') |
                Set-Content $path -Encoding ascii
            $path
        }
        function Run-Self-Test([string]$App, [string[]]$Extra = @()) {
            $result = Join-Path $dir ('r' + [guid]::NewGuid().ToString('N') + '\result.json')
            & pwsh -NoProfile -File $script -Exe $App -Result $result -TimeoutSeconds 60 @Extra *> $null
            $LASTEXITCODE
        }
        $honest = New-FakeApp 'honest' ''
        $other = New-FakeApp 'other' '1/3'
        $code = Run-Self-Test $honest @('-Shard', '2/3')
        Expect 'a shard of the right number passes' ($code -eq 0) "(exit $code)"
        $code = Run-Self-Test $other @('-Shard', '2/3')
        Expect 'a result of another shard is refused' ($code -ne 0) "(exit $code)"
        $code = Run-Self-Test $honest
        Expect 'without -Shard the whole run passes as before' ($code -eq 0) "(exit $code)"
        $code = Run-Self-Test $honest @('-Shard', '4/3')
        Expect 'a bad -Shard is refused' ($code -ne 0) "(exit $code)"
    }
    finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
}
else { Write-Output 'skipped: the run cases need Windows (a .cmd stands in for the app)' }

if ($failures) { throw "ASSERTION FAILED: $($failures -join '; ')" }
Write-Output 'app-self-test shard wrapper: all cases passed'
