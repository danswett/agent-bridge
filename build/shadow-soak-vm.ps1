#Requires -Version 7.3
<#
.SYNOPSIS
    The shadow soak on the Azure rehearsal VMs (docs/host-shadow.md, "A scripted soak"): VM-M runs
    agent-ha-bridge with a read-only shadow host beside it, and a scripted driver launches,
    prompts, answers, compacts, ends and resumes Copilot, Claude and Codex sessions through VM-W's
    disposable Home Assistant until `shadow summary --gate` passes.

.DESCRIPTION
    Runs from the operator's machine. Everything inside the VMs goes through
    build/rehearsal-azure-vmw.ps1 -InvokeScript, so through `az vm run-command` and never SSH;
    nothing here talks to any Home Assistant but VM-W's disposable one, through VM-M's loopback.

    -Prepare   VM-W: Mosquitto running (the disposable instance's MQTT, which agent-ha-bridge
               publishes through). VM-M: the tunnel to VM-W's Home Assistant as a logon task of
               `rehearsal`; agent-ha-bridge checked (installed, daemon beating, pointed at
               127.0.0.1); the .NET SDK for `rehearsal`; the shadow host built from -HostRef and
               installed (build/shadow-week.ps1 -Install, or -Update once installed); the driver
               (agent-bridge-rehearsal) built from -DriverRef; the app's own host stopped, so
               only agent-ha-bridge and the shadow host are left to compare (-KeepAppHost to leave it).
               With -DriverOnly, only a new driver build (between -Stop and -Start).
    -Start     The driver as a logon task of `rehearsal` (agent-bridge-rehearsal soak run), and
               auto-shutdown off for both VMs, so the soak runs through the night. -Fresh starts the
               episodes again from the first, rather than carrying on from soak.json.
    -Status    What the driver has done, its log's end, and `shadow summary --gate`.
    -Collect   A session's raw transcripts, zipped on VM-M and copied here in run-command-sized
               pieces, then unzipped into -OutDir.
    -Stop      The driver's task stopped; the shadow host and agent-ha-bridge left running.
    -Finish    The driver stopped, the app's host started again, auto-shutdown back on, and both
               VMs deallocated. Snapshots and the resource group are left alone.

    The driver's state (soak.json, soak.log) is on VM-M in C:\rehearsal\soak\state, so -Start
    after -Stop, a reboot or a -Finish carries on from the next episode.

.EXAMPLE
    ./build/shadow-soak-vm.ps1 -Subscription <id> -Prepare -DriverRef feat/shadow-soak-vm

.EXAMPLE
    ./build/shadow-soak-vm.ps1 -Subscription <id> -Start -RestartDaemonAt 20 -RestartShadowAt 40

.EXAMPLE
    ./build/shadow-soak-vm.ps1 -Subscription <id> -Status

.EXAMPLE
    ./build/shadow-soak-vm.ps1 -Subscription <id> -Collect 0894c91f-f3f5-4546-8684-a8f9e6ad58e7 -OutDir .\transcripts\copilot
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $Subscription,

    [Parameter(Mandatory, ParameterSetName = 'Prepare')][switch] $Prepare,
    [Parameter(Mandatory, ParameterSetName = 'Start')][switch] $Start,
    [Parameter(ParameterSetName = 'Status')][switch] $Status,
    [Parameter(Mandatory, ParameterSetName = 'Collect')][string] $Collect,
    [Parameter(Mandatory, ParameterSetName = 'Stop')][switch] $Stop,
    [Parameter(Mandatory, ParameterSetName = 'Finish')][switch] $Finish,

    # The commit the shadow host is built from, and the driver's.
    [Parameter(ParameterSetName = 'Prepare')][ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $HostRef = 'main',
    [Parameter(ParameterSetName = 'Prepare')][ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $DriverRef = 'main',
    [Parameter(ParameterSetName = 'Prepare')][switch] $KeepAppHost,
    [Parameter(ParameterSetName = 'Prepare')][switch] $DriverOnly,

    [Parameter(ParameterSetName = 'Start')][switch] $Fresh,
    [Parameter(ParameterSetName = 'Start')][ValidateRange(1, 500)][int] $Count = 66,
    [Parameter(ParameterSetName = 'Start')][ValidateRange(1, 240)][int] $EveryMinutes = 22,
    [Parameter(ParameterSetName = 'Start')][int] $RestartDaemonAt = 20,
    [Parameter(ParameterSetName = 'Start')][int] $RestartShadowAt = 40,
    # Small, cheap models: the soak is for coverage of events, not quality.
    [Parameter(ParameterSetName = 'Start')][hashtable] $Models = @{ Copilot = 'claude-haiku-4.5'; Claude = 'haiku'; Codex = 'gpt-5.6-luna' },
    [Parameter(ParameterSetName = 'Start')][hashtable] $Efforts = @{ Codex = 'low' },

    [Parameter(Mandatory, ParameterSetName = 'Collect')][string] $OutDir,

    [string] $AzureConfigDir = (Join-Path $HOME '.azure-agentbridge')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$vmScript = Join-Path $PSScriptRoot 'rehearsal-azure-vmw.ps1'
$scratch = Join-Path $HOME '.agentbridge-rehearsal'
$soak = 'C:\rehearsal\soak'
$repoUrl = 'https://github.com/danswett/agent-bridge.git'
$env:AZURE_CONFIG_DIR = $AzureConfigDir

# One script in one VM, as SYSTEM or as its desktop user, through the rehearsal script.
function Invoke-Guest([string] $Role, [string] $Script, [switch] $AsDesktopUser, [int] $TimeoutMinutes = 30) {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $file = Join-Path $scratch "soak-$([guid]::NewGuid().ToString('n')).ps1"
    Set-Content -Path $file -Value $Script -Encoding utf8
    try {
        $extra = if ($AsDesktopUser) { @{ AsDesktopUser = $true; TimeoutMinutes = $TimeoutMinutes } } else { @{} }
        $out = & $vmScript -Subscription $Subscription -AzureConfigDir $AzureConfigDir -Role $Role -InvokeScript $file @extra 6>$null
        return (($out | Out-String) -replace '(?m)^JOB-DONE\s*$', '').Trim()
    } finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

# The commit a ref names, so VM-M checks out exactly that and says so.
function Get-RefCommit([string] $Ref) {
    $line = git ls-remote $repoUrl $Ref "refs/heads/$Ref" 2>$null | Select-Object -First 1
    if ($line) { return ($line -split '\s+')[0] }
    if ($Ref -match '^[0-9a-f]{7,40}$') { return $Ref }
    throw "no branch or commit $Ref in $repoUrl"
}

$dotnetEnv = @'
$env:PATH = "$HOME\.dotnet;$env:PATH"; $env:DOTNET_ROOT = "$HOME\.dotnet"; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
function Get-Checkout([string] $Dir, [string] $Commit) {
    if (-not (Test-Path "$Dir\.git")) { New-Item -ItemType Directory $Dir -Force | Out-Null; git -C $Dir init -q 2>&1 | Out-Null; git -C $Dir remote add origin __REPO__ 2>&1 | Out-Null }
    git -C $Dir fetch -q --depth 1 origin $Commit 2>&1 | Out-Null
    git -C $Dir checkout -q --force FETCH_HEAD 2>&1 | Out-Null
    $head = git -C $Dir rev-parse HEAD
    if ($head -ne $Commit) { throw "$Dir is at $head, not $Commit" }
}
'@.Replace('__REPO__', $repoUrl)

if ($Prepare -and $DriverOnly) {
    # A new driver build only, between episodes: -Stop first, -Start after.
    $driverCommit = Get-RefCommit $DriverRef
    Invoke-Guest M -AsDesktopUser -TimeoutMinutes 20 -Script ($dotnetEnv + @'
Get-Checkout '__SOAK__\driver' '__DRIVER__'
dotnet publish '__SOAK__\driver\tools\AgentBridge.Rehearsal' -c Release -r win-x64 --self-contained -o '__SOAK__\tool' --nologo -v q 2>&1 | Select-Object -Last 2
if ($LASTEXITCODE) { throw 'publishing the driver failed' }
"driver at __DRIVER__"
'@).Replace('__SOAK__', $soak).Replace('__DRIVER__', $driverCommit) | Write-Output
    return
}

if ($Prepare) {
    $hostCommit = Get-RefCommit $HostRef
    $driverCommit = Get-RefCommit $DriverRef
    Write-Information "VM-W: Mosquitto" -InformationAction Continue
    # A container stopped by hand stays stopped across a restart (it was, after the rehearsal).
    Invoke-Guest W -AsDesktopUser -TimeoutMinutes 5 -Script @'
wsl.exe -d Ubuntu -u root -- docker update --restart unless-stopped ha-mosquitto-1 2>&1 | Out-Null
wsl.exe -d Ubuntu -u root -- docker start ha-mosquitto-1 2>&1 | Out-Null
wsl.exe -d Ubuntu -u root -- docker ps --format '{{.Names}} {{.Status}}' 2>&1
'@ | Write-Output

    Write-Information "VM-M: the tunnel to VM-W's Home Assistant" -InformationAction Continue
    Invoke-Guest M -Script @'
$user = 'rehearsal'
$dir = 'C:\ProgramData\AgentBridgeSoak'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
& icacls.exe $dir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' "${user}:(OI)(CI)RX" | Out-Null
$loop = Join-Path $dir 'tunnel.ps1'
Set-Content -Path $loop -Encoding utf8 -Value @"
# VM-M's forward to VM-W's Home Assistant (parity-checklist A-04), kept up for the soak.
while (`$true) {
    & ssh.exe -N -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -L 8123:127.0.0.1:8123 rehearsal@VM-W
    Start-Sleep -Seconds 10
}
"@
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$loop`""
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'AgentBridgeSoakTunnel' -Action $action -Principal $principal -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'AgentBridgeSoakTunnel'
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Seconds 5
    $up = try { Invoke-WebRequest http://127.0.0.1:8123/api/ -UseBasicParsing -TimeoutSec 5 | Out-Null; $true } catch { $_.Exception.Response -ne $null }
} until ($up -or (Get-Date) -gt $deadline)
if (-not $up) { throw "Home Assistant does not answer on VM-M's loopback" }
'tunnel up'
'@ | Write-Output

    Write-Information "VM-M: agent-ha-bridge, the shadow host ($hostCommit) and the driver ($driverCommit)" -InformationAction Continue
    $guest = $dotnetEnv + @'
$ahb = "$HOME\.agent-ha-bridge"
$version = Get-Content "$ahb\installer\VERSION"
$config = Get-Content "$ahb\config.json" -Raw | ConvertFrom-Json
# The disposable instance only, through the tunnel: never anything off this machine.
if (([uri]$config.homeAssistant.baseUrl).Host -notin '127.0.0.1', 'localhost') { throw "agent-ha-bridge is not pointed at the tunnel's 127.0.0.1" }
$beat = ((Get-Date) - (Get-Item "$ahb\runtime\agent-bridge-daemon.heartbeat").LastWriteTime).TotalSeconds
if ($beat -gt 120) { throw "agent-ha-bridge $version has not beaten for $([int]$beat) s" }
"agent-ha-bridge $version, beat $([int]$beat) s ago"
$h = @{ Authorization = 'Bearer ' + (Get-Content C:\rehearsal\ha.token -Raw).Trim() }
$slug = (($env:COMPUTERNAME.ToLowerInvariant() -replace '[^a-z0-9]+', '_').Trim('_'))
"online in Home Assistant: " + (Invoke-RestMethod "http://127.0.0.1:8123/api/states/binary_sensor.agent_bridge_${slug}_online" -Headers $h).state

if (-not ((& "$HOME\.dotnet\dotnet.exe" --list-sdks 2>$null) -match '^10\.')) {
    $inst = Join-Path $env:LOCALAPPDATA 'dotnet-install.ps1'
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $inst -UseBasicParsing
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $inst -Channel 10.0 -InstallDir "$HOME\.dotnet" -NoPath 2>&1 | Select-Object -Last 1
}

Get-Checkout '__SOAK__\agent-bridge' '__HOST__'
$installed = [bool](Get-ScheduledTask | Where-Object TaskName -like 'AgentBridgeShadowHost_shadow_*')
Set-Location '__SOAK__\agent-bridge'
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File build\shadow-week.ps1 $(if ($installed) { '-Update' } else { '-Install' }) 2>&1 | Select-Object -Last 3
if ($LASTEXITCODE) { throw 'shadow-week.ps1 failed' }

Get-Checkout '__SOAK__\driver' '__DRIVER__'
dotnet publish '__SOAK__\driver\tools\AgentBridge.Rehearsal' -c Release -r win-x64 --self-contained -o '__SOAK__\tool' --nologo -v q 2>&1 | Select-Object -Last 2
if ($LASTEXITCODE) { throw 'publishing the driver failed' }
"shadow host at __HOST__, driver at __DRIVER__"
'@
    $guest = $guest.Replace('__SOAK__', $soak).Replace('__HOST__', $hostCommit).Replace('__DRIVER__', $driverCommit)
    if (-not $KeepAppHost) {
        # The app's own host is read-only beside agent-ha-bridge too, but its Home Assistant
        # connector wakes whenever the daemon is down, as during the soak's daemon restart.
        $guest += @'

foreach ($p in Get-Process AgentBridge -ErrorAction SilentlyContinue) { Stop-Process -Id $p.Id -Force }
foreach ($t in Get-ScheduledTask | Where-Object TaskName -like 'AgentBridgeHost_*') { Stop-ScheduledTask -TaskName $t.TaskName }
foreach ($p in Get-Process agent-bridge-host, agent-bridge-hostw -ErrorAction SilentlyContinue) {
    if ($p.Path -like "$env:LOCALAPPDATA\AgentBridgeApp\*") { Stop-Process -Id $p.Id -Force }
}
'the app and its host stopped'
'@
    }
    Invoke-Guest M -AsDesktopUser -TimeoutMinutes 60 -Script $guest | Write-Output
    return
}

if ($Start) {
    $arguments = @('soak', 'run', '--token-file', 'C:\rehearsal\ha.token', '--state', "$soak\state", '--count', $Count, '--every', $EveryMinutes,
        '--restart-daemon-at', $RestartDaemonAt, '--restart-shadow-at', $RestartShadowAt)
    foreach ($k in $Models.Keys) { $arguments += @('--model', "$k=$($Models[$k])") }
    foreach ($k in $Efforts.Keys) { $arguments += @('--effort', "$k=$($Efforts[$k])") }
    $line = ($arguments | ForEach-Object { if ("$_" -match '\s') { "'$_'" } else { "$_" } }) -join ' '
    foreach ($role in 'W', 'M') { & $vmScript -Subscription $Subscription -AzureConfigDir $AzureConfigDir -Role $role -AutoShutdown Off 6>$null }
    Invoke-Guest M -Script @'
$user = 'rehearsal'
$dir = 'C:\ProgramData\AgentBridgeSoak'
$run = Join-Path $dir 'driver.ps1'
# Restarted after a failure, and at logon, so a reboot carries on from soak.json.
Set-Content -Path $run -Encoding utf8 -Value @"
New-Item -ItemType Directory -Path '__SOAK__\state' -Force | Out-Null
while (`$true) {
    & '__SOAK__\tool\agent-bridge-rehearsal.exe' __ARGS__ *>> '__SOAK__\state\driver.out'
    if (`$LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 60
}
"@
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$run`""
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = 'PT3M'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
if ('__FRESH__' -eq 'True') { Remove-Item '__SOAK__\state\soak.json' -ErrorAction SilentlyContinue }
Register-ScheduledTask -TaskName 'AgentBridgeSoakDriver' -Action $action -Principal $principal -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'AgentBridgeSoakDriver'
'driver started: ' + (Get-ScheduledTask -TaskName 'AgentBridgeSoakDriver').State
'@.Replace('__SOAK__', $soak).Replace('__ARGS__', $line).Replace('__FRESH__', [string][bool]$Fresh) | Write-Output
    return
}

if ($Stop) {
    Invoke-Guest M -Script @'
Stop-ScheduledTask -TaskName 'AgentBridgeSoakDriver' -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='agent-bridge-rehearsal.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
'driver stopped'
'@ | Write-Output
    return
}

if ($Collect) {
    if ($Collect -notmatch '^[A-Za-z0-9-]{8,64}$') { throw "$Collect is not a session id" }
    $zip = "$soak\collect\$Collect.zip"
    $made = Invoke-Guest M -AsDesktopUser -TimeoutMinutes 5 -Script @"
New-Item -ItemType Directory -Path '$soak\collect' -Force | Out-Null
& '$soak\tool\agent-bridge-rehearsal.exe' soak collect $Collect --out '$zip'
'size=' + (Get-Item '$zip').Length
"@
    Write-Information $made -InformationAction Continue
    if ($made -notmatch 'size=(\d+)') { throw "nothing collected for $Collect" }
    $size = [long]$Matches[1]
    # run-command hands back the last 4 KB of output, so the zip comes in pieces under that.
    $piece = 2800
    $bytes = [Collections.Generic.List[byte]]::new()
    for ($at = 0; $at -lt $size; $at += $piece) {
        $text = Invoke-Guest M -Script @"
`$s = [IO.File]::OpenRead('$zip'); try { `$null = `$s.Seek($at, 'Begin'); `$b = New-Object byte[] $piece; `$n = `$s.Read(`$b, 0, $piece); 'B64=' + [Convert]::ToBase64String(`$b, 0, `$n) } finally { `$s.Dispose() }
"@
        if ($text -notmatch 'B64=([A-Za-z0-9+/=]+)') { throw "piece at $at did not come back" }
        $bytes.AddRange([Convert]::FromBase64String($Matches[1]))
        Write-Progress -Activity "copying $Collect" -PercentComplete ([int](100 * $bytes.Count / $size))
    }
    if ($bytes.Count -ne $size) { throw "copied $($bytes.Count) of $size bytes" }
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $local = Join-Path $OutDir "$Collect.zip"
    [IO.File]::WriteAllBytes($local, $bytes.ToArray())
    Expand-Archive -Path $local -DestinationPath (Join-Path $OutDir $Collect) -Force
    Remove-Item $local
    Get-ChildItem (Join-Path $OutDir $Collect) -Recurse -File | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($OutDir.Length).TrimStart('\') } }, Length
    return
}

if ($Finish) {
    Invoke-Guest M -Script @'
Stop-ScheduledTask -TaskName 'AgentBridgeSoakDriver' -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='agent-bridge-rehearsal.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Unregister-ScheduledTask -TaskName 'AgentBridgeSoakDriver' -Confirm:$false -ErrorAction SilentlyContinue
foreach ($t in Get-ScheduledTask | Where-Object TaskName -like 'AgentBridgeHost_*') { Start-ScheduledTask -TaskName $t.TaskName }
'driver removed, the app host started again'
'@ | Write-Output
    foreach ($role in 'W', 'M') {
        & $vmScript -Subscription $Subscription -AzureConfigDir $AzureConfigDir -Role $role -AutoShutdown On 6>$null
        & $vmScript -Subscription $Subscription -AzureConfigDir $AzureConfigDir -Role $role -Deallocate
    }
    return
}

# Status
Invoke-Guest M -AsDesktopUser -TimeoutMinutes 5 -Script @"
'driver task: ' + (Get-ScheduledTask -TaskName 'AgentBridgeSoakDriver' -ErrorAction SilentlyContinue).State
& '$soak\tool\agent-bridge-rehearsal.exe' soak status --state '$soak\state' 2>&1
Get-Content '$soak\state\soak.log' -Tail 6 -ErrorAction SilentlyContinue
& "`$env:LOCALAPPDATA\AgentBridgeShadow\bin\agent-bridge-host.exe" shadow summary --gate --profile shadow 2>&1 | Select-Object -Last 24
"@ | Write-Output
