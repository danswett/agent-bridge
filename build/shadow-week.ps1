# The shadow comparison with agent-ha-bridge on this machine (docs/host-shadow.md,
# "Running it"): this host serving a profile of its own, read-only beside the daemon,
# and `shadow run` against it, each started by a per-user logon task like the app's. No admin.
#
#   pwsh build/shadow-week.ps1 -Install [-WhatIf]   # publish, mirror the workspaces, register and start both
#   pwsh build/shadow-week.ps1 -Install -Relay      # the same, and the host on the relay, so the phone sees it
#   pwsh build/shadow-week.ps1 -Update              # a new build only: the tasks and the profile's config as they are
#   pwsh build/shadow-week.ps1 -Status              # the tasks, the relay, and the summary so far
#   pwsh build/shadow-week.ps1 -Uninstall           # stop and remove both tasks; reports and profile stay
#
# Needs the .NET 10 SDK for -Install. Run it from the checkout to be compared, in a terminal of
# your own: it changes the host's config and may sign in, which agent-bridge-host refuses inside an
# agent session. Re-running -Install publishes that checkout again and restarts both.
#
# -Update publishes the checkout and swaps the build in under the tasks already registered, the
# same all-or-nothing way, and changes nothing else: no config, no workspace, no sign-in, no task
# registered. So it runs inside an agent session too, without the config guard giving way. Run
# -Install instead after changing agent-ha-bridge's workspaces or logins, or this script's options.
#
# The profile is made read-only (delivery.readOnly): it takes no answer, reply, stop, launch,
# approval or transfer from any device, whether or not agent-ha-bridge is running.
#
# -Relay: the host serves the phone too, as "<computer> (shadow)", saying it is read-only and why.
# It sends no notifications (push.enabled off: agent-ha-bridge already notifies the phone). A
# profile not signed in is signed in first, with a device code printed here, to -RelayUrl,
# AGENT_BRIDGE_RELAY, the relay the profile used before, the app's, or else the relay the phone's
# app uses (S4_RELAY in .github/workflows/pages.yml). Without -Relay the host serves this machine
# only (--local-only), as before.
#
# -Install checks and prepares everything first (the relay, the build, the profile's config,
# signing in) while the previous install keeps running, and only then swaps it over. A failure
# before the swap leaves the previous tasks running as they were, and the profile's config as it
# was; one during the swap puts back the previous build and tasks, starts those that were running,
# and only then fails. build/test-shadow-week.ps1 checks both.
#Requires -Version 7
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Install', Mandatory)][switch]$Install,
    [Parameter(ParameterSetName = 'Update', Mandatory)][switch]$Update,
    [Parameter(ParameterSetName = 'Status')][switch]$Status,
    [Parameter(ParameterSetName = 'Uninstall', Mandatory)][switch]$Uninstall,
    # Not "default": a profile of its own, so the comparison never shares a device key, an
    # account or a config with a host the app installs.
    [ValidatePattern('^[a-z0-9][a-z0-9_-]{0,31}$')][string]$HostProfile = 'shadow',
    [string]$Bin = (Join-Path $env:LOCALAPPDATA 'AgentBridgeShadow\bin'),
    [Parameter(ParameterSetName = 'Install')][ValidateRange(5, 3600)][int]$Interval = 15,
    [Parameter(ParameterSetName = 'Install')][ValidateRange(0, 86400)][int]$Grace = 60,
    [Parameter(ParameterSetName = 'Install')][switch]$Relay,
    [Parameter(ParameterSetName = 'Install')][string]$RelayUrl,
    # agent-ha-bridge's folder for this user, to compare with and copy the workspaces from.
    [string]$AgentHaBridgeHome = (Join-Path $HOME '.agent-ha-bridge')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'the week-long run is set up on Windows only' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $Bin 'agent-bridge-host.exe'
$launcher = Join-Path $Bin 'agent-bridge-hostw.exe'
$ahb = $AgentHaBridgeHome
# A new build is published beside the running one, and the running one kept beside it until the
# new one is serving.
$staging = "$Bin.new"
$previousBin = "$Bin.previous"
# Present from stopping the previous install until the new one serves, or the previous one is back:
# found at the start, the last swap never finished, and bin.previous is the build to keep.
$swapMarker = "$Bin.swapping"
$script:UnfinishedSwap = Test-Path -LiteralPath $swapMarker
$profileConfig = Join-Path $env:LOCALAPPDATA "AgentBridge\$HostProfile\config.json"

# agent-bridge-host, as published for this install ($exe moves from the new build to $Bin as the
# install goes). build/test-shadow-week.ps1 plays it with a function of its own.
if (-not (Test-Path function:Invoke-ShadowHost)) {
    function Invoke-ShadowHost { & $exe @args }
}
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
    Stop-ShadowBinProcess
}

function Stop-ShadowBinProcess {
    $prefix = [IO.Path]::GetFullPath($Bin).TrimEnd('\') + '\'
    foreach ($p in Get-Process -Name 'agent-bridge-host', 'agent-bridge-hostw' -ErrorAction SilentlyContinue) {
        $path = try { $p.Path } catch { $null }
        if ($path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            if ($PSCmdlet.ShouldProcess("$($p.ProcessName) ($($p.Id))", 'stop')) { Stop-Process -Id $p.Id -Force }
        }
    }
}

# Why a config change failed. Exit 4 is agent-bridge-host refusing to change its own config from
# inside an agent session (#44, design §6.3 invariant 13), which only a person at an ordinary
# terminal can get past.
function Get-ConfigRefusal([string]$What) {
    if ($LASTEXITCODE -eq 4) { return "$What`: agent-bridge-host does not change its config from inside an agent session; run -Install from an ordinary terminal" }
    $What
}
# agent-ha-bridge's approved folders and resume count, into this profile: the resume lists
# are compared only as far as both approve the same folders. Made to match on every install,
# so a label, an isolate flag or a workspace changed or removed there since is changed here
# too; any failure stops the install rather than start a week that cannot agree. Its further
# Copilot logins (usage.copilotAccounts) too, when it names any, so both report the same
# accounts in the same places. Only newSession.workspaces, newSession.resumeCount and
# usage.copilotAccounts are read from its config, which also holds its Home Assistant token.
function Sync-AgentHaBridgeWorkspace {
    $config = Join-Path $ahb 'config.json'
    if (-not (Test-Path $config)) { throw "no agent-ha-bridge config at $config, so its workspaces cannot be copied" }
    # Only newSession and usage.copilotAccounts are kept; the rest of the file, the token with it,
    # is dropped at once.
    $parsed = [IO.File]::ReadAllText($config) | ConvertFrom-Json -AsHashtable
    $session = $parsed['newSession']
    if ($session -isnot [hashtable]) { $session = @{} }
    $usage = $parsed['usage']
    # Comma-wrapped, so a list of one stays a list.
    $accounts = if ($usage -is [hashtable] -and $usage.ContainsKey('copilotAccounts')) { , $usage['copilotAccounts'] } else { $null }
    $parsed = $null
    $usage = $null

    # Every workspace the profile has now goes first, so what is added is exactly the daemon's.
    # Not under -WhatIf, when nothing has been published to ask.
    $listed = if ($WhatIfPreference) { @() } else { @(Invoke-ShadowHost workspace list --profile $HostProfile) }
    if (-not $WhatIfPreference -and $LASTEXITCODE) { throw "the profile's workspaces could not be listed: $($listed -join ' ')" }
    foreach ($line in $listed) {
        $fields = ([string]$line).Split("`t")
        if ($fields.Count -lt 2) { continue }
        # By label: a workspace whose folder is gone is listed with a note after its path.
        if ($PSCmdlet.ShouldProcess($fields[0], "remove from profile $HostProfile, to copy agent-ha-bridge's afresh")) {
            Invoke-ShadowHost workspace remove $fields[0] --profile $HostProfile | Out-Null
            if ($LASTEXITCODE) { throw (Get-ConfigRefusal "could not remove '$($fields[0])' from profile $HostProfile") }
        }
    }

    # Labelled as agent-ha-bridge labels them (Get-BridgeWorkspaceChoices): the configured
    # label, else the folder's name; a repeated label gets its path after it, and an entry
    # still repeated, or with an isolate that is not true or false, is not offered at all.
    $seen = @{}
    $paths = @{}
    foreach ($w in @($session['workspaces'])) {
        # A workspace is a folder, or {label, path, isolate}; a folder may start with ~.
        $entry = if ($w -is [string]) { @{ path = $w } } elseif ($w -is [hashtable]) { $w } else { continue }
        if ($entry['path'] -isnot [string] -or -not $entry['path'].Trim()) { continue }
        $folder = [string]$entry['path']
        if ($folder.StartsWith('~')) { $folder = Join-Path $HOME $folder.Substring(1).TrimStart('\', '/') }
        try { $folder = [IO.Path]::GetFullPath($folder) } catch { continue }
        # agent-ha-bridge offers nothing in a folder that is gone either.
        if (-not [IO.Directory]::Exists($folder)) { Write-Warning "$folder does not exist; not approved"; continue }
        if ($entry.ContainsKey('isolate') -and $entry['isolate'] -isnot [bool]) { Write-Warning "$folder has an isolate that is not true or false; agent-ha-bridge does not offer it"; continue }
        $label = if ($entry['label'] -is [string] -and $entry['label'].Trim()) { $entry['label'] } else { [IO.Path]::GetFileName($folder.TrimEnd('\', '/')) }
        if (-not $label) { $label = $folder }
        if ($seen.ContainsKey($label)) { $label = "$label ($folder)" }
        if ($seen.ContainsKey($label)) { continue }
        $seen[$label] = $true
        # The host approves a folder once; the daemon lists it twice under two labels.
        if ($paths.ContainsKey($folder.ToLowerInvariant())) { Write-Warning "$folder is listed twice; approved once, as '$($paths[$folder.ToLowerInvariant()])'"; continue }
        $paths[$folder.ToLowerInvariant()] = $label
        $arguments = @('workspace', 'add', $folder, '--label', $label, '--profile', $HostProfile)
        if ($entry['isolate'] -eq $true) { $arguments += '--isolate' }
        if ($PSCmdlet.ShouldProcess("$folder as '$label'", "approve in profile $HostProfile")) {
            Invoke-ShadowHost @arguments
            if ($LASTEXITCODE) { throw (Get-ConfigRefusal "could not approve $folder in profile $HostProfile") }
        }
    }
    # Absent there means its default, which is this host's default too.
    $count = $session['resumeCount']
    $arguments = if ($count -is [long] -or $count -is [int]) { @('config', 'set', 'launch.resumeCount', [string][int]$count) } else { @('config', 'unset', 'launch.resumeCount') }
    if ($PSCmdlet.ShouldProcess(($arguments[1..2] -join ' '), "in profile $HostProfile")) {
        Invoke-ShadowHost @arguments --profile $HostProfile
        if ($LASTEXITCODE) { throw (Get-ConfigRefusal "could not set launch.resumeCount in profile $HostProfile") }
    }
    Sync-CopilotAccount $accounts
}

# usage.copilotAccounts as agent-ha-bridge reads it (Get-BridgeCopilotExtraAccounts): a list,
# or one login alone. Not set there (absent or null), the profile keeps its own, so one set by
# hand survives a reinstall. Each of the first three keeps its place, the Nth naming
# usage_copilot_<N+1>; one the daemon would skip (not a login, or repeating an earlier one) goes
# over as "", the host's place kept unused, and is never repeated: a token pasted where a login
# belongs would otherwise reach a second config, and this window. Past the third there is no
# place, in either.
function Sync-CopilotAccount($Accounts) {
    if ($null -eq $Accounts) { return }
    if ($Accounts -is [System.Collections.IDictionary]) {
        Write-Warning "agent-ha-bridge's usage.copilotAccounts is not a list of logins; profile $HostProfile keeps its own"
        return
    }
    $entries = @($Accounts)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $logins = [Collections.Generic.List[string]]::new()
    foreach ($entry in $entries | Select-Object -First 3) {
        $login = if ($entry -is [string]) { $entry.Trim() } else { '' }
        $usable = $login -cmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$' -and $login -cnotmatch '^(?:gh[opusr]_|github_pat_)' -and $seen.Add($login)
        $logins.Add($(if ($usable) { $login } else { '' }))
    }
    # A place left unused at the end holds no later login's place.
    while ($logins.Count -and -not $logins[$logins.Count - 1]) { $logins.RemoveAt($logins.Count - 1) }
    $skipped = @($logins | Where-Object { -not $_ }).Count
    if ($skipped) { Write-Warning "$skipped of agent-ha-bridge's usage.copilotAccounts are not logins agent-ha-bridge uses; each keeps its place, unused" }
    if ($entries.Count -gt 3) { Write-Warning "agent-ha-bridge's usage.copilotAccounts has $($entries.Count) entries; only the first three have places" }
    $logins = [string[]]$logins
    $json = ConvertTo-Json -InputObject $logins -Compress
    if ($PSCmdlet.ShouldProcess("usage.copilotAccounts ($($logins.Count) entr$(if ($logins.Count -eq 1) { 'y' } else { 'ies' }))", "set in profile $HostProfile, as agent-ha-bridge has it")) {
        Invoke-ShadowHost config set usage.copilotAccounts $json --profile $HostProfile
        if ($LASTEXITCODE) { throw (Get-ConfigRefusal "could not set usage.copilotAccounts in profile $HostProfile") }
    }
}

# The profile's account, or $null when it has not signed in (agent-bridge-host whoami).
function Get-ShadowAccount {
    $line = [string](Invoke-ShadowHost whoami --profile $HostProfile)
    if ($LASTEXITCODE) { throw "agent-bridge-host whoami failed: $line" }
    if ($line -match '\baccount (\S+)' -and $Matches[1] -ne '(not') { return $Matches[1] }
    $null
}

# The relay the phone's app uses: what the Pages build bakes into it (S4_RELAY), read from this
# checkout rather than copied here.
function Get-PwaRelay {
    $pages = Join-Path $root '.github\workflows\pages.yml'
    if (-not (Test-Path $pages)) { return $null }
    $m = Select-String -Path $pages -Pattern '^\s*S4_RELAY:\s*(\S+)\s*$' | Select-Object -First 1
    if ($m) { $m.Matches[0].Groups[1].Value } else { $null }
}

# Where to sign in a profile that has not: as asked, else as agent-bridge-host itself would
# (AGENT_BRIDGE_RELAY, then the profile's last), else the relay the app's own profile signed in
# to, else the one the phone's app uses. A machine without the app (DEV-VM1) has only the last.
function Get-ShadowRelayUrl {
    if ($RelayUrl) { return $RelayUrl }
    if ($env:AGENT_BRIDGE_RELAY) { return $env:AGENT_BRIDGE_RELAY }
    foreach ($p in $HostProfile, 'default') {
        $saved = Join-Path $env:LOCALAPPDATA "AgentBridge\$p\relay.txt"
        if (Test-Path $saved) { $url = (Get-Content -Raw $saved).Trim(); if ($url) { return $url } }
    }
    if ($pwa = Get-PwaRelay) { return $pwa }
    throw 'no relay to sign in to: pass -RelayUrl <url>'
}

# A relay address the host can use: https, or http only to this machine (a development relay).
function Assert-ShadowRelayUrl([string]$Url) {
    $u = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$u) -or -not ($u.Scheme -eq 'https' -or ($u.Scheme -eq 'http' -and $u.IsLoopback))) {
        throw "'$Url' is not a relay address: give one like https://relay.example.com"
    }
}

# The relay that issued the profile's account (sign-in saves it), or $null.
function Get-ShadowSignedInRelay {
    $saved = Join-Path $env:LOCALAPPDATA "AgentBridge\$HostProfile\relay.txt"
    if (Test-Path $saved) { $url = (Get-Content -Raw $saved).Trim(); if ($url) { return $url } }
    $null
}

# On the relay: no notifications from this profile, and signed in. Sets $script:ShadowRelay to
# the relay that issued its account, which the task then names (--relay), so serve never sends
# that token to another one (AGENT_BRIDGE_RELAY, say, which serve would otherwise prefer).
$script:ShadowRelay = $null
function Set-ShadowRelay {
    if ($PSCmdlet.ShouldProcess("profile $HostProfile", 'turn notifications off (push.enabled false)')) {
        Invoke-ShadowHost config set push.enabled false --profile $HostProfile
        if ($LASTEXITCODE) { throw (Get-ConfigRefusal "could not turn notifications off in profile $HostProfile") }
    }
    if ($WhatIfPreference) {
        # Nothing is published to ask whether the profile is signed in; say where it would go.
        $PSCmdlet.ShouldProcess("profile $HostProfile", "sign in to $(Get-ShadowRelayUrl) with a device code, unless it is signed in already") | Out-Null
        return
    }
    $account = Get-ShadowAccount
    $issuer = Get-ShadowSignedInRelay
    if ($account -and $issuer) {
        if ($RelayUrl -and $RelayUrl.TrimEnd('/') -ne $issuer.TrimEnd('/')) {
            throw "profile $HostProfile is signed in to $issuer, not $RelayUrl; sign it in again there (agent-bridge-host sign-in --profile $HostProfile --relay $RelayUrl) first"
        }
        Assert-ShadowRelayUrl $issuer
        Write-Output "profile $HostProfile is signed in as $account on $issuer"
        $script:ShadowRelay = $issuer
        return
    }
    $url = Get-ShadowRelayUrl
    if (-not $PSCmdlet.ShouldProcess("profile $HostProfile", "sign in to $url with a device code")) { return }
    Write-Output "Signing profile $HostProfile in to $url. Open the address below and enter the code, as the account your phone uses:"
    Invoke-ShadowHost sign-in --profile $HostProfile --relay $url
    if ($LASTEXITCODE -eq 4) { throw 'agent-bridge-host does not sign in from inside an agent session; run -Install -Relay from an ordinary terminal' }
    if ($LASTEXITCODE) { throw "signing profile $HostProfile in failed" }
    $script:ShadowRelay = Get-ShadowSignedInRelay
    if (-not $script:ShadowRelay) { throw "signed in, but profile $HostProfile has no relay saved" }
    Assert-ShadowRelayUrl $script:ShadowRelay
}

# The previous install's tasks as they are now: what to put back, and which were running.
function Get-ShadowTaskState {
    foreach ($name in $hostTask, $runTask) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Name = $name
            Xml = if ($task) { Export-ScheduledTask -TaskName $name } else { $null }
            Running = [bool]($task -and [string]$task.State -eq 'Running')
        }
    }
}

# Puts back what a failed swap replaced: the previous build, the profile's config and the tasks,
# and starts the tasks that were running. Each step is tried whatever the others do; what could
# not be put back is said, with the reason. -BinariesOnly (-Update): only the build was replaced,
# so the config and the tasks' definitions are not touched at all.
function Restore-ShadowInstall($Previous, $Config, [switch]$BinariesOnly) {
    $problems = @()
    foreach ($name in $runTask, $hostTask) {
        try { if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $name } } catch { $problems += "stopping $name`: $_" }
    }
    try { Stop-ShadowBinProcess } catch { $problems += "stopping the new build: $_" }
    try {
        if (Test-Path $previousBin) {
            if (Test-Path $Bin) { Remove-Item $Bin -Recurse -Force }
            Move-Item $previousBin $Bin
        }
    }
    catch { $problems += "putting back the previous build: $_" }
    if (-not $BinariesOnly) {
        try { Restore-ShadowConfig $Config } catch { $problems += "putting back the profile's config: $_" }
    }
    foreach ($t in $Previous | Where-Object { -not $BinariesOnly }) {
        try {
            if ($t.Xml) { Register-ScheduledTask -TaskName $t.Name -Xml $t.Xml -Force | Out-Null }
            elseif (Get-ScheduledTask -TaskName $t.Name -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $t.Name -Confirm:$false }
        }
        catch { $problems += "putting back $($t.Name): $_" }
    }
    foreach ($t in $Previous | Where-Object Running) {
        try { Start-ScheduledTask -TaskName $t.Name } catch { $problems += "starting $($t.Name): $_" }
    }
    # Back as it was: the next install has no unfinished swap to recover.
    if (-not $problems.Count) { Remove-Item -LiteralPath $swapMarker -Force -ErrorAction SilentlyContinue }
    $problems
}

# The profile's config.json as it was before this install, or $null when it had none.
function Save-ShadowConfig { if (Test-Path $profileConfig) { , [IO.File]::ReadAllBytes($profileConfig) } else { $null } }
function Restore-ShadowConfig($Saved) {
    if ($null -eq $Saved) { if (Test-Path $profileConfig) { Remove-Item $profileConfig -Force } }
    elseif (-not (Test-Path $profileConfig) -or -not [Linq.Enumerable]::SequenceEqual([byte[]]$Saved, [IO.File]::ReadAllBytes($profileConfig))) {
        New-Item -ItemType Directory -Force (Split-Path $profileConfig) | Out-Null
        [IO.File]::WriteAllBytes($profileConfig, $Saved)
    }
}

# The new host has started serving: its log written since it was started, and its task still
# running. Start-ScheduledTask only asks; a host that exits at once would otherwise be committed
# and the previous build let go.
function Wait-ShadowHostReady([datetime]$Since) {
    $log = Join-Path $env:LOCALAPPDATA "AgentBridge\$HostProfile\logs\host.log"
    for ($i = 0; $i -lt 60; $i++) {
        if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -ge $Since.AddSeconds(-2)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not ((Test-Path $log) -and (Get-Item $log).LastWriteTime -ge $Since.AddSeconds(-2))) { throw "the new host wrote nothing to $log within 30 seconds of starting" }
}

# Both tasks still running a few seconds after starting: a launcher that ends at once is a host or
# comparison that could not start.
function Assert-ShadowTaskRunning {
    Start-Sleep -Seconds 5
    foreach ($name in $hostTask, $runTask) {
        $state = [string](Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue).State
        if ($state -ne 'Running') {
            $info = Get-ScheduledTaskInfo -TaskName $name
            throw ("{0} is {1} just after starting (last result 0x{2:x})" -f $name, $state, $info.LastTaskResult)
        }
    }
}

# The program a registered task runs, from its exported XML; $null when it names none.
function Get-ShadowTaskCommand([string]$Xml) {
    try { $command = ([xml]$Xml).Task.Actions.Exec.Command } catch { return $null }
    if ($command -isnot [string] -or -not $command.Trim('"', ' ')) { return $null }
    [IO.Path]::GetFullPath($command.Trim('"', ' '))
}

# Moved with a few tries: a process just ended may hold its files a moment longer.
function Move-ShadowFolder([string]$From, [string]$To) {
    for ($i = 1; ; $i++) {
        try { Move-Item $From $To; return }
        catch { if ($i -ge 10) { throw }; Start-Sleep -Milliseconds 500 }
    }
}

if ($Install -or $Update) {
    # 1. Everything that can fail, while the previous install keeps running.
    if ($Install) {
        if (-not (Test-Path $ahb)) { throw "agent-ha-bridge is not installed for this user ($ahb); there is nothing to compare with" }
        if ($RelayUrl) { Assert-ShadowRelayUrl $RelayUrl }
        if ($Relay) { Assert-ShadowRelayUrl (Get-ShadowRelayUrl) }
    }
    # What to put back, read before anything changes: a task that cannot be exported stops the
    # install here, with nothing touched.
    $previous = @(Get-ShadowTaskState)
    # -Update replaces a build under tasks that exist; what they run is -Install's to decide.
    if ($Update) {
        if (@($previous | Where-Object { -not $_.Xml }).Count -or -not (Test-Path -LiteralPath $exe)) {
            throw "nothing to update: $hostTask and $runTask, and a build in $Bin, come from -Install first (nothing was changed)"
        }
        # And they must run the build in $Bin. Installed to another folder since, they would go on
        # running that one, and its log and running tasks would pass for the new build serving
        # (found in review, #108).
        foreach ($t in $previous) {
            if ((Get-ShadowTaskCommand $t.Xml) -ne [IO.Path]::GetFullPath($launcher)) {
                throw "$($t.Name) does not run $launcher, so -Update -Bin $Bin would not reach it; give the -Bin it was installed with, or run -Install (nothing was changed)"
            }
        }
    }
    $config = if ($Install) { Save-ShadowConfig } else { $null }
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    $serveArguments = @('serve', '--profile', $HostProfile)
    # prepare: only bin.new and the profile's config have changed. swap: the previous install has
    # been stopped. done: the new one is serving.
    $phase = 'prepare'
    $handled = $false
    try {
        if ($PSCmdlet.ShouldProcess($staging, "publish the host from $root")) {
            foreach ($project in 'src/AgentBridge.Host/AgentBridge.Host.csproj', 'src/AgentBridge.HostLauncher/AgentBridge.HostLauncher.csproj') {
                dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -o $staging --nologo -v q
                if ($LASTEXITCODE) { throw "publishing $project failed" }
            }
        }
        $exe = Join-Path $staging 'agent-bridge-host.exe'
        if ($Install) {
            Sync-AgentHaBridgeWorkspace
            # Read-only whatever agent-ha-bridge is doing: G5 alone lasts only while its daemon runs, and an
            # update, a crash or a logoff race restarting it would leave this host acting in the gap.
            if ($PSCmdlet.ShouldProcess("profile $HostProfile", 'make it read-only (delivery.readOnly true)')) {
                Invoke-ShadowHost config set delivery.readOnly true --profile $HostProfile
                if ($LASTEXITCODE) { throw (Get-ConfigRefusal "could not make profile $HostProfile read-only") }
            }
            if ($Relay) {
                Set-ShadowRelay
                # Named apart from the machine's own host, on the phone's machine list.
                $serveArguments += @('--name', "$env:COMPUTERNAME (shadow)")
                if ($script:ShadowRelay) { $serveArguments += @('--relay', $script:ShadowRelay) }
            }
            else { $serveArguments += '--local-only' }
            $runArguments = @('shadow', 'run', '--profile', $HostProfile, '--interval', "$Interval", '--grace', "$Grace")
            # The daemon compared is the one the workspaces were copied from.
            if ($PSBoundParameters.ContainsKey('AgentHaBridgeHome')) { $runArguments += @('--agent-ha-bridge-home', (Resolve-Path -LiteralPath $ahb).ProviderPath) }
            $serve = New-ShadowTaskXml "Agent Bridge host, profile $HostProfile, read-only beside agent-ha-bridge for the shadow comparison (docs/host-shadow.md)" $serveArguments $null
            $run = New-ShadowTaskXml "Agent Bridge shadow comparison with agent-ha-bridge, profile $HostProfile (docs/host-shadow.md)" $runArguments 'PT1M'
        }

        # 2. The swap: stop the previous install, put the new build in its place, register, start,
        # and see both running before the previous build is let go.
        $plan = if ($Install) { 'stop the previous install, swap in the new build, register and start the logon tasks' } else { 'stop the tasks, swap in the new build, and start them as they are registered' }
        if (-not $PSCmdlet.ShouldProcess("$hostTask, $runTask", $plan)) { return }
        $phase = 'swap'
        Set-Content -LiteralPath $swapMarker -Value (Get-Date -Format o)
        Stop-ShadowProcess
        if (Test-Path $previousBin) {
            # Left by a swap that never finished: bin.previous is the last build known to serve, and
            # bin the one that never did, which goes instead. Else a finished swap's leftover.
            if (-not (Test-Path $Bin) -or $script:UnfinishedSwap) { if (Test-Path $Bin) { Remove-Item $Bin -Recurse -Force } }
            else { Remove-Item $previousBin -Recurse -Force }
        }
        if (Test-Path $Bin) { Move-ShadowFolder $Bin $previousBin }
        Move-ShadowFolder $staging $Bin
        $exe = Join-Path $Bin 'agent-bridge-host.exe'
        if ($Install) {
            Register-ScheduledTask -TaskName $hostTask -Xml $serve -Force | Out-Null
            Register-ScheduledTask -TaskName $runTask -Xml $run -Force | Out-Null
        }
        $started = Get-Date
        Start-ScheduledTask -TaskName $hostTask
        Wait-ShadowHostReady $started
        Start-ScheduledTask -TaskName $runTask
        Assert-ShadowTaskRunning
        $phase = 'done'
    }
    catch {
        $failure = $_
        $handled = $true
        if ($phase -eq 'prepare') {
            if ($Install) { Restore-ShadowConfig $config }
            Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
            throw "$failure (nothing was stopped or replaced: the previous install is still running, unchanged)"
        }
        $problems = @(Restore-ShadowInstall $previous $config -BinariesOnly:$Update)
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        if ($problems.Count) { throw "$failure; and the previous install could not all be put back: $($problems -join '; ')" }
        throw "$failure (the previous install was put back, and the tasks that were running were started again)"
    }
    finally {
        # Interrupted (Ctrl+C runs no catch), or -Confirm declined at the swap: put back all the
        # same, and say what could not be, on stderr, since nothing else is left to say it.
        if (-not $handled -and $phase -ne 'done') {
            if ($phase -eq 'prepare') {
                if ($Install) { Restore-ShadowConfig $config }
                Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
            }
            else {
                foreach ($problem in @(Restore-ShadowInstall $previous $config -BinariesOnly:$Update)) { [Console]::Error.WriteLine("shadow-week: not put back: $problem") }
                Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Remove-Item -LiteralPath $swapMarker -Force -ErrorAction SilentlyContinue
    if (Test-Path $previousBin) { Remove-Item $previousBin -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Output "started $hostTask and $runTask. Check in a few minutes with: pwsh $PSCommandPath -Status"
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
$serving = Get-ScheduledTask -TaskName $hostTask -ErrorAction SilentlyContinue
if ($serving) {
    $onRelay = [string]$serving.Actions[0].Arguments -notmatch '--local-only'
    $account = if (Test-Path $exe) { Get-ShadowAccount } else { $null }
    Write-Output ('relay: {0}; {1}' -f ($onRelay ? 'on (the phone sees this host)' : 'off (--local-only)'), ($account ? "signed in as $account" : 'not signed in'))
    # What the host last said about its relay connection.
    $log = Join-Path $env:LOCALAPPDATA "AgentBridge\$HostProfile\logs\host.log"
    if ($onRelay -and (Test-Path $log)) {
        $said = Get-Content $log -Tail 2000 | Select-String -Pattern 'connected as |connection ended|not signed in|refused|relay' | Select-Object -Last 1
        if ($said) { Write-Output "relay, last logged: $($said.Line)" }
    }
}
if (Test-Path $exe) { Invoke-ShadowHost shadow summary --profile $HostProfile }
