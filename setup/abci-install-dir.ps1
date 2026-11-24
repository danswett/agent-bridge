# Where an ab-ci runner's jobs install .NET (DOTNET_INSTALL_DIR), for fix-abci-env.ps1 (#4104). Dot-sourced; defines
# functions only. Run 45301025629 failed at setup-dotnet with no write access to <runner>\.dotnet: the runner folder is
# admin-owned, and the runner account writes only its work folder. So .NET goes where the runner keeps its other
# tools, <work folder>\_tool\dotnet, and anything else is refused before a file is touched.

# The runner's work folder: .runner's workFolder (relative to the runner folder unless rooted), else _work.
function Get-AbCiWorkFolder([string]$RunnerDir) {
    $work = '_work'
    $settings = Join-Path $RunnerDir '.runner'
    if (Test-Path -LiteralPath $settings) {
        $configured = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).PSObject.Properties['workFolder']
        if ($configured -and $configured.Value) { $work = [string]$configured.Value }
    }
    [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($work)) { $work } else { Join-Path $RunnerDir $work }))
}

function Get-AbCiInstallDir([string]$WorkFolder) { Join-Path (Join-Path $WorkFolder '_tool') 'dotnet' }

# Why the runner account ($AccountSid) could not install .NET at $InstallDir, or nothing. It must be inside the work
# folder (no '..' out of it), with no reparse point on the way (one could send the install anywhere), and the nearest
# folder that exists must give the account itself write, inherited by the folders setup-dotnet creates under it.
function Get-AbCiInstallDirProblems([string]$WorkFolder, [string]$InstallDir, [string]$AccountSid) {
    $work = [IO.Path]::GetFullPath($WorkFolder).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\', '/')
    if (-not $target.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        return "is outside the runner's work folder $work, the only folder the runner account writes"
    }
    if (-not (Test-Path -LiteralPath $work -PathType Container)) { return "has no work folder: $work doesn't exist" }
    $problems = @()
    $nearest = $null
    for ($p = $target; $p; $p = Split-Path -Parent $p) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        if (-not $nearest) { $nearest = $p }
        if ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { $problems += "has a reparse point at $p" }
    }
    # Write, append (create folder), delete; or generic write/all.
    $writeBits = 0x2 -bor 0x4 -bor 0x10000 -bor 0x40000000 -bor 0x10000000
    $grants = @((Get-Acl -LiteralPath $nearest).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -eq $AccountSid -and ([int64]$_.FileSystemRights -band $writeBits) -ne 0 -and
            -not ($_.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            ($nearest -eq $target -or ($_.InheritanceFlags -band [Security.AccessControl.InheritanceFlags]::ContainerInherit))
        })
    $denied = @((Get-Acl -LiteralPath $nearest).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq 'Deny' -and $_.IdentityReference.Value -eq $AccountSid -and ([int64]$_.FileSystemRights -band $writeBits) -ne 0 })
    if ($grants.Count -eq 0 -or $denied.Count -gt 0) {
        $problems += "can't be written by the runner account ($AccountSid): $nearest gives it no write that its new folders inherit"
    }
    $problems
}
