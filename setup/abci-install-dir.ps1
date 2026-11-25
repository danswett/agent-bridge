# Where an ab-ci runner's jobs install .NET (DOTNET_INSTALL_DIR), for fix-abci-env.ps1 (#4104). Dot-sourced; defines
# functions only. Run 45301025629 failed at setup-dotnet with no write access to <runner>\.dotnet: the runner folder is
# admin-owned, and the runner account writes only its work folder. So .NET goes where the runner keeps its other
# tools, <runner>\<work folder>\_tool\dotnet, and anything else is refused before a file is touched.

# Administrators, SYSTEM, TrustedInstaller.
$script:AbCiAdminSids = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
# Write, append (create folder), delete child, delete, change permissions or owner; generic write/all.
$script:AbCiWriteBits = 0x2 -bor 0x4 -bor 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000

# The runner's work folder: .runner's workFolder, else _work. It must be one plain folder name inside the runner
# folder: no separator, '..', drive, UNC or device prefix, no trailing dot or space, no reserved device name.
function Get-AbCiWorkFolder([string]$RunnerDir) {
    $name = '_work'
    $settings = Join-Path $RunnerDir '.runner'
    if (Test-Path -LiteralPath $settings) {
        $configured = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).PSObject.Properties['workFolder']
        if ($configured -and $null -ne $configured.Value -and "$($configured.Value)" -ne '') { $name = [string]$configured.Value }
    }
    if ($name -notmatch '^[A-Za-z0-9_-][A-Za-z0-9_.-]*$' -or $name.EndsWith('.') -or
        $name -match '^(?i)(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9]|LPT[0-9])(\.|$)') {
        throw "$settings names a work folder that isn't one plain folder name in the runner folder."
    }
    $runner = [IO.Path]::GetFullPath($RunnerDir).TrimEnd('\', '/')
    $work = [IO.Path]::GetFullPath((Join-Path $runner $name))
    if ((Split-Path -Parent $work) -ne $runner) { throw "$settings names a work folder outside the runner folder." }
    $work
}

function Get-AbCiInstallDir([string]$WorkFolder) { Join-Path (Join-Path $WorkFolder '_tool') 'dotnet' }

# Why the runner account ($AccountSid) could not safely install .NET at $InstallDir, or nothing. The canonical
# install dir must be inside the runner's work folder; no folder from it up to the runner root (the runner folder's
# parent) may be a reparse point; none may be owned by, or let anyone but an admin ($AdminSids) or the runner account
# write (a writer there could swap the .NET every job runs); and the nearest folder that exists must give the runner
# account itself write, inherited by the folders setup-dotnet creates.
function Get-AbCiInstallDirProblems([string]$RunnerDir, [string]$InstallDir, [string]$AccountSid, [string[]]$AdminSids = $script:AbCiAdminSids) {
    $work = Get-AbCiWorkFolder $RunnerDir
    $target = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\', '/')
    if (-not $target.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        return "is outside the runner's work folder $work, the only folder the runner account writes"
    }
    if (-not (Test-Path -LiteralPath $work -PathType Container)) { return "has no work folder: $work doesn't exist" }
    $root = Split-Path -Parent ([IO.Path]::GetFullPath($RunnerDir).TrimEnd('\', '/'))
    $problems = @()
    $nearest = $null
    for ($p = $target; $p.Length -ge $root.Length; $p = Split-Path -Parent $p) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        if (-not $nearest) { $nearest = $p }
        if ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { $problems += "has a reparse point at $p" }
        $acl = Get-Acl -LiteralPath $p
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $AdminSids + $AccountSid) { $problems += "has $p owned by $owner, neither an admin nor the runner account" }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            $who = $rule.IdentityReference.Value
            # CREATOR OWNER applies only to what an allowed writer creates.
            if ($rule.AccessControlType -ne 'Allow' -or $who -in $AdminSids + $AccountSid + 'S-1-3-0') { continue }
            if (([int64]$rule.FileSystemRights -band $script:AbCiWriteBits) -ne 0) { $problems += "has $p writable by $who ($($rule.FileSystemRights))" }
        }
        if ($p -eq $root) { break }
    }
    $rules = @((Get-Acl -LiteralPath $nearest).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.IdentityReference.Value -eq $AccountSid })
    $grants = @($rules | Where-Object {
            $_.AccessControlType -eq 'Allow' -and ([int64]$_.FileSystemRights -band $script:AbCiWriteBits) -ne 0 -and
            -not ($_.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            ($nearest -eq $target -or ($_.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ContainerInherit))
        })
    $denied = @($rules | Where-Object { $_.AccessControlType -eq 'Deny' -and ([int64]$_.FileSystemRights -band $script:AbCiWriteBits) -ne 0 })
    if ($grants.Count -eq 0 -or $denied.Count -gt 0) {
        $problems += "can't be written by the runner account ($AccountSid): $nearest gives it no write that its new folders inherit"
    }
    $problems
}
