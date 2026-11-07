# Offline checks for the nonexecuting runner interface. CI only on execution-restricted hosts.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runner = Join-Path $PSScriptRoot 'run.ps1'
$arguments = @{
    Subscription = '11111111-2222-3333-4444-555555555555'
    Role = 'win-x64'
    Build = 'v0.1.600@sha256:' + ('a' * 64)
    Scenario = 'nightly-v1'
    Relay = 'staging'
    RunId = 'abc123'
}
if (Test-Path Env:LAB_GITHUB_TOKEN) { throw 'Tests require LAB_GITHUB_TOKEN to be absent; no existing credential is changed' }

$global:LabRunnerCalls = [Collections.Generic.List[string]]::new()
function global:az { $global:LabRunnerCalls.Add('az'); throw 'An external command was attempted' }
function global:gh { $global:LabRunnerCalls.Add('gh'); throw 'An external command was attempted' }
function global:hyperloop { $global:LabRunnerCalls.Add('hyperloop'); throw 'An external command was attempted' }

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    $global:LabRunnerCalls.Clear()
    try {
        & $body
        Assert ($global:LabRunnerCalls.Count -eq 0) "External commands ran: $($global:LabRunnerCalls -join ', ')"
        Write-Output "ok   $name"
    } catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Test-Refused([hashtable]$request) {
    try { & $runner @request | Out-Null } catch { return $_.Exception.Message }
    return $null
}

$temp = Join-Path ([IO.Path]::GetTempPath()) "lab-runner-test-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    Check 'Both roles return unverified plans, never run results or coverage' {
        foreach ($role in 'win-x64', 'win-arm64') {
            $request = $arguments.Clone()
            $request.Role = $role
            $plan = & $runner @request
            Assert ($plan.draft -and $plan.mode -ceq 'plan' -and -not $plan.executionAllowed) 'Planning claimed execution'
            Assert (-not $plan.requestedBuild.provenanceVerified) 'A digest was mistaken for provenance'
            Assert ($plan.role -ceq $role -and $plan.provision.role -ceq $role) 'The role changed'
            Assert ($plan.provision.mode -ceq 'plan') 'The provisioner was not in planning mode'
            Assert ($plan.provision.resources[0] -clike "*-$($arguments.RunId)") 'Resource planning lost the run id'
            foreach ($field in 'outcome', 'steps', 'artifacts', 'schemaVersion') {
                Assert ($null -eq $plan.PSObject.Properties[$field]) "Planning emitted a result field: $field"
            }
        }
    }

    Check 'Default run ids are fresh and both roles use the sole provisioner' {
        $request = $arguments.Clone()
        $request.Remove('RunId')
        $first = & $runner @request
        $second = & $runner @request
        Assert ($first.runId -cmatch '^[a-z0-9]{6,12}$' -and $first.runId -cne $second.runId) 'A default run id was reused'
        foreach ($plan in $first, $second) {
            Assert ($plan.provision.runId -ceq $plan.runId) 'The provisioner got another run id'
            Assert ($plan.provision.subscription -ceq $arguments.Subscription) 'The explicit subscription changed'
        }
    }

    Check 'URLs, mutable builds, unknown coverage and malformed resource identities are refused' {
        $bad = @{
            Build = @('v0.1.600', 'latest', 'https://example.invalid/build.exe', '..\build.exe', ('v0.1.600@sha256:' + ('A' * 64)))
            Scenario = @('nightly-v2', 'NIGHTLY-V1', 'real-provider')
            Relay = @('production', 'STAGING', 'https://relay-staging.slipgates.com', 'https://example.invalid')
            Subscription = @('default', '00000000-0000-0000-0000-000000000000')
            Role = @('WIN-X64', 'linux-arm64')
            RunId = @('ABC123', 'abc-123', '../abc123')
        }
        foreach ($field in $bad.Keys) {
            foreach ($value in $bad[$field]) {
                $request = $arguments.Clone()
                $request[$field] = $value
                Assert ($null -ne (Test-Refused $request)) "$field accepted an invalid request"
            }
        }
        $request = $arguments.Clone()
        $request.Relay = 'ppe'
        $plan = & $runner @request
        Assert ($plan.relay -ceq 'ppe' -and @($plan.unmet | Where-Object { $_ -clike 'ppe (*' }).Count -eq 1) 'PPE was substituted or treated as available'
    }

    Check 'Apply, confirmation suppression and WhatIf never execute or produce evidence' {
        $out = Join-Path $temp 'not-created'
        foreach ($role in 'win-x64', 'win-arm64') {
            foreach ($whatIf in $false, $true) {
                $request = $arguments.Clone()
                $request.Role = $role
                $request.Out = $out
                $request.Apply = $true
                $request.Confirm = $false
                $request.WhatIf = $whatIf
                $message = Test-Refused $request
                Assert ($null -ne $message -and $message -clike 'Run refused before any resource, Azure or sign-in call:*') 'Apply did not clearly refuse'
                foreach ($id in 'owner-authorization', 'run-registration', 'expiry-sweeper', 'cost-alerts', 'build-provenance', 'scenario-execution', 'transport-evidence', 'owner-device-flow') {
                    Assert ($message -clike "*- $id (*") "The refusal omitted $id"
                }
                Assert (-not (Test-Path -LiteralPath $out)) 'A refused run created an output directory'
            }
        }
    }

    Check 'Planning and refusal leave an existing result untouched' {
        $existing = Join-Path $temp 'existing'
        New-Item -ItemType Directory -Path $existing | Out-Null
        $result = Join-Path $existing 'result.json'
        Set-Content -LiteralPath $result -Value 'not runner evidence' -NoNewline
        $request = $arguments.Clone()
        $request.Out = $existing
        & $runner @request | Out-Null
        $request.Apply = $true
        Assert ($null -ne (Test-Refused $request)) 'Apply did not refuse'
        Assert ((Get-Content -LiteralPath $result -Raw) -ceq 'not runner evidence') 'Existing evidence was changed'
        Assert (@(Get-ChildItem -LiteralPath $existing).Count -eq 1) 'Additional evidence was written'
    }

    Check 'A legacy lab-token environment is refused without printing its value' {
        $env:LAB_GITHUB_TOKEN = 'test-placeholder-not-a-credential'
        try {
            $message = Test-Refused $arguments
            Assert ($null -ne $message -and $message -clike 'LAB_GITHUB_TOKEN is refused;*') 'The legacy credential route was accepted'
            Assert (-not $message.Contains($env:LAB_GITHUB_TOKEN)) 'The refusal echoed the environment value'
        } finally { Remove-Item Env:LAB_GITHUB_TOKEN }
    }
} finally {
    Remove-Item -LiteralPath $temp -Recurse
    Remove-Item Function:\az
    Remove-Item Function:\gh
    Remove-Item Function:\hyperloop
}

if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
