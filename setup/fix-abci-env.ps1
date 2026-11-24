#Requires -Version 7
<#
.SYNOPSIS
  Gives every ab-ci runner on this VM the environment its jobs need (#4104; run 45277647321 failed without it).

.DESCRIPTION
  The ab-ci runners run as abci-runner, whose jobs found no NuGet source they could reach (nuget.org is blocked from
  these VMs) and no machine-wide Python on PATH. For each runner folder under -Root (C:\ab-ci-runners\1, \2, ...) this
  sets four lines in the runner's .env, keeping every other line:
    PATH                       the machine-wide tool folders the fleet-ci runner uses (pwsh 7, Git, Node, GitHub CLI,
                               Python 3.12 and its Scripts, Windows); a folder missing on this VM is left out, with a warning
    DOTNET_INSTALL_DIR         <work folder>\_tool\dotnet, so setup-dotnet installs per runner, where the runner
                               account can write (run 45301025629: it can't write the admin-owned runner folder)
    AGENT_BRIDGE_NUGET_SOURCE  the NuGet mirror (-Mirror); ci.yml's product job restores through it on self-hosted runners
    DOTNET_CLI_TELEMETRY_OPTOUT 1
  NUGET_PACKAGES and DOTNET_CLI_HOME are not set here: each job sets its own under RUNNER_TEMP, which wins.
  Then it restarts each runner's service so the runner reads its .env again, but never one that is running a job
  (a Runner.Worker process under that folder): that runner is reported and left for a later run of this script.

  Every runner is checked before any file is written: a PATH folder the runner account could write, or a .NET
  folder outside its work folder or one it can't write (setup/abci-install-dir.ps1), refuses the whole run.

  Run elevated (the .env files and services belong to the runner account). -WhatIf shows every change and writes
  nothing; it also runs without elevation, then saying where a file can't be read. Changes nothing else on the machine.
.EXAMPLE
  pwsh -NoProfile -File setup/fix-abci-env.ps1 -WhatIf
  pwsh -NoProfile -File setup/fix-abci-env.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Root = 'C:\ab-ci-runners',
    [ValidatePattern('^https://[A-Za-z0-9.-]+/[^\s"<>&]*$')]
    [string] $Mirror = 'https://packagefeedproxy.microsoft.io/nuget/v3/index.json'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'abci-install-dir.ps1')

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated -and -not $WhatIfPreference) { throw 'Run this from an elevated PowerShell (or add -WhatIf to see the plan).' }
# Runners are found by their services (readable without elevation), each running from <Root>\<N>\bin.

$wanted = @(
    'C:\Program Files\PowerShell\7', 'C:\Program Files\Git\cmd', 'C:\Program Files\Git\bin', 'C:\Program Files\nodejs',
    'C:\Program Files\GitHub CLI', 'C:\Program Files\Python312', 'C:\Program Files\Python312\Scripts',
    'C:\Windows\System32', 'C:\Windows', 'C:\Windows\System32\WindowsPowerShell\v1.0')
$path = @($wanted | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
foreach ($gone in @($wanted | Where-Object { $_ -notin $path })) { Write-Warning "Not on this VM, left out of PATH: $gone" }

# #1294 P1: a PATH folder a non-admin can write (or redirect) lets that account plant a program every job then runs.
# Every folder must be owned by an admin principal, grant no write to anyone else, be no reparse point (nor sit under
# one), and not be per-user. Anything else refuses the whole run before a file is touched.
$adminSids = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') # Administrators, SYSTEM, TrustedInstaller
$ignoredSids = @('S-1-3-0') # CREATOR OWNER: inherit-only, applies to what an allowed writer creates
# Write, append, create, delete, change permissions or owner; and the generic write/all bits.
$writeBits = 0x2 -bor 0x4 -bor 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000
function Get-PathProblems([string]$Dir) {
    $problems = @()
    if ($Dir -match '^(?i)C:\\Users\\') { $problems += 'is per-user (under C:\Users)' }
    for ($p = $Dir; $p; $p = Split-Path -Parent $p) {
        if ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { $problems += "has a reparse point at $p" }
    }
    $acl = Get-Acl -LiteralPath $Dir
    $owner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $adminSids) { $problems += "is owned by $($acl.Owner), not an admin principal" }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or $rule.IdentityReference.Value -in $adminSids + $ignoredSids) { continue }
        if (([int64]$rule.FileSystemRights -band $writeBits) -ne 0) {
            $who = try { $rule.IdentityReference.Translate([Security.Principal.NTAccount]).Value } catch { $rule.IdentityReference.Value }
            $problems += "lets $who write ($($rule.FileSystemRights))"
        }
    }
    $problems
}
$unsafe = @(foreach ($dir in $path) { foreach ($why in @(Get-PathProblems $dir)) { "$dir $why" } })
if ($unsafe) { throw "Refusing, nothing changed: PATH folders the runner account could write or redirect:`n  $($unsafe -join "`n  ")" }
if ('C:\Program Files\Python312' -notin $path) { Write-Warning 'No machine-wide Python 3.12 here: jobs that need python will still fail.' }
$managed = '^(PATH|DOTNET_INSTALL_DIR|AGENT_BRIDGE_NUGET_SOURCE|DOTNET_CLI_TELEMETRY_OPTOUT)='

$runners = @(Get-CimInstance Win32_Service -Filter "Name like 'actions.runner.%'" | ForEach-Object {
        if ($_.PathName -and $_.PathName.Trim('"') -match ('^' + [regex]::Escape($Root) + '\\(\d+)\\bin\\')) { [pscustomobject]@{ FullName = Join-Path $Root $Matches[1]; Name = $Matches[1]; Service = $_.Name; Account = $_.StartName } }
    } | Sort-Object { [int]$_.Name })
if ($runners.Count -eq 0) { throw "No runner service runs from $Root\<N>\bin on this machine." }

# The account each runner's service runs as, and the .NET folder in its work folder that account must be able to write.
$refused = @()
foreach ($runner in $runners) {
    $account = $runner.Account -replace '^\.\\', "$env:COMPUTERNAME\"
    $sid = if ($account -eq 'LocalSystem') { 'S-1-5-18' } else { (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value }
    try {
        $work = Get-AbCiWorkFolder $runner.FullName
        $runner | Add-Member InstallDir (Get-AbCiInstallDir $work)
        foreach ($why in @(Get-AbCiInstallDirProblems $work $runner.InstallDir $sid)) { $refused += "$($runner.InstallDir) $why" }
    } catch [UnauthorizedAccessException] {
        if (-not $WhatIfPreference) { throw }
        $runner | Add-Member InstallDir -Force (Join-Path $runner.FullName '_work\_tool\dotnet')
        Write-Host "$($runner.FullName)'s work folder can't be checked without elevation; assuming $($runner.InstallDir)."
    }
}
if ($refused) { throw "Refusing, nothing changed: .NET folders the runner account couldn't install to:`n  $($refused -join "`n  ")" }

$busy = @()
foreach ($runner in $runners) {
    $dir = $runner.FullName
    $envFile = Join-Path $dir '.env'
    $keep = @()
    try {
        if (Test-Path -LiteralPath $envFile) { $keep = @(Get-Content -LiteralPath $envFile | Where-Object { $_ -notmatch $managed }) }
    } catch [UnauthorizedAccessException], [System.Management.Automation.ItemNotFoundException] {
        if (-not $WhatIfPreference) { throw }
        Write-Host "$envFile can't be read without elevation; its other lines would be kept."
    }
    $lines = $keep + @("PATH=$($path -join ';')", "DOTNET_INSTALL_DIR=$($runner.InstallDir)", "AGENT_BRIDGE_NUGET_SOURCE=$Mirror", 'DOTNET_CLI_TELEMETRY_OPTOUT=1')
    if ($PSCmdlet.ShouldProcess($envFile, "set PATH, DOTNET_INSTALL_DIR, AGENT_BRIDGE_NUGET_SOURCE and DOTNET_CLI_TELEMETRY_OPTOUT ($($keep.Count) other line(s) kept)")) {
        Set-Content -LiteralPath $envFile -Value $lines -Encoding utf8NoBOM
    }
    $service = @([pscustomobject]@{ Name = $runner.Service })
    $working = @(Get-CimInstance Win32_Process -Filter "Name = 'Runner.Worker.exe'" |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$dir\", [StringComparison]::OrdinalIgnoreCase) })
    if ($working.Count -gt 0) { $busy += $service[0].Name; Write-Warning "$($service[0].Name) is running a job: not restarted. Run this again once it is idle."; continue }
    if ($PSCmdlet.ShouldProcess($service[0].Name, 'restart, so the runner reads its .env')) { Restart-Service -Name $service[0].Name }
}
if ($busy) { Write-Host "Not restarted (busy): $($busy -join ', ')" } else { Write-Host "Done: $($runners.Count) runner(s) under $Root." }
