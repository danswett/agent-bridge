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
  folder that isn't inside its work folder (one plain name in the runner folder), that crosses a reparse point,
  that anyone but an admin or that runner's own account could write (up to -Root), or that the runner account can't
  write (setup/abci-install-dir.ps1), refuses the whole run.

  Note: a runner root that inherits C:\'s default rights (Users may create folders, Authenticated Users modify) is
  refused as "writable by" those groups; its ACL must be fixed first (no inherited write for non-admins).

  A value with a newline, ';' or '=' (which would end or split its .env line) refuses the run too. Each runner's
  .NET folder is printed, also under -WhatIf.

  Run elevated (the .env files and services belong to the runner account). -WhatIf shows every change and writes
  nothing; it also runs without elevation, then saying where a file can't be read. Changes nothing else on the machine.

  Tests only (AbCiEnvScriptTests): with AGENT_BRIDGE_ABCI_TEST=1, a banner says so and -Root must resolve to a folder
  under the temp folder, by no device or UNC form and through no reparse point; its numbered folders are the runners, AGENT_BRIDGE_ABCI_TEST_ACCOUNT (a SID) their account, the caller counts as an
  admin, AGENT_BRIDGE_ABCI_TEST_PATH ('|'-separated) replaces the tool folders, and no service is touched. Without
  the flag, any -Root but C:\ab-ci-runners is refused.
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

$realRoot = 'C:\ab-ci-runners'
$testMode = $env:AGENT_BRIDGE_ABCI_TEST -eq '1'
# No device (\\?\, \\.\) or UNC form: each is another spelling of a path that a string compare would miss.
if ($Root -match '^[\\/]{2}') { throw "-Root must be a plain drive path, not a device or UNC path." }
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($testMode) {
    Write-Warning '*** TEST SEAM ON (AGENT_BRIDGE_ABCI_TEST=1): fake runner root under the temp folder, no services touched. Never set this on a VM. ***'
    # The seam takes only a folder under this user's temp folder, reached through no reparse point.
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ($temp -match '^[\\/]{2}' -or -not $Root.StartsWith("$temp\", [StringComparison]::OrdinalIgnoreCase)) { throw "AGENT_BRIDGE_ABCI_TEST is set: -Root must be under the temp folder $temp." }
    for ($p = $Root; $p.Length -gt $temp.Length; $p = Split-Path -Parent $p) {
        if ((Test-Path -LiteralPath $p) -and (Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "AGENT_BRIDGE_ABCI_TEST is set: -Root crosses a reparse point at $p." }
    }
} elseif ($Root -ne $realRoot) { throw "-Root other than $realRoot is for tests only." }

# Under the seam, AGENT_BRIDGE_ABCI_TEST_ELEVATED stands in for the token, so the check itself is tested.
$elevated = if ($testMode) { $env:AGENT_BRIDGE_ABCI_TEST_ELEVATED -eq '1' } else {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
if (-not $elevated -and -not $WhatIfPreference) { throw 'Run this from an elevated PowerShell (or add -WhatIf to see the plan).' }

# A .env line is NAME=value up to the line end; the runner splits PATH on ';'.
function Test-EnvValue([string]$Value) { $Value -notmatch '[\r\n;=]' }
# Runners are found by their services (readable without elevation), each running from <Root>\<N>\bin.

$wanted = @(
    'C:\Program Files\PowerShell\7', 'C:\Program Files\Git\cmd', 'C:\Program Files\Git\bin', 'C:\Program Files\nodejs',
    'C:\Program Files\GitHub CLI', 'C:\Program Files\Python312', 'C:\Program Files\Python312\Scripts',
    'C:\Windows\System32', 'C:\Windows', 'C:\Windows\System32\WindowsPowerShell\v1.0')
if ($testMode) { $wanted = @("$env:AGENT_BRIDGE_ABCI_TEST_PATH" -split '\|' | Where-Object { $_ }) }
$path = @($wanted | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
foreach ($gone in @($wanted | Where-Object { $_ -notin $path })) { Write-Warning "Not on this VM, left out of PATH: $gone" }
$badValues = @(@($path) + $Mirror | Where-Object { -not (Test-EnvValue $_) })
if ($badValues) { throw "Refusing, nothing changed: .env values can't hold a newline, ';' or '=':`n  $($badValues -join "`n  ")" }

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

$runners = @(if ($testMode) {
    @(Get-ChildItem -LiteralPath $Root -Directory | Where-Object Name -match '^\d+$' | ForEach-Object {
            [pscustomobject]@{ FullName = $_.FullName; Name = $_.Name; Service = "test-runner-$($_.Name)"; Account = $null } } | Sort-Object { [int]$_.Name })
} else {
    @(Get-CimInstance Win32_Service -Filter "Name like 'actions.runner.%'" | ForEach-Object {
            if ($_.PathName -and $_.PathName.Trim('"') -match ('^' + [regex]::Escape($Root) + '\\(\d+)\\bin\\')) { [pscustomobject]@{ FullName = Join-Path $Root $Matches[1]; Name = $Matches[1]; Service = $_.Name; Account = $_.StartName } }
        } | Sort-Object { [int]$_.Name })
})
if ($runners.Count -eq 0) { Write-Host "No runners found: no runner service runs from $Root\<N>\bin. Nothing changed."; exit 1 }

# The account each runner's service runs as, and the .NET folder in its work folder that account must be able to write.
$refused = @()
foreach ($runner in $runners) {
    $installAdmins = $script:AbCiAdminSids
    if ($testMode) {
        $sid = $env:AGENT_BRIDGE_ABCI_TEST_ACCOUNT
        $installAdmins += [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    } else {
        $account = $runner.Account -replace '^\.\\', "$env:COMPUTERNAME\"
        $sid = if ($account -eq 'LocalSystem') { 'S-1-5-18' } else { (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value }
    }
    try {
        $runner | Add-Member InstallDir (Get-AbCiInstallDir (Get-AbCiWorkFolder $runner.FullName))
        Write-Host "Runner $($runner.Name): DOTNET_INSTALL_DIR=$($runner.InstallDir)"
        if (-not (Test-EnvValue $runner.InstallDir)) { $refused += "$($runner.InstallDir) can't be an .env value: it holds a newline, ';' or '='"; continue }
        foreach ($why in @(Get-AbCiInstallDirProblems $runner.FullName $runner.InstallDir $sid $installAdmins)) { $refused += "$($runner.InstallDir) $why" }
    } catch [UnauthorizedAccessException] {
        if (-not $WhatIfPreference) { throw }
        $runner | Add-Member InstallDir -Force (Join-Path $runner.FullName '_work\_tool\dotnet')
        Write-Host "$($runner.FullName)'s work folder can't be checked without elevation; assuming $($runner.InstallDir)."
    } catch {
        $refused += "$($runner.FullName): $($_.Exception.Message)"
    }
}
if ($refused) { throw "Refusing, nothing changed: .NET folders the runner account couldn't install to:`n  $($refused -join "`n  ")" }

# All or nothing: every .env is read and written to <.env>.new first; only when every one is written are they moved
# into place, and only then is any service restarted. A failure before the moves changes no .env.
$plans = foreach ($runner in $runners) {
    $envFile = Join-Path $runner.FullName '.env'
    $keep = @()
    try {
        if (Test-Path -LiteralPath $envFile) { $keep = @(Get-Content -LiteralPath $envFile | Where-Object { $_ -notmatch $managed }) }
    } catch [UnauthorizedAccessException], [System.Management.Automation.ItemNotFoundException] {
        if (-not $WhatIfPreference) { throw }
        Write-Host "$envFile can't be read without elevation; its other lines would be kept."
    }
    [pscustomobject]@{ Runner = $runner; File = $envFile; Keep = $keep.Count
        Lines = $keep + @("PATH=$($path -join ';')", "DOTNET_INSTALL_DIR=$($runner.InstallDir)", "AGENT_BRIDGE_NUGET_SOURCE=$Mirror", 'DOTNET_CLI_TELEMETRY_OPTOUT=1') }
}
$staged = @()
try {
    foreach ($plan in $plans) {
        if ($PSCmdlet.ShouldProcess($plan.File, "set PATH, DOTNET_INSTALL_DIR, AGENT_BRIDGE_NUGET_SOURCE and DOTNET_CLI_TELEMETRY_OPTOUT ($($plan.Keep) other line(s) kept)")) {
            Set-Content -LiteralPath "$($plan.File).new" -Value $plan.Lines -Encoding utf8NoBOM
            $staged += $plan
        }
    }
} catch {
    foreach ($plan in $staged) { Remove-Item -LiteralPath "$($plan.File).new" -Force -ErrorAction SilentlyContinue }
    throw "Refusing, nothing changed: a .env couldn't be written ($($_.Exception.Message))."
}
foreach ($plan in $staged) { Move-Item -LiteralPath "$($plan.File).new" -Destination $plan.File -Force; Write-Host "Wrote $($plan.File)" }

$busy = @()
# Under -WhatIf nothing was staged: show the restarts the plan would make.
foreach ($plan in $(if ($WhatIfPreference) { $plans } else { $staged })) {
    $name = $plan.Runner.Service
    $working = @(Get-CimInstance Win32_Process -Filter "Name = 'Runner.Worker.exe'" |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$($plan.Runner.FullName)\", [StringComparison]::OrdinalIgnoreCase) })
    if ($working.Count -gt 0) { $busy += $name; Write-Warning "$name is running a job: not restarted. Run this again once it is idle."; continue }
    if ($testMode) { Write-Host "Restarted (test, no service touched): $name"; continue }
    if ($PSCmdlet.ShouldProcess($name, 'restart, so the runner reads its .env')) { Restart-Service -Name $name }
}
if ($busy) { Write-Host "Not restarted (busy): $($busy -join ', ')" } else { Write-Host "Done: $($runners.Count) runner(s) under $Root." }