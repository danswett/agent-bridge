<#
.SYNOPSIS
    Names the az sign-in a rehearsal belongs to, and says which account that is.
.DESCRIPTION
    The rehearsal is run by agents, so there is no owner step. The account is the one `az` is already signed in as
    (az account show, in -AzureConfigDir). AGENT_BRIDGE_REHEARSAL_AZURE_USER is an optional override that wins when set.
    With no az sign-in and no override it stops with a plain message. Prints the account to the host; returns it.
#>
[CmdletBinding()]
param([string] $AzureConfigDir = (Join-Path $HOME '.azure-agentbridge'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:AGENT_BRIDGE_REHEARSAL_AZURE_USER) {
    Write-Host "Rehearsal az account: $env:AGENT_BRIDGE_REHEARSAL_AZURE_USER (from AGENT_BRIDGE_REHEARSAL_AZURE_USER)"
    return $env:AGENT_BRIDGE_REHEARSAL_AZURE_USER
}
$env:AZURE_CONFIG_DIR = $AzureConfigDir
$signedIn = ''
try { $signedIn = "$(az account show --query user.name -o tsv 2>$null)".Trim() } catch { $signedIn = '' }
if ($LASTEXITCODE -ne 0 -or -not $signedIn) {
    throw "az is not signed in (AZURE_CONFIG_DIR=$AzureConfigDir), so there is no account to run the rehearsal as. Run az login with that profile, then try again. Nothing was started or stopped."
}
Write-Host "Rehearsal az account: $signedIn (the account az is signed in as)"
return $signedIn
