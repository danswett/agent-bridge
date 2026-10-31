#Requires -Version 7.3
<#
.SYNOPSIS
    Provisions VM-W for the migration rehearsal (docs/parity-checklist.md) as an Azure VM, or
    starts, deallocates or removes it.

.DESCRIPTION
    Idempotent: every step checks what is already there, so a rerun after a failure, or against a
    finished VM, converges instead of duplicating. What it builds, all in the resource group
    rg-agent-bridge-rehearsal and nothing outside it:

    * a Windows 11 VM (REHEARSAL-W) with a static Standard public IP and auto-shutdown;
    * an NSG that allows TCP 22 from -AllowedSshCidr only and denies everything else inbound.
      There is no RDP rule: the desktop is reached through Hyperloop, over an SSH tunnel;
    * OpenSSH Server, key-only, with the host key pinned from the VM's own output (never trusted
      on first use);
    * the standard user `rehearsal`, whose key may only forward to 127.0.0.1:8123 (Home
      Assistant), for VM-M's tunnel;
    * autologon of the admin (password in the LSA secret, not the registry) and Hyperloop's server
      in that interactive session, reachable only through a tunnel;
    * WSL2 with Ubuntu and Docker Engine inside it (not Docker Desktop, for licensing), started at
      logon; Git and PowerShell 7.

    It never starts Home Assistant: that is checklist step A-02.

    Everything inside the VM is done through `az vm run-command`, Azure's control plane, not SSH.
    A network that proxies outbound SSH (Entra Global Secure Access, for one) can leave the
    operator's own SSH arriving from outside -AllowedSshCidr even when its web traffic does not.
    SSH is needed here only to copy Hyperloop's binaries, and is skipped with a warning when it
    cannot connect.

    The admin password is the only secret. It is generated here, kept only in -SecretFile as a
    DPAPI-protected SecureString (this Windows user on this machine), and is never printed. It is
    handed to `az vm create` (which records arguments in its command log only as `{}`). For
    autologon it crosses to the VM encrypted to a one-time RSA key that the VM generated and
    deletes after use.

.PARAMETER Subscription
    The Azure subscription id. Always passed explicitly: the CLI's default may be another.

.PARAMETER AllowedSshCidr
    The only source range allowed to reach TCP 22. Ranges wider than /16 are refused.

.PARAMETER AdminKeyPath
    Private key (ed25519) for the admin account. Created, without a passphrase, if missing.

.PARAMETER TunnelPublicKeyPath
    Public key that VM-M uses as `rehearsal`. Optional; without it `rehearsal` has no key.

.PARAMETER SecretFile
    The DPAPI file holding the admin password. Created if missing.

.PARAMETER HyperloopDirectory
    A folder holding Hyperloop-server.exe and its sibling worker Hyperloop.exe. Optional; without it
    the binaries are not copied (the logon task is registered either way).

.PARAMETER AzureConfigDir
    AZURE_CONFIG_DIR for the az CLI, so a separate sign-in can be used.

.PARAMETER ExpectedAzureUser
    The az sign-in the rehearsal belongs to. Any other is refused unless -AllowOtherAzureUser.

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -AllowedSshCidr 203.0.113.0/24 `
        -TunnelPublicKeyPath .\rehearsal-m-tunnel.pub -HyperloopDirectory <Hyperloop.Tools folder>

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Deallocate

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Remove
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Subscription', Justification = 'Used by Invoke-Az and Test-AzResource')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPositionalParameters', '', Justification = 'Invoke-Az passes az arguments through verbatim')]
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Provision')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $Subscription,

    [Parameter(Mandatory, ParameterSetName = 'Provision')]
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$')]
    [string] $AllowedSshCidr,

    [Parameter(ParameterSetName = 'Provision')]
    [Parameter(ParameterSetName = 'Start')]
    [string] $AdminKeyPath = (Join-Path $HOME '.ssh/agentbridge-rehearsal'),

    [Parameter(ParameterSetName = 'Provision')]
    [string] $TunnelPublicKeyPath,

    [Parameter(ParameterSetName = 'Provision')]
    [string] $SecretFile = (Join-Path $HOME '.agentbridge-rehearsal/rehearsal-w-admin.dpapi'),

    [Parameter(ParameterSetName = 'Provision')]
    [string] $HyperloopDirectory,

    [string] $AzureConfigDir = (Join-Path $HOME '.azure-agentbridge'),

    [string] $ExpectedAzureUser = 'danswett@outlook.com',

    [switch] $AllowOtherAzureUser,

    [Parameter(Mandatory, ParameterSetName = 'Deallocate')]
    [switch] $Deallocate,

    [Parameter(Mandatory, ParameterSetName = 'Start')]
    [switch] $Start,

    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Fixed rather than parameters: -Remove deletes this group and nothing else, whatever it is given.
$ResourceGroup = 'rg-agent-bridge-rehearsal'
$Location = 'westus2'
$VmName = 'rehearsal-w'
$ComputerName = 'REHEARSAL-W'
$AdminUser = 'rehearsaladmin'
$TunnelUser = 'rehearsal'
$Image = 'MicrosoftWindowsDesktop:windows-11:win11-24h2-pro:latest'
# D4s_v5 runs nested virtualization (WSL2 needs it); D4as_v5 is the fallback. Standard security
# type: nothing here needs Trusted Launch, and it is what the CLI chose for this image.
$Sizes = @('Standard_D4s_v5', 'Standard_D4as_v5')
$SecurityType = 'Standard'
$AutoShutdownUtc = '0800'
$HyperloopPort = 4850
$Tags = @('purpose=rehearsal-39', 'owner=danswett', 'lifetime=delete-after-rehearsal')
$KnownHostsPath = Join-Path $HOME '.ssh/known_hosts_agentbridge-rehearsal'
# Relative paths resolve against the current location, so Split-Path always finds a parent.
foreach ($name in 'SecretFile', 'AdminKeyPath', 'TunnelPublicKeyPath', 'HyperloopDirectory', 'AzureConfigDir') {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value) { Set-Variable -Name $name -Value $PSCmdlet.GetUnresolvedProviderPathFromPSPath($value) }
}
$StateDir = Split-Path $SecretFile

$env:AZURE_CONFIG_DIR = $AzureConfigDir

function Write-Step([string] $Message) {
    Write-Information "[rehearsal-w] $Message" -InformationAction Continue
}

function Invoke-Az {
    # A simple function on purpose: as an advanced one, az's -g and -o would bind to PowerShell's
    # own common parameters. az is a batch file on Windows, so nothing passed here may contain
    # cmd metacharacters; the generated password avoids them for that reason.
    $out = & az @args --subscription $Subscription --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "az $($args[0]) $($args[1]) failed: $(($out | Out-String).Trim())"
    }
    return ($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n"
}

function Test-AzResource([string[]] $ShowArgs) {
    $out = & az @ShowArgs --subscription $Subscription --only-show-errors -o none 2>&1
    if ($LASTEXITCODE -eq 0) { return $true }
    # Only a definite "not found" means absent. A timeout, throttling or denied read must not
    # look like absence: provisioning would then skip the tag check and retag a foreign group.
    $text = ($out | Out-String)
    if ($text -match 'ResourceGroupNotFound|ResourceNotFound|NotFound|could not be found|was not found') { return $false }
    throw "az $($ShowArgs -join ' ') failed: $($text.Trim())"
}

function Get-VmPublicIp {
    return (Invoke-Az network public-ip show -g $ResourceGroup -n "$VmName-pip" --query ipAddress -o tsv).Trim()
}

function Get-TunnelCommand([string] $Ip) {
    $ssh = "ssh -i `"$AdminKeyPath`" -o IdentitiesOnly=yes -o UserKnownHostsFile=`"$KnownHostsPath`" -o StrictHostKeyChecking=yes"
    $hostKey = if (Test-Path $KnownHostsPath) { (Get-Content $KnownHostsPath -Raw).Trim() } else { '<run a provision first>' }
    return [pscustomobject]@{
        HyperloopTunnel = "$ssh -N -L 4851:127.0.0.1:${HyperloopPort} $AdminUser@$Ip"
        HyperloopServer = 'hyperloop servers add rehearsal-w --scheme tcp --authority 127.0.0.1:4851'
        AdminShell      = "$ssh $AdminUser@$Ip"
        # VM-M must not trust on first use: append KnownHostsLine to its ~/.ssh/known_hosts first.
        KnownHostsLine  = $hostKey
        VmMTunnel       = "ssh -i <VM-M key> -o StrictHostKeyChecking=yes -N -L 8123:127.0.0.1:8123 $TunnelUser@$Ip"
    }
}

function Get-RandomAdminPassword {
    # Letters, digits and _-.@#+ only: it is passed through az.cmd, where % ^ & | < > ! and quotes
    # would be reinterpreted. 32 characters keeps it strong without them.
    $letters = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $special = [char[]]'_-.@#+'
    do {
        $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $chars = foreach ($b in $bytes) { if ($b % 5 -eq 0) { $special[$b % $special.Length] } else { $letters[$b % $letters.Length] } }
        $plain = -join $chars
    } until ($plain -cmatch '[A-Z]' -and $plain -cmatch '[a-z]' -and $plain -match '\d' -and $plain -match '[_\-.@#+]' -and $plain -match '^[A-Za-z]')
    $plain = $null
    $secure = [securestring]::new()
    foreach ($ch in $chars) { $secure.AppendChar($ch) }
    $secure.MakeReadOnly()
    return $secure
}

function Get-AdminPassword {
    if (Test-Path $SecretFile) {
        return Get-Content -Path $SecretFile | ConvertTo-SecureString
    }
    $secure = Get-RandomAdminPassword
    $secure | ConvertFrom-SecureString | Set-Content -Path $SecretFile -Encoding ascii
    Write-Step "admin password generated and stored DPAPI-protected at $SecretFile"
    return $secure
}

function ConvertTo-PlainText([securestring] $Secure) {
    return [System.Net.NetworkCredential]::new('', $Secure).Password
}

function Initialize-AdminKey {
    if (Test-Path $AdminKeyPath) { return }
    $dir = Split-Path $AdminKeyPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    # PowerShell 7.3+ passes '' to a native command as an empty argument; '""' would become a
    # two-character passphrase.
    & ssh-keygen -t ed25519 -N '' -C 'agentbridge-rehearsal-admin' -f $AdminKeyPath -q
    if ($LASTEXITCODE -ne 0) { throw 'ssh-keygen failed' }
    Write-Step "admin key created at $AdminKeyPath"
}

function Invoke-VmRunCommand {
    <#
    Runs a script on the VM as SYSTEM through Azure's control plane. Only non-secret text may go
    in: run-command keeps the script in the VM's extension settings.
    #>
    param([string] $Script)
    # Every staging folder lives under Program Files, where only administrators can create, and
    # is re-ACLed regardless: one in ProgramData could be pre-created, and so owned, by
    # `rehearsal`, letting it swap a script or installer before SYSTEM or the admin runs it.
    $protected = @'
function Get-ProtectedDirectory([string] $Leaf) {
    $root = Join-Path $env:ProgramFiles 'AgentBridgeRehearsal'
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    & icacls.exe $root /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $root failed" }
    $dir = Join-Path $root $Leaf
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}
'@
    $body = "`$ErrorActionPreference = 'Stop'`r`n`$ProgressPreference = 'SilentlyContinue'`r`n" +
        "try {`r`n$protected`r`n$Script`r`n'GUEST-OK'`r`n} catch { `$m = (`$_ | Out-String); 'GUEST-ERROR: ' + `$m.Substring(0, [Math]::Min(2000, `$m.Length)); 'GUEST-FAILED' }"
    if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
    $file = Join-Path $StateDir "guest-$([guid]::NewGuid().ToString('n')).ps1"
    Set-Content -Path $file -Value $body -Encoding utf8
    try {
        $json = Invoke-Az vm run-command invoke -g $ResourceGroup -n $VmName --command-id RunPowerShellScript `
            --scripts "@$file" -o json
    } finally {
        Remove-Item $file -ErrorAction SilentlyContinue
    }
    $value = @(($json | ConvertFrom-Json).value)
    $stdout = ($value | Where-Object { $_.code -like '*StdOut*' } | ForEach-Object message) -join "`n"
    $stderr = ($value | Where-Object { $_.code -like '*StdErr*' } | ForEach-Object message) -join "`n"
    # run-command returns only the last 4 KB of output, so success is a marker the script prints
    # last; a failure prints its own marker after a bounded message. Neither can be cut off.
    if ($stdout -notmatch 'GUEST-OK\s*$') { throw "guest script failed: $stdout`n$stderr" }
    $stdout = $stdout -replace 'GUEST-OK\s*$', ''
    if ($stderr.Trim()) { Write-Verbose "guest stderr: $stderr" }
    return $stdout
}

function Wait-AdminSession([int] $TimeoutSeconds = 900) {
    # After a restart, autologon signs the admin in at the console; the logon tasks start then.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $q = Invoke-VmRunCommand -Script 'quser 2>&1 | Out-String'
            if ($q -match "$AdminUser\s+console\s+\d+\s+Active") { return }
        } catch {
            Write-Verbose "waiting for the VM: $_"
        }
        Start-Sleep -Seconds 15
    }
    throw "the admin was not signed in at the console within $TimeoutSeconds s (autologon?)"
}

function Invoke-VmAsAdmin {
    <#
    Runs a script as the admin, elevated, inside the autologon desktop session: a one-shot
    scheduled task started from run-command, whose output comes back through a log file. WSL
    distributions are per user, so they cannot be installed as SYSTEM. The job runs with
    ErrorActionPreference Continue because Windows PowerShell turns a native command's redirected
    stderr into a terminating error under Stop; the scripts check exit codes and throw themselves.
    #>
    param([string] $Script, [int] $TimeoutMinutes = 30)
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Script))
    $runner = @'
$id = [guid]::NewGuid().ToString('n')
$dir = Get-ProtectedDirectory 'jobs'
$job = Join-Path $dir "$id.ps1"; $log = Join-Path $dir "$id.log"
$inner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__B64__'))
$wrapped = "`$ErrorActionPreference = 'Continue'`r`n`$ProgressPreference = 'SilentlyContinue'`r`ntry {`r`n& {`r`n$inner`r`n} *>&1 | Out-File -FilePath '$log' -Encoding utf8 -Append`r`nAdd-Content -Path '$log' -Value 'JOB-DONE'`r`n} catch { Add-Content -Path '$log' -Value ('JOB-ERROR: ' + (`$_ | Out-String)) }"
Set-Content -Path $job -Value $wrapped -Encoding utf8
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$job`""
$principal = New-ScheduledTaskPrincipal -UserId '__ADMIN_USER__' -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(__TIMEOUT__))
Register-ScheduledTask -TaskName "Rehearsal job $id" -Action $action -Principal $principal -Settings $settings -Force | Out-Null
try {
    Start-ScheduledTask -TaskName "Rehearsal job $id"
    $deadline = (Get-Date).AddMinutes(__TIMEOUT__)
    $text = ''
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (Test-Path $log) {
            $text = Get-Content $log -Raw -ErrorAction SilentlyContinue
            if ($text -match 'JOB-DONE|JOB-ERROR') { break }
        }
    }
    if ($text -notmatch 'JOB-DONE|JOB-ERROR') { throw "job did not finish in __TIMEOUT__ minutes: $text" }
    if ($text -match 'JOB-ERROR') { throw $text }
    $text
} finally {
    Unregister-ScheduledTask -TaskName "Rehearsal job $id" -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $job, $log -ErrorAction SilentlyContinue
}
'@
    $runner = $runner.Replace('__B64__', $b64).Replace('__ADMIN_USER__', $AdminUser).Replace('__TIMEOUT__', "$TimeoutMinutes")
    return Invoke-VmRunCommand -Script $runner
}

function Invoke-VmRestart {
    Write-Step 'restarting the VM'
    Invoke-Az vm restart -g $ResourceGroup -n $VmName -o none | Out-Null
    Wait-AdminSession
}

function Get-SshArgument([string] $Ip) {
    return @('-i', $AdminKeyPath, '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
        '-o', "UserKnownHostsFile=$KnownHostsPath", '-o', 'StrictHostKeyChecking=yes',
        '-o', 'ConnectTimeout=15')
}

function Test-Ssh([string] $Ip) {
    & ssh @(Get-SshArgument $Ip) "$AdminUser@$Ip" 'exit 0' 2>$null
    return $LASTEXITCODE -eq 0
}

# ---------------------------------------------------------------------------------------------
# Guest scripts. Placeholders are replaced before sending; keys travel as base64 so no key text
# meets a quoting layer.
# ---------------------------------------------------------------------------------------------
$GuestBootstrap = @'
$adminUser = '__ADMIN_USER__'
$tunnelUser = '__TUNNEL_USER__'
$adminKey = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String('__ADMIN_KEY_B64__')).Trim()
$tunnelKey = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String('__TUNNEL_KEY_B64__')).Trim()
$sshDir = Join-Path $env:ProgramData 'ssh'

# Before any SSH account exists. C:\ lets every signed-in user create and modify folders, and
# Hyperloop's binaries in C:\Tools run as the admin, so `rehearsal` must not be able to create,
# own or write anything there; the admin itself may, for server updates. Ownership and the
# children's ACLs are reset too, in case the folder was made by someone else first.
New-Item -ItemType Directory -Path 'C:\Tools' -Force | Out-Null
& icacls.exe 'C:\Tools' /setowner '*S-1-5-32-544' /T /C | Out-Null
& icacls.exe 'C:\Tools' /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' `
    "${adminUser}:(OI)(CI)M" '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'icacls C:\Tools failed' }
if (Get-ChildItem 'C:\Tools' -Force) { & icacls.exe 'C:\Tools\*' /reset /T /C | Out-Null }

# Windows Update supplies the capability; on a fresh VM this can take most of an hour.
$cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null }
# The first start generates the host keys.
Set-Service sshd -StartupType Automatic
Start-Service sshd
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -PropertyType String -Force `
    -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" | Out-Null

if (-not (Get-LocalUser -Name $tunnelUser -ErrorAction SilentlyContinue)) {
    # A random password nobody keeps: this account only ever signs in with its key.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 24; $rng.GetBytes($bytes)
    $pw = ConvertTo-SecureString ('Aa1-' + [Convert]::ToBase64String($bytes)) -AsPlainText -Force
    New-LocalUser -Name $tunnelUser -Password $pw -PasswordNeverExpires -AccountNeverExpires `
        -Description 'Rehearsal: VM-M tunnel to Home Assistant' | Out-Null
    Add-LocalGroupMember -Group 'Users' -Member $tunnelUser -ErrorAction SilentlyContinue
    $pw = $null
}

function Set-KeyFile([string] $Path, [string] $Content, [string] $ReaderSid) {
    Set-Content -Path $Path -Value $Content -Encoding ascii
    $grants = @('/grant', '*S-1-5-32-544:F', '/grant', '*S-1-5-18:F')
    if ($ReaderSid) { $grants += @('/grant', "*${ReaderSid}:R") }
    & icacls.exe $Path /inheritance:r @grants | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $Path failed" }
}
Set-KeyFile (Join-Path $sshDir 'administrators_authorized_keys') $adminKey $null
# Written even when empty, so dropping -TunnelPublicKeyPath on a rerun revokes the old key.
$sid = (Get-LocalUser -Name $tunnelUser).SID.Value
Set-KeyFile (Join-Path $sshDir "${tunnelUser}_authorized_keys") $tunnelKey $sid

# The only folder `rehearsal` sees (sshd chroots its SFTP here), so it cannot plant or read files
# anywhere else on C:\. Files it exchanges, such as A-04's token, go through here.
$drop = 'C:\RehearsalDrop'
New-Item -ItemType Directory -Path $drop -Force | Out-Null
& icacls.exe $drop /setowner '*S-1-5-32-544' /T /C | Out-Null
& icacls.exe $drop /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' "*${sid}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls $drop failed" }

$config = @"
# Written by build/rehearsal-azure-vmw.ps1. Key-only; the rehearsal user may only forward to HA and use SFTP.
Port 22
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitEmptyPasswords no
AllowAgentForwarding no
GatewayPorts no
PermitTunnel no
AllowUsers $adminUser $tunnelUser
AuthorizedKeysFile .ssh/authorized_keys
Subsystem sftp sftp-server.exe

Match User $tunnelUser
    AuthorizedKeysFile __PROGRAMDATA__/ssh/${tunnelUser}_authorized_keys
    AllowTcpForwarding local
    PermitOpen 127.0.0.1:8123
    # No shell: a process it could run would reach Hyperloop (no PIN) on loopback, where
    # PermitOpen does not apply. 'ssh -N -L' and scp (SFTP, inside the drop folder) still work.
    ForceCommand internal-sftp
    ChrootDirectory C:\RehearsalDrop

Match Group administrators
    AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys
"@
$configPath = Join-Path $sshDir 'sshd_config'
$candidate = Join-Path $sshDir 'sshd_config.candidate'
Set-Content -Path $candidate -Value $config -Encoding ascii
& "$env:SystemRoot\System32\OpenSSH\sshd.exe" -t -f $candidate
if ($LASTEXITCODE -ne 0) { throw 'the new sshd_config does not parse' }
Move-Item -Path $candidate -Destination $configPath -Force
Restart-Service sshd

# The capability's own rule covers only the Private profile, and an Azure NIC is Public.
if (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue) {
    Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -Profile Any -Enabled True
} else {
    New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True `
        -Profile Any -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
}
# Belt and braces behind the NSG: Hyperloop, Home Assistant and Mosquitto are for tunnels only.
# Loopback is exempt from Windows Firewall, so the tunnels still reach them.
foreach ($p in @(@{ n = 'Hyperloop'; port = __HL_PORT__ }, @{ n = 'HomeAssistant'; port = 8123 }, @{ n = 'Mosquitto'; port = 1883 })) {
    $name = "Rehearsal-Block-$($p.n)-In"
    if (-not (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name $name -DisplayName "Rehearsal: block $($p.n) inbound" -Direction Inbound `
            -Protocol TCP -LocalPort $p.port -Action Block | Out-Null
    }
}

# An unattended desktop that stays usable for Hyperloop: no sleep, no display timeout, no
# first-sign-in privacy pages, no Edge first-run.
powercfg /change monitor-timeout-ac 0; powercfg /change standby-timeout-ac 0
New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE' -Name DisablePrivacyExperience -Value 1 -Type DWord
New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' -Name HideFirstRunExperience -Value 1 -Type DWord

$hostKey = (Get-Content (Join-Path $sshDir 'ssh_host_ed25519_key.pub')).Trim()
"HOSTKEY=$hostKey"
'@

# Autologon, step 1: the VM makes a one-time RSA key in the machine key store and hands back only
# its public half.
$GuestEnvelopeKey = @'
$p = New-Object System.Security.Cryptography.CspParameters
$p.KeyContainerName = 'rehearsal-autologon'
$p.Flags = [System.Security.Cryptography.CspProviderFlags]::UseMachineKeyStore
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072, $p)
'PUBKEY=' + $rsa.ToXmlString($false)
'@

# Autologon, step 2: the password, encrypted to that key, goes to the LSA secret DefaultPassword
# (as Sysinternals Autologon does), not the plain-text Winlogon value; then the key is deleted.
$GuestAutologon = @'
$p = New-Object System.Security.Cryptography.CspParameters
$p.KeyContainerName = 'rehearsal-autologon'
$p.Flags = [System.Security.Cryptography.CspProviderFlags]::UseMachineKeyStore
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider($p)
try {
    $secret = [Text.Encoding]::UTF8.GetString($rsa.Decrypt([Convert]::FromBase64String('__CIPHER_B64__'), $true))
} finally {
    $rsa.PersistKeyInCsp = $false
    $rsa.Clear()
}
Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class RehearsalLsa {
    [StructLayout(LayoutKind.Sequential)] struct UStr { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct ObjAttr { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref ObjAttr attributes, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr handle, ref UStr key, ref UStr data);
    [DllImport("advapi32.dll")] static extern uint LsaNtStatusToWinError(uint status);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
    static UStr Make(string s) { UStr u = new UStr(); u.Buffer = Marshal.StringToHGlobalUni(s); u.Length = (ushort)(s.Length * 2); u.MaximumLength = (ushort)(s.Length * 2 + 2); return u; }
    public static void Store(string key, string value) {
        ObjAttr oa = new ObjAttr(); oa.Length = Marshal.SizeOf(typeof(ObjAttr));
        IntPtr h;
        uint st = LsaOpenPolicy(IntPtr.Zero, ref oa, 0x000F0FFF, out h);
        if (st != 0) throw new Win32Exception((int)LsaNtStatusToWinError(st));
        UStr k = Make(key); UStr d = Make(value);
        try { st = LsaStorePrivateData(h, ref k, ref d); if (st != 0) throw new Win32Exception((int)LsaNtStatusToWinError(st)); }
        finally { LsaClose(h); Marshal.ZeroFreeGlobalAllocUnicode(d.Buffer); Marshal.FreeHGlobal(k.Buffer); }
    }
}
"@
[RehearsalLsa]::Store('DefaultPassword', $secret)
$secret = $null
$wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Set-ItemProperty -Path $wl -Name AutoAdminLogon -Value '1'
Set-ItemProperty -Path $wl -Name DefaultUserName -Value '__ADMIN_USER__'
Set-ItemProperty -Path $wl -Name DefaultDomainName -Value $env:COMPUTERNAME
Remove-ItemProperty -Path $wl -Name DefaultPassword -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $wl -Name AutoLogonCount -ErrorAction SilentlyContinue
'autologon=configured'
'@

# Hyperloop's server in the admin's desktop session, visible in a console window. tcp mode with no
# PIN: the NSG and Windows Firewall keep its port to loopback, i.e. to the SSH tunnel. --no-setup
# so it changes nothing on the machine (no firewall openings) by itself.
$GuestHyperloopTask = @'
# Every logon checks both binaries are validly signed by Microsoft before the server starts.
$launcher = @"
foreach (`$f in 'C:\Tools\hyperloop.exe', 'C:\Tools\hyperloop-server.exe') {
    `$s = Get-AuthenticodeSignature `$f
    if (`$s.Status -ne 'Valid' -or `$s.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') { exit 1 }
}
Start-Process "`$env:SystemRoot\System32\cmd.exe" -ArgumentList '/k ""C:\Tools\hyperloop-server.exe" --mode tcp:__HL_PORT__ --no-auth --no-setup"'
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launcher))
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand $encoded"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User '__ADMIN_USER__'
# Started at the instant of logon, the server accepted TCP but never completed a TLS handshake;
# a short delay lets the session settle first.
$trigger.Delay = 'PT45S'
$principal = New-ScheduledTaskPrincipal -UserId '__ADMIN_USER__' -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'Rehearsal Hyperloop Server' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
"hyperloop-task=registered; binaries=$(Test-Path C:\Tools\hyperloop-server.exe)"
'@

# Hyperloop answering TLS on loopback, in session 1. A server started by the very first logon
# once sat accepting TCP without ever completing a TLS handshake; restarting its task cleared it,
# so the check does that once before failing.
$GuestHyperloopCheck = @'
# A function, so the early return does not skip the wrapper's completion marker.
function Test-RehearsalHyperloop {
if (-not (Test-Path 'C:\Tools\hyperloop.exe')) { 'hyperloop=absent'; return }
# Only Microsoft-signed binaries are started as the admin.
foreach ($exe in 'C:\Tools\hyperloop.exe', 'C:\Tools\hyperloop-server.exe') {
    $sig = Get-AuthenticodeSignature $exe
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
        throw "$exe is not a valid Microsoft-signed binary ($($sig.Status)); not starting it"
    }
}
function Invoke-Bounded([string[]] $Arguments) {
    $o = Join-Path (Get-ProtectedDirectory 'jobs') 'hl-check-out.txt'; $e = Join-Path (Get-ProtectedDirectory 'jobs') 'hl-check-err.txt'
    $p = Start-Process 'C:\Tools\hyperloop.exe' -ArgumentList $Arguments -RedirectStandardOutput $o `
        -RedirectStandardError $e -RedirectStandardInput "$env:SystemRoot\win.ini" -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(60000)) { $p.Kill(); $t = 'TIMEOUT' } else { $t = (Get-Content $o -Raw) + (Get-Content $e -Raw) }
    Remove-Item $o, $e -ErrorAction SilentlyContinue
    return $t
}
$target = 'tcp://127.0.0.1:__HL_PORT__'
$diag = Invoke-Bounded @('diagnose', $target)
if ($diag -notmatch '"tls":\{"success":true') {
    'hyperloop=restarting'
    Get-Process hyperloop, hyperloop-server -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'cmd.exe' -and $_.CommandLine -like '*hyperloop-server*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-ScheduledTask -TaskName 'Rehearsal Hyperloop Server'
    Start-Sleep -Seconds 20
}
$status = Invoke-Bounded @('status', $target)
if ($status -notmatch '"interactive":true') { throw "Hyperloop is not serving the interactive session: $status" }
'hyperloop=interactive'
}
Test-RehearsalHyperloop
'@

# Git and PowerShell 7 from their GitHub releases (winget is not available to SYSTEM). Each
# installer's Authenticode signature is checked before it runs.
$GuestTools = @'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dl = Get-ProtectedDirectory 'downloads'
function Get-Release([string] $Repo, [string] $Pattern) {
    $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'rehearsal-azure-vmw' }
    $asset = $rel.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
    if (-not $asset) { throw "no asset matching $Pattern in $Repo" }
    $path = Join-Path $dl $asset.name
    if (-not (Test-Path $path)) {
        # Downloaded beside, then renamed, so an interrupted run never leaves a partial file
        # that the next run would mistake for a cached installer.
        Invoke-WebRequest $asset.browser_download_url -OutFile "$path.partial" -UseBasicParsing
        Move-Item "$path.partial" $path -Force
    }
    $sig = Get-AuthenticodeSignature $path
    if ($sig.Status -ne 'Valid') {
        Remove-Item $path -Force
        throw "$($asset.name): signature $($sig.Status); deleted so a rerun downloads it again"
    }
    return $path
}
if (-not (Test-Path "$env:ProgramFiles\PowerShell\7\pwsh.exe")) {
    $msi = Get-Release 'PowerShell/PowerShell' '^PowerShell-[\d.]+-win-x64\.msi$'
    $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart', 'ADD_PATH=1') -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "PowerShell MSI exit $($p.ExitCode)" }
}
if (-not (Test-Path "$env:ProgramFiles\Git\cmd\git.exe")) {
    $exe = Get-Release 'git-for-windows/git' '^Git-[\d.]+(\.windows\.\d+)?-64-bit\.exe$'
    $p = Start-Process $exe -ArgumentList '/VERYSILENT /NORESTART /NOCANCEL /SP- /SUPPRESSMSGBOXES' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Git installer exit $($p.ExitCode)" }
}
Remove-Item $dl -Recurse -Force -ErrorAction SilentlyContinue
"pwsh=$(& "$env:ProgramFiles\PowerShell\7\pwsh.exe" -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"
"git=$(& "$env:ProgramFiles\Git\cmd\git.exe" --version)"
"edge=$(Test-Path "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe")"
'@

# WSL itself, as the admin. `--no-distribution` installs the WSL package and Virtual Machine
# Platform; the feature needs a restart before a distribution can run.
$GuestWslInstall = @'
$env:WSL_UTF8 = '1'
$feature = (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State
$wslOk = $false
try { & wsl.exe --version *> $null; $wslOk = ($LASTEXITCODE -eq 0) } catch { $wslOk = $false }
if ("$feature" -ne 'Enabled' -or -not $wslOk) {
    & wsl.exe --install --no-distribution 2>&1 | Out-String
    "wsl=installed-restart-needed"
} else {
    "wsl=ready"
}
'@

$GuestUbuntuDocker = @'
$env:WSL_UTF8 = '1'
$distros = @((& wsl.exe --list --quiet) -replace "`0", '' | Where-Object { $_ })
if ($distros -notcontains 'Ubuntu') {
    & wsl.exe --install -d Ubuntu --no-launch 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "wsl --install Ubuntu exit $LASTEXITCODE" }
}
$bash = @"
set -e
export DEBIAN_FRONTEND=noninteractive
grep -q '^systemd=true' /etc/wsl.conf 2>/dev/null || printf '[boot]\nsystemd=true\n' >> /etc/wsl.conf
# Finish whatever an interrupted run left: apt refuses every install while dpkg is mid-transaction.
dpkg --configure -a
apt-get -f install -y -qq >/dev/null
if [ ! -s /etc/apt/sources.list.d/docker.list ]; then
  apt-get update -qq
  apt-get install -y -qq ca-certificates curl >/dev/null
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=`$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu `$(. /etc/os-release && echo `$VERSION_CODENAME) stable" > /etc/apt/sources.list.d/docker.list
fi
# Every run, not only when dockerd is missing: apt-get install is idempotent, and it completes
# a set left half-installed by an interrupted run (Compose included).
apt-get update -qq
apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
systemctl enable docker >/dev/null 2>&1 || true
echo docker=`$(docker --version)
echo compose=`$(docker compose version --short)
"@
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bash.Replace("`r`n", "`n")))
& wsl.exe -d Ubuntu -u root -- bash -c "echo $b64 | base64 -d | bash" 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "docker install exit $LASTEXITCODE" }

# Keep the distribution (and so dockerd) running for the whole sign-in: WSL stops an idle
# instance once no wsl.exe client is attached, so the logon task stays attached, headless.
$cfg = Join-Path $env:USERPROFILE '.wslconfig'
if (-not (Test-Path $cfg)) { Set-Content -Path $cfg -Value "[wsl2]`r`nvmIdleTimeout=-1`r`n" -Encoding ascii }
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument '--headless wsl.exe -d Ubuntu -u root -- sh -c "systemctl start docker 2>/dev/null || service docker start; exec sleep infinity"'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User '__ADMIN_USER__'
$principal = New-ScheduledTaskPrincipal -UserId '__ADMIN_USER__' -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'Rehearsal WSL Docker' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
'docker-task=registered'
'@

# hello-world, and a container port published on WSL's 127.0.0.1 answering at Windows' 127.0.0.1
# (WSL's default localhost forwarding), which is how Home Assistant will be reached.
$GuestDockerCheck = @'
$env:WSL_UTF8 = '1'
# Read before anything below starts WSL itself: the logon task is what should have started it.
"docker-task=$((Get-ScheduledTask -TaskName 'Rehearsal WSL Docker').State)"
"dockerd=$((& wsl.exe -d Ubuntu -u root -- systemctl is-active docker) -join '')"
"compose=$((& wsl.exe -d Ubuntu -u root -- docker compose version --short) -join '')"
$hello = & wsl.exe -d Ubuntu -u root -- docker run --rm hello-world 2>&1 | Out-String
"hello-world=$($hello -match 'Hello from Docker!')"
& wsl.exe -d Ubuntu -u root -- docker rm -f rehearsal-probe 2>&1 | Out-Null
& wsl.exe -d Ubuntu -u root -- docker run -d --name rehearsal-probe -p 127.0.0.1:18123:80 nginx:alpine 2>&1 | Out-Null
$code = 0
for ($i = 0; $i -lt 30 -and $code -ne 200; $i++) {
    Start-Sleep -Seconds 2
    try { $code = (Invoke-WebRequest http://127.0.0.1:18123/ -UseBasicParsing -TimeoutSec 5).StatusCode } catch { $code = 0 }
}
& wsl.exe -d Ubuntu -u root -- docker rm -f rehearsal-probe 2>&1 | Out-Null
"windows-loopback-18123=$code"
'@

function Expand-Guest([string] $Script) {
    return $Script.Replace('__ADMIN_USER__', $AdminUser).Replace('__TUNNEL_USER__', $TunnelUser).Replace('__HL_PORT__', "$HyperloopPort")
}

# ---------------------------------------------------------------------------------------------
# Identity, then Start / Deallocate / Remove
# ---------------------------------------------------------------------------------------------
# Before anything that writes: the az profile in -AzureConfigDir must be the expected sign-in.
$signedIn = (Invoke-Az account show --query user.name -o tsv).Trim()
if ($signedIn -ne $ExpectedAzureUser) {
    if (-not $AllowOtherAzureUser) {
        throw "az is signed in as '$signedIn', not '$ExpectedAzureUser' (AZURE_CONFIG_DIR=$AzureConfigDir). Sign in with that profile, or pass -AllowOtherAzureUser."
    }
    Write-Warning "continuing as '$signedIn' because -AllowOtherAzureUser was given"
}

# Every operation on an existing group first proves it is the rehearsal's: the names are fixed, so
# an unrelated group of the same name must never be started, stopped, adopted or deleted.
$groupExists = Test-AzResource @('group', 'show', '-n', $ResourceGroup)
if ($groupExists) {
    $purpose = "$(Invoke-Az group show -n $ResourceGroup --query 'tags.purpose' -o tsv)".Trim()
    if ($purpose -ne 'rehearsal-39') { throw "$ResourceGroup is not tagged purpose=rehearsal-39; refusing to touch it" }
}

if ($Remove) {
    if (-not $groupExists) {
        Write-Step "$ResourceGroup does not exist"
        return
    }
    if ($PSCmdlet.ShouldProcess($ResourceGroup, 'Delete the resource group and everything in it') -and
        $PSCmdlet.ShouldContinue("Delete $ResourceGroup and every resource in it (VM, disk, IP, NSG, network)?", 'Remove VM-W')) {
        Invoke-Az group delete -n $ResourceGroup --yes -o none | Out-Null
        Write-Step "$ResourceGroup deleted. The DPAPI password file and keys on this machine were left in place."
    }
    return
}

if (($Deallocate -or $Start) -and -not $groupExists) { throw "$ResourceGroup does not exist; provision first" }

if ($Deallocate) {
    if ($PSCmdlet.ShouldProcess($VmName, 'Deallocate')) {
        Invoke-Az vm deallocate -g $ResourceGroup -n $VmName -o none | Out-Null
        Write-Step "$VmName deallocated: compute is no longer billed; the disk and static IP still are."
    }
    return
}

if ($Start) {
    if ($PSCmdlet.ShouldProcess($VmName, 'Start')) {
        Invoke-Az vm start -g $ResourceGroup -n $VmName -o none | Out-Null
        Wait-AdminSession
        Invoke-VmRunCommand -Script (Expand-Guest $GuestHyperloopCheck) | Write-Verbose
        $ip = Get-VmPublicIp
        Write-Step "$VmName is running at $ip with the admin signed in"
        Get-TunnelCommand $ip
    }
    return
}

# ---------------------------------------------------------------------------------------------
# Provision
# ---------------------------------------------------------------------------------------------
# The password file relies on DPAPI, which ConvertFrom-SecureString uses only on Windows.
if (-not $IsWindows) { throw 'provisioning stores the admin password with DPAPI, so it runs only on Windows' }
$prefix = [int]($AllowedSshCidr.Split('/')[1])
if ($prefix -lt 16) { throw "-AllowedSshCidr $AllowedSshCidr is wider than /16; refusing to open SSH that far" }
if ($TunnelPublicKeyPath -and -not (Test-Path $TunnelPublicKeyPath)) { throw "$TunnelPublicKeyPath not found" }
if ($HyperloopDirectory -and -not (Test-Path (Join-Path $HyperloopDirectory 'Hyperloop-server.exe'))) {
    throw "Hyperloop-server.exe not found in $HyperloopDirectory"
}
if (-not $PSCmdlet.ShouldProcess("$ResourceGroup/$VmName", 'Provision VM-W')) { return }
if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }

Initialize-AdminKey
$adminPub = (Get-Content "$AdminKeyPath.pub" -Raw).Trim()
$tunnelPub = if ($TunnelPublicKeyPath) { (Get-Content $TunnelPublicKeyPath -Raw).Trim() } else { '' }

Write-Step "resource group $ResourceGroup"
# Safe only because the tag was checked above: `group create` would retag a foreign group.
Invoke-Az group create -n $ResourceGroup -l $Location --tags @Tags -o none | Out-Null

Write-Step 'network security group'
if (-not (Test-AzResource @('network', 'nsg', 'show', '-g', $ResourceGroup, '-n', "$VmName-nsg"))) {
    Invoke-Az network nsg create -g $ResourceGroup -n "$VmName-nsg" -l $Location --tags @Tags -o none | Out-Null
}
# `rule create` replaces a rule of the same name, so a changed -AllowedSshCidr is applied.
Invoke-Az network nsg rule create -g $ResourceGroup --nsg-name "$VmName-nsg" -n Allow-SSH-From-Corp-Egress `
    --priority 100 --direction Inbound --access Allow --protocol Tcp --source-address-prefixes $AllowedSshCidr `
    --source-port-ranges '*' --destination-address-prefixes '*' --destination-port-ranges 22 -o none | Out-Null
Invoke-Az network nsg rule create -g $ResourceGroup --nsg-name "$VmName-nsg" -n Deny-All-Other-Inbound `
    --priority 4096 --direction Inbound --access Deny --protocol '*' --source-address-prefixes '*' `
    --source-port-ranges '*' --destination-address-prefixes '*' --destination-port-ranges '*' -o none | Out-Null
$extra = Invoke-Az network nsg rule list -g $ResourceGroup --nsg-name "$VmName-nsg" `
    --query "[?direction=='Inbound' && access=='Allow' && name!='Allow-SSH-From-Corp-Egress'].name" -o tsv
if ($extra.Trim()) { throw "unexpected inbound allow rules on the NSG: $extra" }

$vmExists = Test-AzResource @('vm', 'show', '-g', $ResourceGroup, '-n', $VmName)
if ($vmExists -and -not (Test-Path $SecretFile)) {
    # A new password would not match the account the VM was created with, and autologon would
    # then fail at the next restart.
    throw "$VmName exists but $SecretFile does not. Restore the original DPAPI file (same Windows user and machine) or -Remove and start again."
}
$password = Get-AdminPassword
if (-not $vmExists) {
    $created = $false
    foreach ($size in $Sizes) {
        Write-Step "creating $VmName ($size)"
        $plain = ConvertTo-PlainText $password
        try {
            Invoke-Az vm create -g $ResourceGroup -n $VmName -l $Location --computer-name $ComputerName `
                --image $Image --size $size --security-type $SecurityType --admin-username $AdminUser `
                --admin-password $plain --license-type Windows_Client --vnet-name "$VmName-vnet" --subnet default `
                --public-ip-address "$VmName-pip" --public-ip-sku Standard --public-ip-address-allocation static `
                --nsg "$VmName-nsg" --storage-sku StandardSSD_LRS --tags @Tags -o none | Out-Null
            $created = $true
            break
        } catch {
            if ("$_" -notmatch 'SkuNotAvailable|NotAvailableForSubscription|QuotaExceeded|AllocationFailed') { throw }
            Write-Step "$size unavailable: $($_.Exception.Message.Split("`n")[0])"
        } finally {
            $plain = $null
        }
    }
    if (-not $created) { throw "none of $($Sizes -join ', ') could be created in $Location" }
}
Invoke-Az vm start -g $ResourceGroup -n $VmName -o none | Out-Null

Write-Step "auto-shutdown at $AutoShutdownUtc UTC (registers Microsoft.DevTestLab if needed)"
Invoke-Az vm auto-shutdown -g $ResourceGroup -n $VmName --time $AutoShutdownUtc -o none | Out-Null

$ip = Get-VmPublicIp

Write-Step 'OpenSSH (key-only), the rehearsal user, firewall (the first run waits on Windows Update)'
$bootstrap = (Expand-Guest $GuestBootstrap).
    Replace('__ADMIN_KEY_B64__', [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($adminPub))).
    Replace('__TUNNEL_KEY_B64__', [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($tunnelPub)))
$message = Invoke-VmRunCommand -Script $bootstrap
$hostKeyLine = ($message -split "`n" | Where-Object { $_ -like 'HOSTKEY=*' } | Select-Object -First 1)
if (-not $hostKeyLine) { throw "the bootstrap did not report a host key: $message" }
# Pin exactly the key the VM reported through Azure's control plane.
$hostKey = ($hostKeyLine.Substring(8).Trim() -split '\s+')[0..1] -join ' '
$khDir = Split-Path $KnownHostsPath
if (-not (Test-Path $khDir)) { New-Item -ItemType Directory -Path $khDir -Force | Out-Null }
Set-Content -Path $KnownHostsPath -Value "$ip $hostKey" -Encoding ascii

Write-Step 'autologon for the admin'
$envelope = Invoke-VmRunCommand -Script $GuestEnvelopeKey
$pubXml = ($envelope -split "`n" | Where-Object { $_ -like 'PUBKEY=*' } | Select-Object -First 1).Substring(7).Trim()
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.FromXmlString($pubXml)
    $plainBytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-PlainText $password))
    $cipher = [Convert]::ToBase64String($rsa.Encrypt($plainBytes, [System.Security.Cryptography.RSAEncryptionPadding]::OaepSHA1))
    [Array]::Clear($plainBytes, 0, $plainBytes.Length)
} finally {
    $rsa.Dispose()
}
Invoke-VmRunCommand -Script (Expand-Guest $GuestAutologon).Replace('__CIPHER_B64__', $cipher) | Write-Verbose

Write-Step 'Git, PowerShell 7, Edge'
Invoke-VmRunCommand -Script $GuestTools | Write-Verbose

Write-Step 'Hyperloop server at logon'
if ($HyperloopDirectory) {
    foreach ($exe in 'Hyperloop-server.exe', 'Hyperloop.exe') {
        $sig = Get-AuthenticodeSignature (Join-Path $HyperloopDirectory $exe)
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
            throw "$exe in $HyperloopDirectory is not a valid Microsoft-signed binary ($($sig.Status))"
        }
    }
    if (Test-Ssh $ip) {
        foreach ($pair in @(@('Hyperloop-server.exe', 'hyperloop-server.exe'), @('Hyperloop.exe', 'hyperloop.exe'))) {
            & scp @(Get-SshArgument $ip) -q (Join-Path $HyperloopDirectory $pair[0]) "${AdminUser}@${ip}:C:/Tools/$($pair[1])"
            if ($LASTEXITCODE -ne 0) {
                # A running server holds its files open; that copy is already in place.
                Write-Warning "could not replace C:\Tools\$($pair[1]) (in use?); keeping the existing one"
            }
        }
    } else {
        Write-Warning ("SSH from here does not reach $ip (is this network's SSH egress outside ${AllowedSshCidr}?). " +
            'Copy Hyperloop-server.exe and Hyperloop.exe to C:\Tools on the VM from a machine whose SSH it accepts.')
    }
}
Invoke-VmRunCommand -Script (Expand-Guest $GuestHyperloopTask) | Write-Verbose

Invoke-VmRestart

Write-Step 'WSL'
$wsl = Invoke-VmAsAdmin -Script $GuestWslInstall
$wsl | Write-Verbose
if ($wsl -match 'restart-needed') { Invoke-VmRestart }

Write-Step 'Ubuntu and Docker Engine in WSL'
Invoke-VmAsAdmin -Script (Expand-Guest $GuestUbuntuDocker) -TimeoutMinutes 45 | Write-Verbose

# Restart once more so the check below proves dockerd comes up by itself at logon.
Invoke-VmRestart
Start-Sleep -Seconds 60
Write-Step 'checking Docker'
$check = Invoke-VmAsAdmin -Script $GuestDockerCheck
$check | Write-Verbose
if ($check -notmatch 'docker-task=Running' -or $check -notmatch 'compose=\d' -or $check -notmatch 'hello-world=True' -or $check -notmatch 'windows-loopback-18123=200') {
    throw "Docker check failed: $check"
}

Write-Step 'checking Hyperloop'
$hyperloop = Invoke-VmRunCommand -Script (Expand-Guest $GuestHyperloopCheck)
$hyperloop | Write-Verbose
if ($hyperloop -match 'hyperloop=absent') { Write-Warning 'Hyperloop is not installed in C:\Tools yet; see the warning above' }

Write-Step "ready: $ComputerName at $ip"
Get-TunnelCommand $ip
