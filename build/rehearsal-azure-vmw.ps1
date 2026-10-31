#Requires -Version 7.3
<#
.SYNOPSIS
    Provisions VM-W, or with -Role M VM-M, for the migration rehearsal (docs/parity-checklist.md)
    as an Azure VM; starts, deallocates, snapshots, restores or removes them.

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

    With -Role M it builds VM-M (REHEARSAL-M) the same way, in the same network as VM-W, except:
    the console user is the standard user `rehearsal`, who runs Hyperloop and has Node.js LTS,
    Copilot CLI, Claude Code and Codex installed but not signed in; `rehearsal` has no SSH access;
    and instead of Docker it gets a key and ssh config whose `ssh rehearsal@VM-W` reaches VM-W's
    private address, authorized for VM-W's `rehearsal` and admitted by VM-W's NSG from VM-M's
    private address only. -Snapshot and -RestoreSnapshot stand in for Hyper-V checkpoints.

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

.PARAMETER Role
    W (the default) for VM-W, M for VM-M. Every action applies to that VM only, except -Remove,
    which deletes the whole group, and -JumpHostSetup, which covers every VM that exists.

.PARAMETER AllowedSshCidr
    The only source range allowed to reach TCP 22. Ranges wider than /16 are refused.

.PARAMETER AdminKeyPath
    Private key (ed25519) for the admin account. Created, without a passphrase, if missing.

.PARAMETER TunnelPublicKeyPath
    Public keys that VM-M uses as `rehearsal` on VM-W. Optional; without them `rehearsal` has no
    key. -Role M authorizes its own key directly and saves it as rehearsal-m-tunnel.pub beside
    -SecretFile, so pass that file too when re-provisioning VM-W.

.PARAMETER SecretFile
    The DPAPI file holding the admin password. Created if missing. Defaults to one per role.

.PARAMETER UserSecretFile
    -Role M: the DPAPI file holding `rehearsal`'s password, which autologon needs. Created if missing.

.PARAMETER HyperloopDirectory
    A folder holding Hyperloop-server.exe and its sibling worker Hyperloop.exe. Optional; without it
    the binaries are not copied (the logon task is registered either way).

.PARAMETER AzureConfigDir
    AZURE_CONFIG_DIR for the az CLI, so a separate sign-in can be used.

.PARAMETER ExpectedAzureUser
    The az sign-in the rehearsal belongs to. Any other is refused unless -AllowOtherAzureUser.

.PARAMETER JumpVia
    user@host of an SSH jump host (VM-M) for an operator whose own SSH egress the NSG does not
    accept. SSH to VM-W then goes through it with ProxyJump, from a generated ssh config file.
    The jump host never holds a key for VM-W.

.PARAMETER JumpKeyPath
    This machine's key for the jump account. Created, without a passphrase, if missing.

.PARAMETER JumpHostKey
    The jump host's sshd public key ("ssh-ed25519 AAAA..."), as -JumpHostSetup's script printed
    it, read over a channel you trust. Pinned; without it the pin must already exist.

.PARAMETER JumpHostSetup
    Print the script that makes the jump host (elevated, Windows PowerShell): sshd key-only for
    one low-privilege account whose key may only open VM-W:22, with no shell, and an inbound
    rule for -JumpAllowedCidr only. Run it there (VM-M has Hyperloop), then pin its HOSTKEY.

.PARAMETER JumpAllowedCidr
    For -JumpHostSetup: the only source allowed to reach the jump host's sshd, normally this
    machine's Hyper-V switch subnet.

.PARAMETER Snapshot
    Take an incremental snapshot of the VM's OS disk, named <vm>-<SnapshotName>. A running VM is
    deallocated first, for a clean disk, and started again afterwards. Never overwrites one.

.PARAMETER RestoreSnapshot
    Replace the VM's OS disk with a new disk made from <vm>-<SnapshotName>, then boot it. Everything
    written since the snapshot is lost, and the replaced disk is deleted unless -KeepReplacedDisk.

.PARAMETER SnapshotName
    The snapshot's name after the VM's, such as S1.

.PARAMETER ListSnapshots
    The snapshots taken of this VM.

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -AllowedSshCidr 203.0.113.0/24 `
        -TunnelPublicKeyPath .\rehearsal-m-tunnel.pub -HyperloopDirectory <Hyperloop.Tools folder>

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -JumpHostSetup -JumpVia vmjump@<VM-M ip> `
        -JumpAllowedCidr <Hyper-V switch subnet>

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Start -JumpVia vmjump@<VM-M ip> -JumpHostKey 'ssh-ed25519 AAAA...'

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Role M -AllowedSshCidr 203.0.113.0/24 `
        -JumpVia vmjump@<jump ip> -HyperloopDirectory <Hyperloop.Tools folder>

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Role M -Snapshot -SnapshotName S1

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Role M -RestoreSnapshot -SnapshotName S1

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Deallocate

.EXAMPLE
    ./build/rehearsal-azure-vmw.ps1 -Subscription <id> -Remove
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Subscription', Justification = 'Used by Invoke-Az and Test-AzResource')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'JumpHostKey', Justification = 'Used by Write-JumpSshConfig')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPositionalParameters', '', Justification = 'Invoke-Az passes az arguments through verbatim')]
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Provision')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string] $Subscription,

    # Before every parameter whose default depends on it.
    [ValidateSet('W', 'M')]
    [string] $Role = 'W',

    [Parameter(Mandatory, ParameterSetName = 'Provision')]
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$')]
    [string] $AllowedSshCidr,

    [Parameter(ParameterSetName = 'Provision')]
    [Parameter(ParameterSetName = 'Start')]
    [string] $AdminKeyPath = (Join-Path $HOME '.ssh/agentbridge-rehearsal'),

    [Parameter(ParameterSetName = 'Provision')]
    [string[]] $TunnelPublicKeyPath,

    [Parameter(ParameterSetName = 'Provision')]
    [string] $SecretFile = (Join-Path $HOME ".agentbridge-rehearsal/rehearsal-$($Role.ToLowerInvariant())-admin.dpapi"),

    [Parameter(ParameterSetName = 'Provision')]
    [string] $UserSecretFile = (Join-Path $HOME '.agentbridge-rehearsal/rehearsal-m-user.dpapi'),

    [Parameter(ParameterSetName = 'Provision')]
    [string] $HyperloopDirectory,

    [string] $AzureConfigDir = (Join-Path $HOME '.azure-agentbridge'),

    [string] $ExpectedAzureUser = 'danswett@outlook.com',

    [switch] $AllowOtherAzureUser,

    [Parameter(ParameterSetName = 'Provision')]
    [Parameter(ParameterSetName = 'Start')]
    [Parameter(Mandatory, ParameterSetName = 'JumpSetup')]
    [ValidatePattern('^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+$')]
    [string] $JumpVia,

    [Parameter(ParameterSetName = 'Provision')]
    [Parameter(ParameterSetName = 'Start')]
    [Parameter(ParameterSetName = 'JumpSetup')]
    [string] $JumpKeyPath = (Join-Path $HOME '.ssh/agentbridge-vm01-jump'),

    [Parameter(ParameterSetName = 'Provision')]
    [Parameter(ParameterSetName = 'Start')]
    [ValidatePattern('^ssh-ed25519 [A-Za-z0-9+/=]+$')]
    [string] $JumpHostKey,

    [Parameter(Mandatory, ParameterSetName = 'JumpSetup')]
    [switch] $JumpHostSetup,

    [Parameter(Mandatory, ParameterSetName = 'JumpSetup')]
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$')]
    # The pattern alone admits 999.0.0.0/99; the setup disables the jump host's other SSH rules
    # before adding this one, so a range Windows rejects there would leave sshd unreachable.
    [ValidateScript({
            $address, $prefix = $_.Split('/')
            $ip = $null
            [ipaddress]::TryParse($address, [ref] $ip) -and $ip.AddressFamily -eq 'InterNetwork' -and
            ($address.Split('.') | ForEach-Object { [int] $_ -le 255 }) -notcontains $false -and [int] $prefix -le 32
        })]
    [string] $JumpAllowedCidr,

    [Parameter(Mandatory, ParameterSetName = 'Deallocate')]
    [switch] $Deallocate,

    [Parameter(Mandatory, ParameterSetName = 'Start')]
    [switch] $Start,

    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [switch] $Remove,

    [Parameter(Mandatory, ParameterSetName = 'Snapshot')]
    [switch] $Snapshot,

    [Parameter(Mandatory, ParameterSetName = 'RestoreSnapshot')]
    [switch] $RestoreSnapshot,

    [Parameter(Mandatory, ParameterSetName = 'Snapshot')]
    [Parameter(Mandatory, ParameterSetName = 'RestoreSnapshot')]
    [ValidatePattern('^[A-Za-z0-9_-]{1,40}$')]
    [string] $SnapshotName,

    [Parameter(ParameterSetName = 'RestoreSnapshot')]
    [switch] $KeepReplacedDisk,

    [Parameter(Mandatory, ParameterSetName = 'ListSnapshots')]
    [switch] $ListSnapshots
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Fixed rather than parameters: -Remove deletes this group and nothing else, whatever it is given.
$ResourceGroup = 'rg-agent-bridge-rehearsal'
$Location = 'westus2'
# VM-W (the publication writer, Home Assistant in Docker) or VM-M (the machine being migrated).
$VmName = "rehearsal-$($Role.ToLowerInvariant())"
$ComputerName = "REHEARSAL-$Role"
$AdminUser = 'rehearsaladmin'
# On VM-W, `rehearsal` is the SFTP-only account VM-M's tunnel signs in as. On VM-M it is the
# checklist's one standard user: signed in at the console, it runs the agents and Hyperloop.
$TunnelUser = 'rehearsal'
$DesktopUser = if ($Role -eq 'M') { $TunnelUser } else { $AdminUser }
# Both VMs share one network, so VM-M reaches VM-W's sshd at its private address and nothing else
# is opened to the internet. The name predates VM-M.
$VnetName = 'rehearsal-w-vnet'
$Image = 'MicrosoftWindowsDesktop:windows-11:win11-24h2-pro:latest'
# D4s_v5 runs nested virtualization (WSL2 needs it); D4as_v5 is the fallback. Standard security
# type: nothing here needs Trusted Launch, and it is what the CLI chose for this image.
$Sizes = @('Standard_D4s_v5', 'Standard_D4as_v5')
$SecurityType = 'Standard'
$AutoShutdownUtc = '0800'
$HyperloopPort = 4850
# This machine's end of the Hyperloop tunnel, distinct per VM so both can be open at once.
$HyperloopLocalPort = if ($Role -eq 'M') { 4852 } else { 4851 }
$Tags = @('purpose=rehearsal-39', 'owner=danswett', 'lifetime=delete-after-rehearsal')
# VM-W's names predate -Role and are kept, so an existing VM-W's pins and config stay valid.
$RoleSuffix = if ($Role -eq 'M') { '-m' } else { '' }
$KnownHostsPath = Join-Path $HOME ".ssh/known_hosts_agentbridge-rehearsal$RoleSuffix"
$WriterKnownHostsPath = Join-Path $HOME '.ssh/known_hosts_agentbridge-rehearsal'
$JumpKnownHostsPath = Join-Path $HOME '.ssh/known_hosts_agentbridge-vm01-jump'
$SshConfigPath = Join-Path $HOME ".ssh/config-agentbridge-rehearsal$RoleSuffix"
$JumpUser, $JumpHost = if ($JumpVia) { $JumpVia.Split('@', 2) } else { $null, $null }
# Windows OpenSSH matches AllowUsers against lowercase names, so an uppercase one would lock out.
if ($JumpUser) { $JumpUser = $JumpUser.ToLowerInvariant() }
# Relative paths resolve against the current location, so Split-Path always finds a parent.
foreach ($name in 'SecretFile', 'UserSecretFile', 'AdminKeyPath', 'TunnelPublicKeyPath', 'HyperloopDirectory', 'AzureConfigDir', 'JumpKeyPath') {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value) {
        $resolved = foreach ($v in @($value)) { $PSCmdlet.GetUnresolvedProviderPathFromPSPath($v) }
        Set-Variable -Name $name -Value $(if ($value -is [array]) { @($resolved) } else { $resolved }) -WhatIf:$false -Confirm:$false
    }
}
$StateDir = Split-Path $SecretFile

$env:AZURE_CONFIG_DIR = $AzureConfigDir

function Write-Step([string] $Message) {
    Write-Information "[$VmName] $Message" -InformationAction Continue
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

function Get-VmPublicIp([string] $Name = $VmName) {
    return (Invoke-Az network public-ip show -g $ResourceGroup -n "$Name-pip" --query ipAddress -o tsv).Trim()
}

function Get-TunnelCommand([string] $Ip) {
    $ssh = if ($JumpVia) { "ssh -F `"$SshConfigPath`"" } else {
        "ssh -i `"$AdminKeyPath`" -o IdentitiesOnly=yes -o UserKnownHostsFile=`"$KnownHostsPath`" -o StrictHostKeyChecking=yes"
    }
    $hostKey = if (Test-Path $KnownHostsPath) { (Get-Content $KnownHostsPath -Raw).Trim() } else { '<run a provision first>' }
    $result = [ordered]@{
        HyperloopTunnel = "$ssh -N -L 127.0.0.1:${HyperloopLocalPort}:127.0.0.1:${HyperloopPort} $AdminUser@$Ip"
        HyperloopServer = "hyperloop servers add $VmName --scheme tcp --authority 127.0.0.1:$HyperloopLocalPort"
        AdminShell      = "$ssh $AdminUser@$Ip"
    }
    if ($Role -eq 'W') {
        # VM-M must not trust on first use: append KnownHostsLine to its ~/.ssh/known_hosts first.
        $result.KnownHostsLine = $hostKey
        $result.VmMTunnel = "ssh -i <VM-M key> -o StrictHostKeyChecking=yes -N -L 8123:127.0.0.1:8123 $TunnelUser@$Ip"
    } else {
        # Provisioning VM-M writes its rehearsal user's ~/.ssh/config, so the checklist's command
        # runs as written: VM-W is the alias, at its private address, with its host key pinned.
        $result.VmMTunnel = "ssh -N -L 8123:127.0.0.1:8123 $TunnelUser@VM-W   (on VM-M, as $TunnelUser)"
    }
    return [pscustomobject] $result
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

function Get-StoredPassword([string] $Path) {
    if (Test-Path $Path) {
        return Get-Content -Path $Path | ConvertTo-SecureString
    }
    $secure = Get-RandomAdminPassword
    $secure | ConvertFrom-SecureString | Set-Content -Path $Path -Encoding ascii
    Write-Step "password generated and stored DPAPI-protected at $Path"
    return $secure
}

function ConvertTo-PlainText([securestring] $Secure) {
    return [System.Net.NetworkCredential]::new('', $Secure).Password
}

function Initialize-SshKey([string] $Path, [string] $Comment) {
    if (Test-Path $Path) { return }
    $dir = Split-Path $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    # PowerShell 7.3+ passes '' to a native command as an empty argument; '""' would become a
    # two-character passphrase.
    & ssh-keygen -t ed25519 -N '' -C $Comment -f $Path -q
    if ($LASTEXITCODE -ne 0) { throw 'ssh-keygen failed' }
    Write-Step "key created at $Path"
}

function Write-JumpSshConfig([string] $Ip) {
    <#
    The ssh config that routes VM-W through the jump host. A config file rather than -J on the
    command line, because ssh does not pass -i, -o UserKnownHostsFile or StrictHostKeyChecking on
    to the jump connection; with -F the same file governs both hops, each pinned.
    #>
    if ($JumpHostKey) {
        Set-Content -Path $JumpKnownHostsPath -Value "$JumpHost $JumpHostKey" -Encoding ascii
    }
    if (-not (Test-Path $JumpKnownHostsPath) -or
        -not (Select-String -Path $JumpKnownHostsPath -SimpleMatch "$JumpHost " -Quiet)) {
        throw "the jump host's key is not pinned in $JumpKnownHostsPath; pass -JumpHostKey with the HOSTKEY that -JumpHostSetup's script printed"
    }
    $config = @"
# Written by build/rehearsal-azure-vmw.ps1 -JumpVia. Use with: ssh -F "$SshConfigPath" $VmName
Host vm01-jump $JumpHost
    HostName $JumpHost
    User $JumpUser
    IdentityFile "$JumpKeyPath"
    IdentitiesOnly yes
    UserKnownHostsFile "$JumpKnownHostsPath"
    StrictHostKeyChecking yes
    ForwardAgent no
    RequestTTY no
    # The jump hop never needs a prompt; without this, a scripted call could stop at one.
    BatchMode yes

Host $VmName $Ip
    HostName $Ip
    User $AdminUser
    IdentityFile "$AdminKeyPath"
    IdentitiesOnly yes
    UserKnownHostsFile "$KnownHostsPath"
    StrictHostKeyChecking yes
    ProxyJump vm01-jump
    ExitOnForwardFailure yes
    ServerAliveInterval 30
"@
    # UTF-8 without a BOM: the file names paths, which may not be ASCII, and ssh reads UTF-8.
    Set-Content -Path $SshConfigPath -Value $config -Encoding utf8NoBOM
}

function Invoke-VmRunCommand {
    <#
    Runs a script on the VM as SYSTEM through Azure's control plane. Only non-secret text may go
    in: run-command keeps the script in the VM's extension settings.
    #>
    param([string] $Script, [string] $Name = $VmName)
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
        $json = Invoke-Az vm run-command invoke -g $ResourceGroup -n $Name --command-id RunPowerShellScript `
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

function Wait-DesktopSession([int] $TimeoutSeconds = 900) {
    # After a restart, autologon signs the desktop user in at the console; the logon tasks start then.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $q = Invoke-VmRunCommand -Script 'quser 2>&1 | Out-String'
            if ($q -match "$DesktopUser\s+console\s+\d+\s+Active") { return }
        } catch {
            Write-Verbose "waiting for the VM: $_"
        }
        Start-Sleep -Seconds 15
    }
    throw "$DesktopUser was not signed in at the console within $TimeoutSeconds s (autologon?)"
}

function Invoke-VmAsDesktopUser {
    <#
    Runs a script as the desktop user inside its autologon session: a one-shot scheduled task
    started from run-command, whose output comes back through a log file. Elevated when that user
    is the admin (VM-W); unelevated as `rehearsal` on VM-M, which is a standard user. WSL
    distributions and npm's global packages are per user, so neither can be installed as SYSTEM.
    The job runs with
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
$principal = New-ScheduledTaskPrincipal -UserId '__DESKTOP_USER__' -LogonType Interactive -RunLevel __RUN_LEVEL__
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(__TIMEOUT__))
# The job and its log sit in a folder only administrators can write; a standard desktop user
# needs to write its log, so it gets this one job's files and nothing else (opening them by path
# needs no right on the folder, which stays unlistable to it).
if ('__RUN_LEVEL__' -eq 'Limited') {
    Set-Content -Path $log -Value '' -Encoding utf8
    & icacls.exe $log /grant '__DESKTOP_USER__:M' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $log failed" }
    & icacls.exe $job /grant '__DESKTOP_USER__:RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $job failed" }
}
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
    $runLevel = if ($DesktopUser -eq $AdminUser) { 'Highest' } else { 'Limited' }
    $runner = $runner.Replace('__B64__', $b64).Replace('__DESKTOP_USER__', $DesktopUser).Replace('__RUN_LEVEL__', $runLevel).
        Replace('__TIMEOUT__', "$TimeoutMinutes")
    return Invoke-VmRunCommand -Script $runner
}

function Invoke-VmRestart {
    Write-Step 'restarting the VM'
    Invoke-Az vm restart -g $ResourceGroup -n $VmName -o none | Out-Null
    Wait-DesktopSession
}

function Get-SshArgument([string] $Ip) {
    if ($JumpVia) { return @('-F', $SshConfigPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15') }
    return @('-i', $AdminKeyPath, '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
        '-o', "UserKnownHostsFile=$KnownHostsPath", '-o', 'StrictHostKeyChecking=yes',
        '-o', 'ConnectTimeout=15')
}

function Test-Ssh([string] $Ip) {
    & ssh @(Get-SshArgument $Ip) "$AdminUser@$Ip" 'exit 0' 2>$null
    return $LASTEXITCODE -eq 0
}

function Get-OsDiskId {
    return (Invoke-Az vm show -g $ResourceGroup -n $VmName --query storageProfile.osDisk.managedDisk.id -o tsv).Trim()
}

function Test-VmRunning {
    $codes = Invoke-Az vm get-instance-view -g $ResourceGroup -n $VmName --query 'instanceView.statuses[].code' -o tsv
    return @($codes -split "`n" | ForEach-Object { $_.Trim() }) -contains 'PowerState/running'
}

function Get-SourceVm($Resource) {
    # The VM a snapshot or disk was made from, recorded in its tags when it was created here.
    $tags = $Resource.PSObject.Properties['tags']
    if (-not $tags -or -not $tags.Value) { return $null }
    $source = $tags.Value.PSObject.Properties['source-vm']
    if ($source) { return "$($source.Value)" } else { return $null }
}

function Get-VmPrivateIp([string] $Name) {
    return (Invoke-Az vm list-ip-addresses -g $ResourceGroup -n $Name `
            --query '[0].virtualMachine.network.privateIpAddresses[0]' -o tsv).Trim()
}

# ---------------------------------------------------------------------------------------------
# Guest scripts. Placeholders are replaced before sending; keys travel as base64 so no key text
# meets a quoting layer.
# ---------------------------------------------------------------------------------------------
$GuestBootstrap = @'
$role = '__ROLE__'
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
        -Description $(if ($role -eq 'M') { 'Rehearsal: the migrated machine''s user' } else { 'Rehearsal: VM-M tunnel to Home Assistant' }) | Out-Null
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
$sid = (Get-LocalUser -Name $tunnelUser).SID.Value
if ($role -eq 'W') {
    # Written even when empty, so dropping -TunnelPublicKeyPath on a rerun revokes the old key.
    Set-KeyFile (Join-Path $sshDir "${tunnelUser}_authorized_keys") $tunnelKey $sid

    # The only folder `rehearsal` sees (sshd chroots its SFTP here), so it cannot plant or read
    # files anywhere else on C:\. Files it exchanges, such as A-04's token, go through here.
    $drop = 'C:\RehearsalDrop'
    New-Item -ItemType Directory -Path $drop -Force | Out-Null
    & icacls.exe $drop /setowner '*S-1-5-32-544' /T /C | Out-Null
    & icacls.exe $drop /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' "*${sid}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls $drop failed" }
    $allowUsers = "$adminUser $tunnelUser"
} else {
    # On VM-M `rehearsal` works at the console and never signs in over SSH.
    Remove-Item (Join-Path $sshDir "${tunnelUser}_authorized_keys") -Force -ErrorAction SilentlyContinue
    $allowUsers = $adminUser
}

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
AllowUsers $allowUsers
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
# On VM-M the desktop user is not the admin: its throwaway password becomes this one, kept only in
# the DPAPI file that encrypted it here.
if ('__DESKTOP_USER__' -ne '__ADMIN_USER__') {
    Set-LocalUser -Name '__DESKTOP_USER__' -Password (ConvertTo-SecureString $secret -AsPlainText -Force)
}
[RehearsalLsa]::Store('DefaultPassword', $secret)
$secret = $null
$wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Set-ItemProperty -Path $wl -Name AutoAdminLogon -Value '1'
Set-ItemProperty -Path $wl -Name DefaultUserName -Value '__DESKTOP_USER__'
Set-ItemProperty -Path $wl -Name DefaultDomainName -Value $env:COMPUTERNAME
Remove-ItemProperty -Path $wl -Name DefaultPassword -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $wl -Name AutoLogonCount -ErrorAction SilentlyContinue
'autologon=configured'
'@

# Hyperloop's server in the desktop user's session, visible in a console window. tcp mode with no
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
$trigger = New-ScheduledTaskTrigger -AtLogOn -User '__DESKTOP_USER__'
# Started at the instant of logon, the server accepted TCP but never completed a TLS handshake;
# a short delay lets the session settle first.
$trigger.Delay = 'PT45S'
$principal = New-ScheduledTaskPrincipal -UserId '__DESKTOP_USER__' -LogonType Interactive -RunLevel Limited
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

# VM-M: Node.js LTS machine-wide, for the agents' npm packages. The MSI must match nodejs.org's
# published SHA-256 and carry a valid Authenticode signature before it runs.
$GuestNode = @'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if (-not (Test-Path "$env:ProgramFiles\nodejs\node.exe")) {
    $dl = Get-ProtectedDirectory 'downloads'
    # Windows PowerShell hands Invoke-RestMethod's JSON array down a pipeline as one object, so it
    # is enumerated explicitly; otherwise every version lands in the URL at once.
    $index = Invoke-RestMethod 'https://nodejs.org/dist/index.json'
    $lts = @($index) | Where-Object { $_.lts } | Select-Object -First 1
    if (-not $lts -or "$($lts.version)" -notmatch '^v\d+\.\d+\.\d+$') { throw "no Node.js LTS version in nodejs.org's index" }
    $name = "node-$($lts.version)-x64.msi"
    $path = Join-Path $dl $name
    if (-not (Test-Path $path)) {
        Invoke-WebRequest "https://nodejs.org/dist/$($lts.version)/$name" -OutFile "$path.partial" -UseBasicParsing
        Move-Item "$path.partial" $path -Force
    }
    $sums = (Invoke-WebRequest "https://nodejs.org/dist/$($lts.version)/SHASUMS256.txt" -UseBasicParsing).Content
    $expected = (($sums -split "`n") | Where-Object { $_ -match "\s$([regex]::Escape($name))$" } | Select-Object -First 1) -replace '\s.*$', ''
    $sig = Get-AuthenticodeSignature $path
    if (-not $expected -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $expected.ToUpperInvariant() -or $sig.Status -ne 'Valid') {
        Remove-Item $path -Force
        throw "$name failed its checksum or signature ($($sig.Status)); deleted so a rerun downloads it again"
    }
    $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$path`"", '/qn', '/norestart') -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "Node.js MSI exit $($p.ExitCode)" }
    Remove-Item $dl -Recurse -Force -ErrorAction SilentlyContinue
}
"node=$(& "$env:ProgramFiles\nodejs\node.exe" --version)"
'@

# VM-M, as `rehearsal`: Copilot CLI, Claude Code and Codex from npm, per user like a person's own
# install, and deliberately not signed in. Signing in is a person's step (A-03), done at the
# console through Hyperloop.
$GuestAgents = @'
$npm = "$env:ProgramFiles\nodejs\npm.cmd"
$prefix = Join-Path $env:APPDATA 'npm'
# The Node MSI adds npm's global folder to the PATH of whoever ran it, which was SYSTEM.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (@("$userPath".Split(';')) -notcontains $prefix) {
    [Environment]::SetEnvironmentVariable('Path', ((@("$userPath".Split(';')) + $prefix | Where-Object { $_ }) -join ';'), 'User')
}
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$out = & $npm install --global --no-fund --no-audit '@github/copilot' '@anthropic-ai/claude-code' '@openai/codex' 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "npm install exit $($LASTEXITCODE): $out" }
foreach ($agent in 'copilot', 'claude', 'codex') {
    $cmd = Join-Path $prefix "$agent.cmd"
    if (-not (Test-Path $cmd)) { throw "$agent was not installed" }
    "$agent=$((& $cmd --version 2>&1 | Out-String).Trim())"
}
'@

# VM-M, as `rehearsal`: the key and ssh config for the checklist's `ssh -N -L 8123:127.0.0.1:8123
# rehearsal@VM-W`. VM-W is an alias for its private address, and its host key, taken from this
# machine's pin, is the only one trusted for it.
$GuestVmWClient = @'
$sshDir = Join-Path $env:USERPROFILE '.ssh'
New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
$key = Join-Path $sshDir 'id_ed25519'
if (-not (Test-Path $key)) {
    # Through Start-Process so -N gets a real empty passphrase; Windows PowerShell drops ''.
    $p = Start-Process "$env:SystemRoot\System32\OpenSSH\ssh-keygen.exe" -Wait -PassThru -NoNewWindow `
        -ArgumentList @('-t', 'ed25519', '-N', '""', '-C', 'rehearsal@REHEARSAL-M', '-f', "`"$key`"", '-q')
    if ($p.ExitCode -ne 0) { throw "ssh-keygen exit $($p.ExitCode)" }
}
Set-Content -Path (Join-Path $sshDir 'known_hosts_vm-w') -Value '__W_PRIVATE_IP__ __W_HOST_KEY__' -Encoding ascii
$block = @"
# >>> rehearsal VM-W (build/rehearsal-azure-vmw.ps1 -Role M)
Host VM-W vm-w
    HostName __W_PRIVATE_IP__
    User __TUNNEL_USER__
    IdentityFile ~/.ssh/id_ed25519
    IdentitiesOnly yes
    UserKnownHostsFile ~/.ssh/known_hosts_vm-w
    StrictHostKeyChecking yes
    ExitOnForwardFailure yes
    ServerAliveInterval 30
# <<< rehearsal VM-W
"@
$config = Join-Path $sshDir 'config'
$text = if (Test-Path $config) { Get-Content $config -Raw } else { '' }
$text = [regex]::Replace("$text", '(?s)# >>> rehearsal VM-W.*?# <<< rehearsal VM-W\r?\n?', '')
Set-Content -Path $config -Value ($text.TrimEnd() + "`r`n" + $block).TrimStart() -Encoding ascii
"PUBKEY=$((Get-Content "$key.pub" -Raw).Trim())"
'@

# VM-W, as SYSTEM: VM-M's key joins any already authorized for `rehearsal`, keeping the file's ACL.
$GuestAuthorizeVmM = @'
$file = Join-Path $env:ProgramData 'ssh\__TUNNEL_USER___authorized_keys'
if (-not (Test-Path $file)) { throw "$file does not exist; provision VM-W first" }
$key = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String('__KEY_B64__')).Trim()
if (@(Get-Content $file | ForEach-Object { $_.Trim() }) -notcontains $key) { Add-Content -Path $file -Value $key -Encoding ascii }
'vm-m-key=authorized'
'@

# VM-M, as `rehearsal`: the tunnel works end to end. SFTP proves the key, the pinned host key and
# the NSG; the forward proves PermitOpen. Home Assistant may not be running yet (A-02 starts it),
# so its HTTP status is reported, not required.
$GuestVmWCheck = @'
$batch = Join-Path $env:TEMP 'rehearsal-sftp.txt'
Set-Content -Path $batch -Value 'pwd' -Encoding ascii
$sftp = & "$env:SystemRoot\System32\OpenSSH\sftp.exe" -b $batch -o BatchMode=yes -o ConnectTimeout=15 VM-W 2>&1 | Out-String
"sftp-exit=$LASTEXITCODE"
Remove-Item $batch -ErrorAction SilentlyContinue
$ssh = Start-Process "$env:SystemRoot\System32\OpenSSH\ssh.exe" -PassThru -WindowStyle Hidden `
    -ArgumentList @('-o', 'BatchMode=yes', '-N', '-L', '18123:127.0.0.1:8123', 'VM-W')
Start-Sleep -Seconds 8
$listening = $false
try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 18123); $listening = $c.Connected; $c.Close() } catch { $listening = $false }
"forward-listening=$listening"
$code = 'none'
try { $code = (Invoke-WebRequest 'http://127.0.0.1:18123/' -UseBasicParsing -TimeoutSec 10).StatusCode } catch { $code = "none ($($_.Exception.Message.Split([Environment]::NewLine)[0]))" }
"home-assistant-http=$code"
if (-not $ssh.HasExited) { $ssh.Kill() }
'@

# The jump host (VM-M), for an operator whose SSH egress the NSG does not accept. It runs there,
# elevated, by whatever means the operator has (VM-M is driven through Hyperloop), and prints the
# host key to pin. It needs sshd installed first: the OpenSSH.Server capability, or the signed
# Win32-OpenSSH MSI where Windows Update cannot supply it (0x800f0950 on the rehearsal's VM-M).
$JumpHostSetupScript = @'
# Runs elevated on the jump host (VM-M), in Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$jumpUser = '__JUMP_USER__'
$allowedRemote = '__ALLOWED_REMOTE__'
# Every rehearsal VM's sshd, and nothing else.
$targets = @('__TARGETS__'.Split(' ') | Where-Object { $_ })
$jumpKey = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String('__JUMP_KEY_B64__')).Trim()
$sshDir = Join-Path $env:ProgramData 'ssh'

# Before changing anything: reuse only the account this script made, never an administrator or
# someone's real account. Read the group through ADSI, failing closed: Get-LocalGroupMember throws
# on an orphaned or Entra member, and a swallowed error there would read as "not an administrator".
$jumpDescription = 'Rehearsal: SSH jump to VM-W only'
$existing = Get-LocalUser -Name $jumpUser -ErrorAction SilentlyContinue
if ($existing) {
    $adminsName = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
    $admins = @(([ADSI]"WinNT://./$adminsName,group").Invoke('Members') | ForEach-Object {
        $sidBytes = $_.GetType().InvokeMember('objectSid', 'GetProperty', $null, $_, $null)
        (New-Object Security.Principal.SecurityIdentifier ($sidBytes, 0)).Value
    })
    if ($admins.Count -eq 0) { throw 'could not read the Administrators group; refusing to reuse an existing account' }
    if ($existing.Description -ne $jumpDescription -or $admins -contains "$($existing.SID)") {
        throw "$jumpUser already exists and is not this script's jump account (or is an administrator); choose another -JumpVia user"
    }
}

# A fresh install may not have created it yet; the authorized-keys file is written before sshd runs.
# Whoever made it, it and everything in it end up owned by Administrators and writable only by
# them and SYSTEM, so no local user can swap the configuration the restrictions live in. Ownership
# matters as much as the ACL: an owner keeps WRITE_DAC and could grant itself access back.
function Invoke-Icacls { & icacls.exe @args | Out-Null; if ($LASTEXITCODE -ne 0) { throw "icacls $($args -join ' ') failed" } }
# Replaces owner and DACL outright. icacls /grant:r only replaces the named principals' entries, so
# anyone else's explicit grant would survive it.
function Set-ExactAcl([string] $Path, [string[]] $Grants) {
    $isDir = (Get-Item -LiteralPath $Path -Force).PSIsContainer
    $acl = if ($isDir) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = if ($isDir) { 'ContainerInherit, ObjectInherit' } else { 'None' }
    foreach ($grant in $Grants) {
        $sidText, $rights = $grant.Split('=')
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule (
            (New-Object Security.Principal.SecurityIdentifier $sidText), [Security.AccessControl.FileSystemRights] $rights,
            [Security.AccessControl.InheritanceFlags] $inherit, 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
$adminOnly = 'S-1-5-18=FullControl', 'S-1-5-32-544=FullControl'
New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
# A host key someone else planted is not one to vouch for, so check owners before taking them over.
$hostKeys = @(Get-ChildItem $sshDir -File -Force | Where-Object { $_.Name -like 'ssh_host_*_key' })
foreach ($hostKeyFile in $hostKeys) {
    $owner = (Get-Acl $hostKeyFile.FullName).GetOwner([Security.Principal.SecurityIdentifier]).Value
    # By SID: the account names are localized.
    if ($owner -notin 'S-1-5-18', 'S-1-5-32-544') { throw "$($hostKeyFile.Name) is owned by $owner; refusing to trust it" }
}
# Private host keys first, so no later step can expose them even briefly: sshd requires them to be
# readable by SYSTEM and Administrators only.
foreach ($hostKeyFile in $hostKeys) { Set-ExactAcl $hostKeyFile.FullName $adminOnly }
Set-ExactAcl $sshDir ($adminOnly + 'S-1-5-11=ReadAndExecute')
if (Get-ChildItem $sshDir -Force) { Invoke-Icacls (Join-Path $sshDir '*') /setowner '*S-1-5-32-544' /T }
# Other files directly here drop any explicit grants and inherit the directory's ACL ...
foreach ($file in Get-ChildItem $sshDir -File -Force | Where-Object { $_.Name -notlike 'ssh_host_*_key' }) {
    Invoke-Icacls $file.FullName /reset
}
# ... and sshd's logs stay private, as the installer made them.
$logDir = Join-Path $sshDir 'logs'
if (Test-Path $logDir) {
    Set-ExactAcl $logDir $adminOnly
    if (Get-ChildItem $logDir -Force) { Invoke-Icacls (Join-Path $logDir '*') /reset /T }
}

$svc = Get-CimInstance Win32_Service -Filter "Name='sshd'"
if (-not $svc) { throw 'sshd is not installed: add the OpenSSH.Server capability, or the signed Win32-OpenSSH MSI where Windows Update is unavailable' }
$sshd = $svc.PathName.Trim('"')

# Firewall first: whatever rule the installer added would allow 22 from anywhere. Only this
# host's Hyper-V switch subnet, only on the interface and profile that subnet arrives on. Work all
# of that out before disabling anything, so a bad range or a missing route stops here with sshd
# still reachable as it was.
$cidrAddress, $cidrPrefix = $allowedRemote.Split('/')
$parsed = $null
if (-not [ipaddress]::TryParse($cidrAddress, [ref] $parsed) -or [int] $cidrPrefix -gt 32) { throw "$allowedRemote is not an IPv4 CIDR" }
$route = Find-NetRoute -RemoteIPAddress $cidrAddress | Select-Object -First 1
$alias = (Get-NetIPInterface -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4).InterfaceAlias
$category = (Get-NetConnectionProfile -InterfaceAlias $alias).NetworkCategory
$fwProfile = if ("$category" -eq 'DomainAuthenticated') { 'Domain' } else { "$category" }
# The disabled rules below name port 22, but an any-port or program allow would still admit SSH from
# anywhere, and disabling those wholesale could cut off Hyperloop. Block wins over allow in Windows
# Firewall, so block 22 from everything outside the range. It goes in first and is updated in place,
# so no rerun ever has a moment without it.
function ConvertTo-Ipv4([uint64] $Value) { ((24, 16, 8, 0) | ForEach-Object { [math]::Floor($Value / [math]::Pow(2, $_)) % 256 }) -join '.' }
$octets = $cidrAddress.Split('.') | ForEach-Object { [uint64] $_ }
$size = [uint64] [math]::Pow(2, 32 - [int] $cidrPrefix)
$first = (($octets[0] * 16777216) + ($octets[1] * 65536) + ($octets[2] * 256) + $octets[3])
$first -= $first % $size
$last = $first + $size - 1
$outside = @('::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')   # all of IPv6; the firewall refuses ::/0
if ($first -gt 0) { $outside += "0.0.0.0-$(ConvertTo-Ipv4 ($first - 1))" }
if ($last -lt 4294967295) { $outside += "$(ConvertTo-Ipv4 ($last + 1))-255.255.255.255" }
$blockRule = @{ Direction = 'Inbound'; Action = 'Block'; Protocol = 'TCP'; LocalPort = 22; RemoteAddress = $outside; Profile = 'Any' }
if (Get-NetFirewallRule -Name 'Rehearsal-Jump-SSH-Block' -ErrorAction SilentlyContinue) {
    Set-NetFirewallRule -Name 'Rehearsal-Jump-SSH-Block' -Enabled True -InterfaceType Any @blockRule
} else {
    New-NetFirewallRule -Name 'Rehearsal-Jump-SSH-Block' -DisplayName 'Rehearsal: block SSH from outside the Hyper-V host subnet' @blockRule | Out-Null
}
Get-NetFirewallPortFilter -Protocol TCP | Where-Object { @($_.LocalPort) -contains '22' } | Get-NetFirewallRule |
    Where-Object { $_.Name -notlike 'Rehearsal-Jump-SSH-*' -and "$($_.Direction)" -eq 'Inbound' -and "$($_.Enabled)" -eq 'True' } |
    Disable-NetFirewallRule
Get-NetFirewallRule -Name 'Rehearsal-Jump-SSH-In' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name 'Rehearsal-Jump-SSH-In' -DisplayName 'Rehearsal: SSH jump from the Hyper-V host only' `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 -RemoteAddress $allowedRemote `
    -InterfaceAlias $alias -Profile $fwProfile | Out-Null

if (-not $existing) {
    # A random password nobody keeps: the account only ever signs in with its key.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 24; $rng.GetBytes($bytes)
    $pw = ConvertTo-SecureString ('Aa1-' + [Convert]::ToBase64String($bytes)) -AsPlainText -Force
    New-LocalUser -Name $jumpUser -Password $pw -PasswordNeverExpires -AccountNeverExpires `
        -Description $jumpDescription | Out-Null
    Add-LocalGroupMember -Group 'Users' -Member $jumpUser -ErrorAction SilentlyContinue
    $pw = $null
}
$sid = (Get-LocalUser -Name $jumpUser).SID.Value

# The key's own options repeat sshd_config's limits, so either alone keeps it to one forward.
$permits = ($targets | ForEach-Object { 'permitopen="' + $_ + '"' }) -join ','
$options = 'restrict,port-forwarding,' + $permits + ',no-pty,no-agent-forwarding,no-X11-forwarding,command="exit 1"'
$keys = Join-Path $sshDir "${jumpUser}_authorized_keys"
Remove-Item -Path $keys -Force -ErrorAction SilentlyContinue
Set-Content -Path $keys -Value "$options $jumpKey" -Encoding ascii
Set-ExactAcl $keys ($adminOnly + "$sid=Read")

$config = @"
# Written by build/rehearsal-azure-vmw.ps1 -JumpVia: key-only, one account, one forward, no shell.
Port 22
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitEmptyPasswords no
AllowUsers $jumpUser
AuthorizedKeysFile __PROGRAMDATA__/ssh/${jumpUser}_authorized_keys
AllowAgentForwarding no
AllowStreamLocalForwarding no
AllowTcpForwarding local
PermitOpen $($targets -join ' ')
GatewayPorts no
PermitTunnel no
PermitTTY no
X11Forwarding no
ForceCommand exit 1
"@
# A fresh install has no host keys until sshd first starts, and `sshd -t` checks them. Generate
# them directly rather than starting the server under its permissive default configuration.
if (-not (Test-Path (Join-Path $sshDir 'ssh_host_ed25519_key'))) {
    & (Join-Path (Split-Path $sshd) 'ssh-keygen.exe') -A
    if ($LASTEXITCODE -ne 0) { throw 'ssh-keygen -A failed' }
    foreach ($file in Get-ChildItem $sshDir -File -Force | Where-Object { $_.Name -like 'ssh_host_*_key' }) {
        Set-ExactAcl $file.FullName $adminOnly
    }
}
$candidate = Join-Path $sshDir 'sshd_config.candidate'
Remove-Item -Path $candidate -Force -ErrorAction SilentlyContinue
Set-Content -Path $candidate -Value $config -Encoding ascii
& $sshd -t -f $candidate
if ($LASTEXITCODE -ne 0) { throw 'the new sshd_config does not parse' }
Remove-Item -Path (Join-Path $sshDir 'sshd_config') -Force -ErrorAction SilentlyContinue
Move-Item -Path $candidate -Destination (Join-Path $sshDir 'sshd_config') -Force
Set-Service sshd -StartupType Automatic
Restart-Service sshd

"firewall=$alias/$fwProfile from $allowedRemote"
# Algorithm and key only, the form -JumpHostKey accepts. Derived from the private key sshd actually
# serves, not read from the .pub beside it, which nothing ties to that key.
$derived = & (Join-Path (Split-Path $sshd) 'ssh-keygen.exe') -y -f (Join-Path $sshDir 'ssh_host_ed25519_key')
if ($LASTEXITCODE -ne 0 -or "$derived" -notmatch '^ssh-ed25519 ') { throw 'could not derive the host public key from ssh_host_ed25519_key' }
"HOSTKEY=$((("$derived").Trim() -split '\s+')[0..1] -join ' ')"
'@

function Expand-Guest([string] $Script) {
    return $Script.Replace('__ADMIN_USER__', $AdminUser).Replace('__TUNNEL_USER__', $TunnelUser).Replace('__HL_PORT__', "$HyperloopPort").
        Replace('__DESKTOP_USER__', $DesktopUser).Replace('__ROLE__', $Role)
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
        $PSCmdlet.ShouldContinue("Delete $ResourceGroup and every resource in it (both VMs, disks, snapshots, IPs, NSGs, network)?", 'Remove the rehearsal VMs')) {
        Invoke-Az group delete -n $ResourceGroup --yes -o none | Out-Null
        Write-Step "$ResourceGroup deleted. The DPAPI password file and keys on this machine were left in place."
    }
    return
}

if (($Deallocate -or $Start -or $JumpHostSetup -or $Snapshot -or $RestoreSnapshot -or $ListSnapshots) -and -not $groupExists) {
    throw "$ResourceGroup does not exist; provision first"
}

if ($JumpHostSetup) {
    if (-not $PSCmdlet.ShouldProcess($JumpKeyPath, 'Create the jump key if missing and print the jump host setup')) { return }
    Initialize-SshKey -Path $JumpKeyPath -Comment 'agentbridge-rehearsal-jump'
    $jumpPub = (Get-Content "$JumpKeyPath.pub" -Raw).Trim()
    # The jump account may reach every rehearsal VM that exists now; rerun after creating another.
    $targets = foreach ($name in 'rehearsal-w', 'rehearsal-m') {
        if (Test-AzResource @('network', 'public-ip', 'show', '-g', $ResourceGroup, '-n', "$name-pip")) { "$(Get-VmPublicIp $name):22" }
    }
    if (-not $targets) { throw "no rehearsal VM in $ResourceGroup has a public IP yet" }
    Write-Step "run this elevated on $JumpHost, then pass its HOSTKEY to -JumpHostKey (it permits $($targets -join ', '))"
    return $JumpHostSetupScript.Replace('__JUMP_USER__', $JumpUser).Replace('__ALLOWED_REMOTE__', $JumpAllowedCidr).
        Replace('__TARGETS__', ($targets -join ' ')).Replace('__JUMP_KEY_B64__', [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($jumpPub)))
}

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
        Wait-DesktopSession
        Invoke-VmRunCommand -Script (Expand-Guest $GuestHyperloopCheck) | Write-Verbose
        $ip = Get-VmPublicIp
        if ($JumpVia) { Write-JumpSshConfig $ip }
        Write-Step "$VmName is running at $ip with the admin signed in"
        Get-TunnelCommand $ip
    }
    return
}

if ($ListSnapshots) {
    $all = @(Invoke-Az snapshot list -g $ResourceGroup -o json | ConvertFrom-Json)
    return @($all | Where-Object { (Get-SourceVm $_) -eq $VmName } | Select-Object name, timeCreated, incremental, diskSizeGB)
}

if ($Snapshot) {
    $name = "$VmName-$SnapshotName"
    if (Test-AzResource @('snapshot', 'show', '-g', $ResourceGroup, '-n', $name)) {
        throw "$name already exists; a snapshot is never overwritten. Choose another -SnapshotName."
    }
    if (-not $PSCmdlet.ShouldProcess($VmName, "Snapshot the OS disk as $name (the VM is stopped meanwhile)")) { return }
    # Deallocated first, so the snapshot holds a cleanly shut-down Windows rather than a
    # crash-consistent disk that restores into a dirty boot.
    $wasRunning = Test-VmRunning
    if ($wasRunning) {
        Write-Step 'deallocating for a consistent snapshot'
        Invoke-Az vm deallocate -g $ResourceGroup -n $VmName -o none | Out-Null
    }
    try {
        Invoke-Az snapshot create -g $ResourceGroup -n $name -l $Location --source (Get-OsDiskId) --incremental true `
            --tags @Tags "source-vm=$VmName" -o none | Out-Null
        Write-Step "$name taken"
    } finally {
        if ($wasRunning) {
            Invoke-Az vm start -g $ResourceGroup -n $VmName -o none | Out-Null
            Wait-DesktopSession
        }
    }
    return
}

if ($RestoreSnapshot) {
    $name = "$VmName-$SnapshotName"
    if (-not (Test-AzResource @('snapshot', 'show', '-g', $ResourceGroup, '-n', $name))) { throw "$name does not exist" }
    $snap = Invoke-Az snapshot show -g $ResourceGroup -n $name -o json | ConvertFrom-Json
    # The other VM's snapshot would boot as a second copy of that machine.
    if ((Get-SourceVm $snap) -ne $VmName) { throw "$name was not taken from $VmName; refusing to restore it here" }
    if (-not $PSCmdlet.ShouldProcess($VmName, "Replace the OS disk with a copy of $name; everything written since it was taken is lost")) { return }
    $oldDisk = Get-OsDiskId
    $sku = (Invoke-Az disk show --ids $oldDisk --query sku.name -o tsv).Trim()
    $newDisk = "$VmName-osdisk-$SnapshotName-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
    Write-Step "creating $newDisk from $name"
    $newId = (Invoke-Az disk create -g $ResourceGroup -n $newDisk -l $Location --source $snap.id --sku $sku `
            --tags @Tags "source-vm=$VmName" --query id -o tsv).Trim()
    Write-Step 'deallocating and swapping the OS disk'
    Invoke-Az vm deallocate -g $ResourceGroup -n $VmName -o none | Out-Null
    Invoke-Az vm update -g $ResourceGroup -n $VmName --os-disk $newId -o none | Out-Null
    Invoke-Az vm start -g $ResourceGroup -n $VmName -o none | Out-Null
    Wait-DesktopSession
    Invoke-VmRunCommand -Script (Expand-Guest $GuestHyperloopCheck) | Write-Verbose
    $oldName = Split-Path $oldDisk -Leaf
    if ($KeepReplacedDisk) {
        Write-Step "the replaced disk $oldName was kept; delete it with az disk delete when done"
    } elseif ($oldDisk -match "/resourceGroups/$([regex]::Escape($ResourceGroup))/providers/Microsoft\.Compute/disks/") {
        Invoke-Az disk delete --ids $oldDisk --yes -o none | Out-Null
        Write-Step "the replaced disk $oldName was deleted"
    } else {
        Write-Warning "the replaced disk $oldDisk is outside $ResourceGroup and was left alone"
    }
    Write-Step "$VmName restored from $name"
    return
}

# ---------------------------------------------------------------------------------------------
# Provision
# ---------------------------------------------------------------------------------------------
# The password file relies on DPAPI, which ConvertFrom-SecureString uses only on Windows.
if (-not $IsWindows) { throw 'provisioning stores the admin password with DPAPI, so it runs only on Windows' }
$prefix = [int]($AllowedSshCidr.Split('/')[1])
if ($prefix -lt 16) { throw "-AllowedSshCidr $AllowedSshCidr is wider than /16; refusing to open SSH that far" }
foreach ($keyPath in @($TunnelPublicKeyPath | Where-Object { $_ })) { if (-not (Test-Path $keyPath)) { throw "$keyPath not found" } }
if ($Role -eq 'M') {
    # VM-M joins VM-W's network and is authorized on it, so VM-W comes first.
    if (-not (Test-AzResource @('vm', 'show', '-g', $ResourceGroup, '-n', 'rehearsal-w'))) { throw 'provision VM-W (-Role W) first' }
    if (-not (Test-Path $WriterKnownHostsPath)) { throw "$WriterKnownHostsPath (VM-W's pinned host key) is missing; provision VM-W from this machine first" }
}
if ($HyperloopDirectory -and -not (Test-Path (Join-Path $HyperloopDirectory 'Hyperloop-server.exe'))) {
    throw "Hyperloop-server.exe not found in $HyperloopDirectory"
}
if (-not $PSCmdlet.ShouldProcess("$ResourceGroup/$VmName", "Provision VM-$Role")) { return }
if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }

Initialize-SshKey -Path $AdminKeyPath -Comment 'agentbridge-rehearsal-admin'
$adminPub = (Get-Content "$AdminKeyPath.pub" -Raw).Trim()
# Several keys may be authorized for VM-W's `rehearsal`: VM-M's own, and a local VM-M's.
$tunnelPub = (@($TunnelPublicKeyPath | Where-Object { $_ } | ForEach-Object { (Get-Content $_ -Raw).Trim() }) -join "`r`n")

Write-Step "resource group $ResourceGroup"
# Safe only because the tag was checked above: `group create` would retag a foreign group.
Invoke-Az group create -n $ResourceGroup -l $Location --tags @Tags -o none | Out-Null

Write-Step "network security group $VmName-nsg"
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
    --query "[?direction=='Inbound' && access=='Allow' && name!='Allow-SSH-From-Corp-Egress' && name!='Allow-SSH-From-VM-M'].name" -o tsv
if ($extra.Trim()) { throw "unexpected inbound allow rules on the NSG: $extra" }

$vmExists = Test-AzResource @('vm', 'show', '-g', $ResourceGroup, '-n', $VmName)
# The desktop user's password is the admin's on VM-W, and its own on VM-M.
$desktopSecret = if ($Role -eq 'M') { $UserSecretFile } else { $SecretFile }
foreach ($file in @($SecretFile, $desktopSecret) | Select-Object -Unique) {
    if ($vmExists -and -not (Test-Path $file)) {
        # A new password would not match the account the VM was created with, and autologon would
        # then fail at the next restart.
        throw "$VmName exists but $file does not. Restore the original DPAPI file (same Windows user and machine) or -Remove and start again."
    }
}
$password = Get-StoredPassword $SecretFile
$desktopPassword = Get-StoredPassword $desktopSecret
if (-not $vmExists) {
    $created = $false
    foreach ($size in $Sizes) {
        Write-Step "creating $VmName ($size)"
        $plain = ConvertTo-PlainText $password
        try {
            Invoke-Az vm create -g $ResourceGroup -n $VmName -l $Location --computer-name $ComputerName `
                --image $Image --size $size --security-type $SecurityType --admin-username $AdminUser `
                --admin-password $plain --license-type Windows_Client --vnet-name $VnetName --subnet default `
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
if ($JumpVia) { Write-JumpSshConfig $ip }

Write-Step "autologon for $DesktopUser"
$envelope = Invoke-VmRunCommand -Script $GuestEnvelopeKey
$pubXml = ($envelope -split "`n" | Where-Object { $_ -like 'PUBKEY=*' } | Select-Object -First 1).Substring(7).Trim()
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.FromXmlString($pubXml)
    $plainBytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-PlainText $desktopPassword))
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

if ($Role -eq 'W') {
    Write-Step 'WSL'
    $wsl = Invoke-VmAsDesktopUser -Script $GuestWslInstall
    $wsl | Write-Verbose
    if ($wsl -match 'restart-needed') { Invoke-VmRestart }

    Write-Step 'Ubuntu and Docker Engine in WSL'
    Invoke-VmAsDesktopUser -Script (Expand-Guest $GuestUbuntuDocker) -TimeoutMinutes 45 | Write-Verbose

    # Restart once more so the check below proves dockerd comes up by itself at logon.
    Invoke-VmRestart
    Start-Sleep -Seconds 60
    Write-Step 'checking Docker'
    $check = Invoke-VmAsDesktopUser -Script $GuestDockerCheck
    $check | Write-Verbose
    if ($check -notmatch 'docker-task=Running' -or $check -notmatch 'compose=\d' -or $check -notmatch 'hello-world=True' -or $check -notmatch 'windows-loopback-18123=200') {
        throw "Docker check failed: $check"
    }
} else {
    Write-Step 'Node.js LTS'
    Invoke-VmRunCommand -Script $GuestNode | Write-Verbose

    Write-Step "Copilot CLI, Claude Code and Codex for $DesktopUser (installed, not signed in)"
    $agents = Invoke-VmAsDesktopUser -Script $GuestAgents -TimeoutMinutes 20
    $agents | Write-Verbose
    foreach ($agent in 'copilot', 'claude', 'codex') {
        if ($agents -notmatch "(?m)^$agent=\S") { throw "$agent did not report a version: $agents" }
    }

    Write-Step "VM-W's Home Assistant tunnel for $DesktopUser"
    $writerIp = Get-VmPrivateIp 'rehearsal-w'
    $selfIp = Get-VmPrivateIp $VmName
    # VM-W's key as this machine pinned it from VM-W's own run-command output: never trusted on use.
    $writerKey = ((Get-Content $WriterKnownHostsPath -Raw).Trim() -split '\s+')[1..2] -join ' '
    $clientScript = (Expand-Guest $GuestVmWClient).Replace('__W_PRIVATE_IP__', $writerIp).Replace('__W_HOST_KEY__', $writerKey)
    $client = Invoke-VmAsDesktopUser -Script $clientScript
    $pubLine = ($client -split "`n" | Where-Object { $_ -like 'PUBKEY=*' } | Select-Object -First 1)
    if (-not $pubLine) { throw "VM-M did not report its tunnel key: $client" }
    $vmmPub = $pubLine.Substring(7).Trim()
    # Kept beside the password files, so a rerun of -Role W can authorize it again
    # (-TunnelPublicKeyPath takes several).
    Set-Content -Path (Join-Path $StateDir 'rehearsal-m-tunnel.pub') -Value $vmmPub -Encoding ascii
    $authorize = (Expand-Guest $GuestAuthorizeVmM).Replace('__KEY_B64__', [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($vmmPub)))
    Invoke-VmRunCommand -Name 'rehearsal-w' -Script $authorize | Write-Verbose
    # VM-W's NSG admits SSH from VM-M's private address only, inside the shared network.
    Invoke-Az network nsg rule create -g $ResourceGroup --nsg-name 'rehearsal-w-nsg' -n Allow-SSH-From-VM-M `
        --priority 110 --direction Inbound --access Allow --protocol Tcp --source-address-prefixes "$selfIp/32" `
        --source-port-ranges '*' --destination-address-prefixes '*' --destination-port-ranges 22 -o none | Out-Null
    $tunnel = Invoke-VmAsDesktopUser -Script $GuestVmWCheck
    $tunnel | Write-Verbose
    if ($tunnel -notmatch 'sftp-exit=0' -or $tunnel -notmatch 'forward-listening=True') { throw "VM-M cannot reach VM-W: $tunnel" }
    Write-Step "VM-M reaches VM-W at $writerIp ($(($tunnel -split "`n" | Where-Object { $_ -like 'home-assistant-http=*' }) -join ''))"
}

Write-Step 'checking Hyperloop'
$hyperloop = Invoke-VmRunCommand -Script (Expand-Guest $GuestHyperloopCheck)
$hyperloop | Write-Verbose
if ($hyperloop -match 'hyperloop=absent') { Write-Warning 'Hyperloop is not installed in C:\Tools yet; see the warning above' }

Write-Step "ready: $ComputerName at $ip"
Get-TunnelCommand $ip
