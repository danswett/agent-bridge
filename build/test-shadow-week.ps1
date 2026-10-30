# Checks build/shadow-week.ps1 -Install against stand-ins for everything it changes: the
# scheduled tasks, dotnet publish, agent-bridge-host and the processes it would stop. Nothing real
# is registered, published, signed in or stopped; the folders are under a scratch directory.
#
#   pwsh build/test-shadow-week.ps1
#
# The case it exists for: an install that failed on a missing relay had already stopped both
# tasks, and the comparison stayed down until it was run again.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { Write-Output 'shadow-week.ps1 is Windows only; skipped'; return }

$target = Join-Path $PSScriptRoot 'shadow-week.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "shadow-week-test-$PID"
$env:LOCALAPPDATA = Join-Path $scratch 'LocalAppData'
Remove-Item Env:AGENT_BRIDGE_RELAY -ErrorAction SilentlyContinue
$bin = Join-Path $scratch 'bin'
$ahb = Join-Path $scratch 'agent-ha-bridge'
$profileFolder = Join-Path $env:LOCALAPPDATA 'AgentBridge\shadowtest'
$profileConfig = Join-Path $profileFolder 'config.json'
$pwaRelay = (Select-String -Path (Join-Path $PSScriptRoot '..\.github\workflows\pages.yml') -Pattern '^\s*S4_RELAY:\s*(\S+)\s*$').Matches[0].Groups[1].Value

# --- Stand-ins. Functions come before cmdlets and programs, so the script under test finds these.
# What they record is global: $script: in them would be the script under test's.
$global:SwTasks = @{}
$global:SwCalls = [Collections.Generic.List[string]]::new()
$global:SwFail = @{}
$global:SwSignedIn = $null

function Get-ScheduledTask {
    [CmdletBinding()] param([string]$TaskName)
    if ($global:SwTasks.ContainsKey($TaskName)) {
        $t = $global:SwTasks[$TaskName]
        [pscustomobject]@{ TaskName = $TaskName; State = $t.State; Actions = @([pscustomobject]@{ Arguments = $t.Xml }) }
    }
}
function Export-ScheduledTask {
    [CmdletBinding()] param([string]$TaskName)
    if ($global:SwFail['export']) { throw "stand-in: exporting $TaskName failed" }
    $global:SwTasks[$TaskName].Xml
}
function Get-ScheduledTaskInfo { [CmdletBinding()] param([string]$TaskName) [pscustomobject]@{ LastTaskResult = 0; LastRunTime = [datetime]::MinValue } }
function Register-ScheduledTask {
    [CmdletBinding()] param([string]$TaskName, [string]$Xml, [switch]$Force)
    $global:SwCalls.Add("register $TaskName")
    if ($global:SwFail['register'] -and $TaskName -like $global:SwFail['register'] -and $Xml -notlike 'PREVIOUS*') { throw "stand-in: registering $TaskName failed" }
    $global:SwTasks[$TaskName] = @{ Xml = $Xml; State = 'Ready' }
}
function Unregister-ScheduledTask { [CmdletBinding()] param([string]$TaskName, [switch]$Confirm) $global:SwCalls.Add("unregister $TaskName"); $global:SwTasks.Remove($TaskName) }
function Start-ScheduledTask {
    [CmdletBinding()] param([string]$TaskName)
    $global:SwCalls.Add("start $TaskName")
    $xml = $global:SwTasks[$TaskName].Xml
    # A new build that exits at once leaves its task Ready; one that serves writes its log.
    if ($global:SwFail['exit'] -and $xml -notlike 'PREVIOUS*' -and $TaskName -like $global:SwFail['exit']) { return }
    $global:SwTasks[$TaskName].State = 'Running'
    if ($TaskName -like 'AgentBridgeShadowHost_*' -and $xml -notlike 'PREVIOUS*') {
        New-Item -ItemType Directory -Force (Join-Path $profileFolder 'logs') | Out-Null
        Add-Content (Join-Path $profileFolder 'logs\host.log') 'serving'
    }
}
function Stop-ScheduledTask { [CmdletBinding()] param([string]$TaskName) $global:SwCalls.Add("stop $TaskName"); $global:SwTasks[$TaskName].State = 'Ready' }
function Get-Process { [CmdletBinding()] param([string[]]$Name) }
function Stop-Process { [CmdletBinding()] param([int]$Id, [switch]$Force) throw 'stand-in: no process may be stopped here' }
function Start-Sleep { [CmdletBinding()] param([int]$Seconds, [int]$Milliseconds) }

function dotnet {
    $global:SwCalls.Add("dotnet $($args[0])")
    if ($global:SwFail['publish']) { $global:LASTEXITCODE = 1; return }
    $out = $args[([array]::IndexOf($args, '-o')) + 1]
    New-Item -ItemType Directory -Force $out | Out-Null
    foreach ($f in 'agent-bridge-host.exe', 'agent-bridge-hostw.exe') { Set-Content (Join-Path $out $f) 'NEW' }
    $global:LASTEXITCODE = 0
}

function Invoke-ShadowHost {
    $line = $args -join ' '
    $global:SwCalls.Add("host $line")
    $global:LASTEXITCODE = 0
    switch -Wildcard ($line) {
        'workspace list*' { return }
        'config set*' {
            if ($global:SwFail['config'] -and $line -like $global:SwFail['config']) { $global:LASTEXITCODE = 4; return }
            New-Item -ItemType Directory -Force $profileFolder | Out-Null
            Add-Content $profileConfig $line
        }
        'whoami*' { if ($global:SwSignedIn) { "profile shadowtest, account acct-1, device dev-1" } else { 'profile shadowtest, account (not signed in)' } }
        'sign-in*' {
            $relay = $args[([array]::IndexOf($args, '--relay')) + 1]
            New-Item -ItemType Directory -Force $profileFolder | Out-Null
            Set-Content (Join-Path $profileFolder 'relay.txt') $relay
            $global:SwSignedIn = $relay
        }
    }
}

# --- Cases.
$failures = 0
function Assert([bool]$Condition, [string]$Claim) {
    if ($Condition) { Write-Output "  ok    $Claim" } else { Write-Output "  FAIL  $Claim"; $script:failures++ }
}

$hostTask = $null; $runTask = $null
function Reset-World([switch]$Previous) {
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ahb, $profileFolder | Out-Null
    Set-Content (Join-Path $ahb 'config.json') '{"homeAssistant":{"token":"not-read"},"newSession":{"workspaces":[],"resumeCount":3}}'
    $global:SwTasks = @{}
    $global:SwCalls.Clear()
    $global:SwFail = @{}
    $global:SwSignedIn = $null
    if ($Previous) {
        New-Item -ItemType Directory -Force $bin | Out-Null
        Set-Content (Join-Path $bin 'agent-bridge-host.exe') 'PREVIOUS'
        Set-Content $profileConfig 'PREVIOUS CONFIG'
        foreach ($t in $script:hostTask, $script:runTask) { $global:SwTasks[$t] = @{ Xml = "PREVIOUS $t"; State = 'Running' } }
    }
}
function Invoke-Install([hashtable]$Extra = @{}) {
    $p = @{ Install = $true; HostProfile = 'shadowtest'; Bin = $bin; AgentHaBridgeHome = $ahb } + $Extra
    try { $null = & $target @p; $null } catch { "$_" }
}
function Test-PreviousUntouched {
    Assert ((Get-Content (Join-Path $bin 'agent-bridge-host.exe')) -eq 'PREVIOUS') 'the previous build is still in place'
    Assert ((Get-Content -Raw $profileConfig).Trim() -eq 'PREVIOUS CONFIG') "the profile's config is as it was"
    Assert (-not (Test-Path "$bin.new") -and -not (Test-Path "$bin.previous")) 'nothing is left beside it'
    foreach ($t in $script:hostTask, $script:runTask) {
        Assert ($global:SwTasks[$t].Xml -eq "PREVIOUS $t" -and $global:SwTasks[$t].State -eq 'Running') "$t is the previous one, running"
    }
}

# The task names come from the script (they hash the user's SID): a first install shows them.
Reset-World
Write-Output 'A first install publishes, configures, registers and starts both tasks'
$err = Invoke-Install
Assert ($null -eq $err) "it succeeds ($err)"
$script:hostTask = @($global:SwTasks.Keys | Where-Object { $_ -like 'AgentBridgeShadowHost_shadowtest_*' })[0]
$script:runTask = @($global:SwTasks.Keys | Where-Object { $_ -like 'AgentBridgeShadowRun_shadowtest_*' })[0]
Assert ($script:hostTask -and $script:runTask) 'both tasks are registered'
Assert ($global:SwTasks.Values.State -notcontains 'Ready') 'both are running'
Assert ((Get-Content (Join-Path $bin 'agent-bridge-host.exe')) -eq 'NEW') 'the new build is in place'
Assert (-not (Test-Path "$bin.new") -and -not (Test-Path "$bin.previous")) 'nothing is left beside it'
Assert ((Get-Content -Raw $profileConfig) -match 'delivery.readOnly true') 'the profile is read-only'
Assert ($global:SwTasks[$script:hostTask].Xml -match '--local-only') 'without -Relay the host serves this machine only'

Reset-World -Previous
Write-Output 'Reinstalling over a running install replaces it'
$err = Invoke-Install
Assert ($null -eq $err) "it succeeds ($err)"
Assert ((Get-Content (Join-Path $bin 'agent-bridge-host.exe')) -eq 'NEW') 'the new build is in place'
Assert (-not (Test-Path "$bin.previous")) 'the previous build is dropped once the new one serves'
foreach ($t in $script:hostTask, $script:runTask) { Assert ($global:SwTasks[$t].Xml -ne "PREVIOUS $t" -and $global:SwTasks[$t].State -eq 'Running') "$t is the new one, running" }

Reset-World -Previous
Write-Output 'A refused config change fails before anything is stopped'
$global:SwFail['config'] = 'config set delivery.readOnly*'
$err = Invoke-Install
Assert ($err -match 'agent session' -and $err -match 'nothing was stopped') "it fails, saying why and that nothing was stopped ($err)"
Assert (-not ($global:SwCalls -match '^(stop|register|start|unregister) ')) 'no task was stopped, registered or started'
Assert ($global:SwCalls -contains 'host config set launch.resumeCount 3 --profile shadowtest') 'an earlier config change was made...'
Test-PreviousUntouched

Reset-World -Previous
Write-Output 'A failed publish fails before anything is stopped'
$global:SwFail['publish'] = $true
$err = Invoke-Install
Assert ($err -match 'publishing') "it fails, saying what ($err)"
Assert (-not ($global:SwCalls -match '^(stop|register|start|unregister) |^host ')) 'no task or config was touched'
Test-PreviousUntouched

Reset-World -Previous
Write-Output 'A relay that is not one fails before anything is touched'
$err = Invoke-Install @{ Relay = $true; RelayUrl = 'http://relay.example.com' }
Assert ($err -match 'not a relay address') "it fails, saying why ($err)"
Assert ($global:SwCalls.Count -eq 0) 'nothing was published, configured or stopped'
Test-PreviousUntouched

Reset-World -Previous
Write-Output 'A failure in the swap puts back the previous install and starts it again'
$global:SwFail['register'] = 'AgentBridgeShadowRun_*'
$err = Invoke-Install
Assert ($err -match 'stand-in: registering' -and $err -match 'put back') "it fails, saying it put the previous install back ($err)"
Assert ($global:SwCalls -contains "stop $script:hostTask") 'the previous install had been stopped...'
Test-PreviousUntouched

Reset-World -Previous
$global:SwTasks[$script:runTask].State = 'Ready'
Write-Output 'Only the tasks that were running are started again'
$global:SwFail['register'] = 'AgentBridgeShadowRun_*'
$null = Invoke-Install
Assert ($global:SwTasks[$script:hostTask].State -eq 'Running') 'the host, which was running, runs'
Assert ($global:SwTasks[$script:runTask].State -eq 'Ready' -and $global:SwTasks[$script:runTask].Xml -eq "PREVIOUS $script:runTask") 'the run, which was not, is back as it was and not started'

Reset-World
Write-Output 'A first install that fails in the swap leaves no task behind'
$global:SwFail['register'] = 'AgentBridgeShadowRun_*'
$err = Invoke-Install
Assert ($err -match 'put back') "it fails ($err)"
Assert ($global:SwTasks.Count -eq 0) 'neither task is left registered'

Reset-World
Write-Output '-Relay with no relay anywhere signs in to the one the phone uses'
$err = Invoke-Install @{ Relay = $true }
Assert ($null -eq $err) "it succeeds ($err)"
Assert ($pwaRelay -like 'https://*') "pages.yml names the phone's relay ($pwaRelay)"
Assert ($global:SwCalls -contains "host sign-in --profile shadowtest --relay $pwaRelay") 'it signed in there'
Assert ($global:SwCalls -contains 'host config set push.enabled false --profile shadowtest') 'notifications are off'
$serve = $global:SwTasks[$script:hostTask].Xml
Assert ($serve -notmatch '--local-only' -and $serve -match [regex]::Escape("--relay $pwaRelay") -and $serve -match '\(shadow\)') 'the host serves the relay it signed in to, named as the shadow'
$signIn = $global:SwCalls.IndexOf("host sign-in --profile shadowtest --relay $pwaRelay")
$firstStop = @(0..($global:SwCalls.Count - 1) | Where-Object { $global:SwCalls[$_] -like 'register *' })[0]
Assert ($signIn -lt $firstStop) 'signing in came before the swap'

Reset-World -Previous
Write-Output '-RelayUrl names the relay to sign in to'
$err = Invoke-Install @{ Relay = $true; RelayUrl = 'https://relay.example.com' }
Assert ($null -eq $err) "it succeeds ($err)"
Assert ($global:SwCalls -contains 'host sign-in --profile shadowtest --relay https://relay.example.com') 'it signed in there'

Reset-World -Previous
Write-Output 'A profile signed in to a relay that is not one fails before anything is stopped'
$global:SwSignedIn = 'http://relay.example.com'
Set-Content (Join-Path $profileFolder 'relay.txt') 'http://relay.example.com'
$env:AGENT_BRIDGE_RELAY = 'https://relay.example.org'
$err = Invoke-Install @{ Relay = $true }
Remove-Item Env:AGENT_BRIDGE_RELAY
Assert ($err -match 'not a relay address' -and $err -match 'nothing was stopped') "it fails, saying why ($err)"
Test-PreviousUntouched

Reset-World -Previous
Write-Output 'A task that cannot be exported fails before anything is touched'
$global:SwFail['export'] = $true
$err = Invoke-Install
Assert ($err -match 'stand-in: exporting') "it fails ($err)"
Assert (-not ($global:SwCalls -match '^(stop|register|start|unregister|dotnet) |^host ')) 'nothing was published, configured or stopped'
Test-PreviousUntouched

Reset-World -Previous
Write-Output 'A new host that exits at once is not kept: the previous install is put back'
$global:SwFail['exit'] = 'AgentBridgeShadowHost_*'
$err = Invoke-Install
Assert ($err -match 'wrote nothing' -and $err -match 'put back') "it fails, saying why ($err)"
Test-PreviousUntouched

Reset-World -Previous
Write-Output 'A comparison that exits at once is not kept either'
$global:SwFail['exit'] = 'AgentBridgeShadowRun_*'
$err = Invoke-Install
Assert ($err -match 'just after starting' -and $err -match 'put back') "it fails, saying why ($err)"
Test-PreviousUntouched

Reset-World -Previous
Write-Output 'After a swap that never finished, the build last known to serve is the one kept'
Move-Item $bin "$bin.previous"
New-Item -ItemType Directory -Force $bin | Out-Null
Set-Content (Join-Path $bin 'agent-bridge-host.exe') 'UNFINISHED'
Set-Content "$bin.swapping" 'then'
$global:SwFail['register'] = 'AgentBridgeShadowRun_*'
$err = Invoke-Install
Assert ($err -match 'put back') "a failing install puts back ($err)"
Test-PreviousUntouched
Assert (-not (Test-Path "$bin.swapping")) 'and the swap is finished'
$global:SwFail = @{}
Set-Content "$bin.swapping" 'then'
Copy-Item -Recurse $bin "$bin.previous"
Set-Content (Join-Path $bin 'agent-bridge-host.exe') 'UNFINISHED'
$err = Invoke-Install
Assert ($null -eq $err -and (Get-Content (Join-Path $bin 'agent-bridge-host.exe')) -eq 'NEW') "a succeeding one installs the new build ($err)"
Assert (-not (Test-Path "$bin.previous") -and -not (Test-Path "$bin.swapping")) 'and leaves nothing beside it'

Reset-World
Write-Output 'The comparison reads the agent-ha-bridge it was installed from'
$err = Invoke-Install
Assert ($null -eq $err -and $global:SwTasks[$script:runTask].Xml -match [regex]::Escape("--agent-ha-bridge-home $ahb")) "the run task names it ($err)"

function Set-AhbUsage([string]$Json) {
    Set-Content (Join-Path $ahb 'config.json') ('{"homeAssistant":{"token":"not-read"},"newSession":{"workspaces":[],"resumeCount":3},"usage":' + $Json + '}')
}

Reset-World
Write-Output "agent-ha-bridge's further Copilot logins are copied in their places, and nothing that is not a login"
Set-AhbUsage '{"copilotAccounts":["alt-login","ghp_notalogin0123456789","ALT-LOGIN","fourth"]}'
$err = Invoke-Install 3>$null
Assert ($null -eq $err) "it succeeds ($err)"
Assert ($global:SwCalls -contains 'host config set usage.copilotAccounts ["alt-login"] --profile shadowtest') 'a token and a repeat keep no login, and past the third there is no place'
Reset-World
Set-AhbUsage '{"copilotAccounts":["alt-login",5,"third"]}'
$null = Invoke-Install 3>$null
Assert ($global:SwCalls -contains 'host config set usage.copilotAccounts ["alt-login","","third"] --profile shadowtest') 'each keeps its place, one that is not a login empty'
Assert (-not ($global:SwCalls -match 'ghp_')) 'the token-shaped entry is passed nowhere'

Reset-World
Write-Output 'One login alone is a list of one'
Set-AhbUsage '{"copilotAccounts":"alt-login"}'
$null = Invoke-Install
Assert ($global:SwCalls -contains 'host config set usage.copilotAccounts ["alt-login"] --profile shadowtest') 'set as a list'

Reset-World
Write-Output "Not set in agent-ha-bridge, the profile's own is left alone"
Set-AhbUsage '{"intervalSeconds":120}'
$null = Invoke-Install
Assert (-not ($global:SwCalls -match 'usage\.copilotAccounts')) 'it is not touched'
Set-AhbUsage '{"copilotAccounts":null}'
$null = Invoke-Install
Assert (-not ($global:SwCalls -match 'usage\.copilotAccounts')) 'nor when it is null'

Reset-World -Previous
Write-Output '-WhatIf changes nothing'
$err = Invoke-Install @{ Relay = $true; WhatIf = $true }
Assert ($null -eq $err) "it succeeds ($err)"
Assert (-not ($global:SwCalls -match '^(stop|register|start|unregister|dotnet) |^host (config|workspace (add|remove)|sign-in)')) 'nothing was published, configured, signed in or stopped'
Test-PreviousUntouched

Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
