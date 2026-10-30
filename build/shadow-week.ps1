# The week-long shadow comparison with agent-ha-bridge on this machine (docs/host-shadow.md,
# "Running the week"): this host serving a profile of its own, read-only beside the daemon,
# and `shadow run` against it, each started by a per-user logon task like the app's. No admin.
#
#   pwsh build/shadow-week.ps1 -Install [-WhatIf]   # publish, mirror the workspaces, register and start both
#   pwsh build/shadow-week.ps1 -Status              # the tasks, and the summary so far
#   pwsh build/shadow-week.ps1 -Uninstall           # stop and remove both tasks; reports and profile stay
#
# Needs the .NET 10 SDK for -Install. Run it from the checkout to be compared; re-running
# -Install publishes that checkout again and restarts both.
#Requires -Version 7
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Install', Mandatory)][switch]$Install,
    [Parameter(ParameterSetName = 'Status')][switch]$Status,
    [Parameter(ParameterSetName = 'Uninstall', Mandatory)][switch]$Uninstall,
    # Not "default": a profile of its own, so the comparison never shares a device key, an
    # account or a config with a host the app installs.
    [ValidatePattern('^[a-z0-9][a-z0-9_-]{0,31}$')][string]$HostProfile = 'shadow',
    [string]$Bin = (Join-Path $env:LOCALAPPDATA 'AgentBridgeShadow\bin'),
    [Parameter(ParameterSetName = 'Install')][ValidateRange(5, 3600)][int]$Interval = 15,
    [Parameter(ParameterSetName = 'Install')][ValidateRange(0, 86400)][int]$Grace = 60
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'the week-long run is set up on Windows only' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $Bin 'agent-bridge-host.exe'
$launcher = Join-Path $Bin 'agent-bridge-hostw.exe'
$ahb = Join-Path $HOME '.agent-ha-bridge'
$user = [Security.Principal.WindowsIdentity]::GetCurrent()
# Never AgentBridgeDaemon* (agent-ha-bridge's, which the G5 probe looks for) and never the
# app's AgentBridgeHost_*; per user, since the task folder is shared by everyone on the machine.
$suffix = '{0}_{1}' -f $HostProfile, ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($user.User.Value)), 0, 4).ToLowerInvariant())
$hostTask = "AgentBridgeShadowHost_$suffix"
$runTask = "AgentBridgeShadowRun_$suffix"

# The app's logon task (LogonTaskXml): the user's interactive token, unelevated, restarted
# every minute when it fails, never stopped for battery or time, one instance. The run waits a
# minute after logon, so the host it reads is serving by then.
function New-ShadowTaskXml([string]$Description, [string[]]$Arguments, [string]$Delay) {
    $x = { param($s) [Security.SecurityElement]::Escape($s) }
    $quoted = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $wait = if ($Delay) { "<Delay>$Delay</Delay>" } else { '' }
    @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>$(& $x $Description)</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$(& $x $user.Name)</UserId>$wait</LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$(& $x $user.Name)</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
  </Settings>
  <Actions Context="Author"><Exec><Command>"$(& $x $launcher)"</Command><Arguments>$(& $x $quoted)</Arguments></Exec></Actions>
</Task>
"@
}

# Ends whatever runs from $Bin: the task's launcher and the host it started, by process id.
function Stop-ShadowProcess {
    foreach ($name in $runTask, $hostTask) {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
            if ($PSCmdlet.ShouldProcess($name, 'stop the scheduled task')) { Stop-ScheduledTask -TaskName $name }
        }
    }
    $prefix = [IO.Path]::GetFullPath($Bin).TrimEnd('\') + '\'
    foreach ($p in Get-Process -Name 'agent-bridge-host', 'agent-bridge-hostw' -ErrorAction SilentlyContinue) {
        $path = try { $p.Path } catch { $null }
        if ($path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            if ($PSCmdlet.ShouldProcess("$($p.ProcessName) ($($p.Id))", 'stop')) { Stop-Process -Id $p.Id -Force }
        }
    }
}

# agent-ha-bridge's approved folders and resume count, into this profile: the resume lists
# are compared only as far as both approve the same folders. Only newSession.workspaces and
# newSession.resumeCount are read from its config, which also holds its Home Assistant token.
function Copy-AgentHaBridgeWorkspace {
    $config = Join-Path $ahb 'config.json'
    if (-not (Test-Path $config)) { Write-Warning "no agent-ha-bridge config at $config; approve workspaces with: $exe workspace add <folder> --profile $HostProfile"; return }
    # Only newSession is kept; the rest of the file, the token with it, is dropped at once.
    $session = ([IO.File]::ReadAllText($config) | ConvertFrom-Json -AsHashtable)['newSession']
    if ($session -isnot [hashtable]) { return }
    foreach ($w in @($session['workspaces'])) {
        # A workspace is a folder, or {label, path, isolate}; a folder may start with ~.
        $entry = if ($w -is [string]) { @{ path = $w } } elseif ($w -is [hashtable]) { $w } else { continue }
        if ($entry['path'] -isnot [string] -or -not $entry['path']) { continue }
        $folder = $entry['path'] -replace '^~(?=$|[\\/])', $HOME
        $arguments = @('workspace', 'add', $folder, '--profile', $HostProfile)
        if ($entry['label'] -is [string] -and $entry['label']) { $arguments += '--label', $entry['label'] }
        if ($entry['isolate'] -eq $true) { $arguments += '--isolate' }
        if ($PSCmdlet.ShouldProcess($folder, "approve in profile $HostProfile")) {
            # Already approved is fine; the CLI says so and changes nothing.
            & $exe @arguments
        }
    }
    if ($session['resumeCount'] -is [long] -or $session['resumeCount'] -is [int]) {
        $count = [int]$session['resumeCount']
        if ($PSCmdlet.ShouldProcess("launch.resumeCount = $count", "set in profile $HostProfile")) {
            & $exe config set launch.resumeCount $count --profile $HostProfile
        }
    }}

if ($Install) {
    if (-not (Test-Path $ahb)) { throw "agent-ha-bridge is not installed for this user ($ahb); there is nothing to compare with" }
    Stop-ShadowProcess
    if ($PSCmdlet.ShouldProcess($Bin, "publish the host from $root")) {
        foreach ($project in 'src/AgentBridge.Host/AgentBridge.Host.csproj', 'src/AgentBridge.HostLauncher/AgentBridge.HostLauncher.csproj') {
            & dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -o $Bin --nologo -v q
            if ($LASTEXITCODE) { throw "publishing $project failed" }
        }
    }
    Copy-AgentHaBridgeWorkspace
    $serve = New-ShadowTaskXml "Agent Bridge host, profile $HostProfile, read-only beside agent-ha-bridge for the shadow comparison (docs/host-shadow.md)" @('serve', '--profile', $HostProfile, '--local-only') $null
    $run = New-ShadowTaskXml "Agent Bridge shadow comparison with agent-ha-bridge, profile $HostProfile (docs/host-shadow.md)" @('shadow', 'run', '--profile', $HostProfile, '--interval', "$Interval", '--grace', "$Grace") 'PT1M'
    if ($PSCmdlet.ShouldProcess("$hostTask, $runTask", 'register and start the logon tasks')) {
        Register-ScheduledTask -TaskName $hostTask -Xml $serve -Force | Out-Null
        Register-ScheduledTask -TaskName $runTask -Xml $run -Force | Out-Null
        Start-ScheduledTask -TaskName $hostTask
        # Given a moment to start serving, so the first samples find it.
        $log = Join-Path $env:LOCALAPPDATA "AgentBridge\$HostProfile\logs\host.log"
        for ($i = 0; $i -lt 30 -and -not (Test-Path $log); $i++) { Start-Sleep -Seconds 1 }
        Start-ScheduledTask -TaskName $runTask
        Write-Output "started $hostTask and $runTask. Check in a few minutes with: pwsh $PSCommandPath -Status"
    }
    return
}

if ($Uninstall) {
    Stop-ShadowProcess
    foreach ($name in $runTask, $hostTask) {
        if ((Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) -and $PSCmdlet.ShouldProcess($name, 'remove the scheduled task')) {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
        }
    }
    Write-Output "removed. The reports stay in $(Join-Path $env:LOCALAPPDATA "AgentBridge\$HostProfile\shadow"); the published host in $Bin."
    return
}

foreach ($name in $hostTask, $runTask) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    $info = if ($task) { Get-ScheduledTaskInfo -TaskName $name } else { $null }
    if ($task) { Write-Output ('{0}: {1}, last result 0x{2:x}, last run {3}' -f $name, $task.State, $info.LastTaskResult, $info.LastRunTime) }
    else { Write-Output "${name}: not registered" }
}
if (Test-Path $exe) { & $exe shadow summary --profile $HostProfile }
