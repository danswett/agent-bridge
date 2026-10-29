<#
.SYNOPSIS
Deploys one relay stamp (infra/main.bicep) to a region, idempotently.

.DESCRIPTION
Two passes. The first creates or adopts the shared resources and each app's identity and
Key Vault. Missing secrets are then seeded: staging gets fresh keys, and production copies
the keys the live app signs with, so existing account tokens and push subscriptions keep
working. The second pass defines the apps. Images are never rolled from here: each app
keeps the image it runs now (relay.yml rolls images), unless one is given explicitly.

Production is left alone unless -Production is given. That is the cutover (docs/relay-deploy.md).

.EXAMPLE
./infra/deploy.ps1 -WhatIf
./infra/deploy.ps1
./infra/deploy.ps1 -Production -ProductionImage agentbridgerelay59731.azurecr.io/agent-bridge-relay@sha256:... -ProductionVersion <sha>
#>
[CmdletBinding()]
param(
    [string]$Region = 'westus2',
    [string]$ResourceGroup = 'rg-agent-bridge-relay',
    # Define production from infra/ too: the cutover. Without it production is untouched.
    [switch]$Production,
    [string]$ProductionImage,
    [string]$ProductionVersion,
    # Only needed while staging does not exist yet.
    [string]$StagingImage,
    [string]$StagingVersion,
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$paramFile = Join-Path $PSScriptRoot "regions/$Region.bicepparam"
$template = Join-Path $PSScriptRoot 'main.bicep'
if (-not (Test-Path $paramFile)) { throw "no parameter file for region $Region ($paramFile)" }

function Get-AppJson([string]$name) {
    $json = az containerapp list -g $ResourceGroup --query "[?name=='$name'] | [0]" -o json
    if (-not $json -or $json -eq 'null') { return $null }
    return $json | ConvertFrom-Json
}

function Get-AppVersion($app) {
    $envVars = @($app.properties.template.containers[0].env)
    $v = $envVars | Where-Object { $_.name -eq 'Relay__Version' } | Select-Object -First 1
    if ($v) { return [string]$v.value }
    return ''
}

function Get-ParamValue([string]$name) {
    $built = az bicep build-params -f $paramFile --stdout | ConvertFrom-Json
    return ($built.parametersJson | ConvertFrom-Json).parameters.$name.value
}

# What each app runs now, so this deploy re-states it rather than rolling it.
$stagingCfg = Get-ParamValue 'staging'
$productionCfg = Get-ParamValue 'production'
$stagingApp = Get-AppJson $stagingCfg.name
if (-not $StagingImage) {
    if (-not $stagingApp) { throw "staging ($($stagingCfg.name)) does not exist yet: pass -StagingImage and -StagingVersion for its first revision" }
    $StagingImage = $stagingApp.properties.template.containers[0].image
    $StagingVersion = Get-AppVersion $stagingApp
}
if ($Production) {
    if (-not $ProductionImage -or -not $ProductionVersion) { throw '-Production needs -ProductionImage and -ProductionVersion: the build staging has passed' }
} else {
    $ProductionImage = ''
    $ProductionVersion = ''
}

$env:RELAY_STAGING_IMAGE = $StagingImage
$env:RELAY_STAGING_VERSION = $StagingVersion
$env:RELAY_PRODUCTION_IMAGE = $ProductionImage
$env:RELAY_PRODUCTION_VERSION = $ProductionVersion
$env:RELAY_DEPLOY_PRODUCTION = if ($Production) { 'true' } else { 'false' }
$env:RELAY_OPERATOR = az ad signed-in-user show --query id -o tsv
foreach ($kind in 'staging', 'production') {
    $var = "RELAY_$($kind.ToUpperInvariant())_DEPLOYER"
    if (-not (Get-Item "env:$var" -ErrorAction SilentlyContinue)) {
        $sp = az ad sp list --display-name "agent-bridge-relay-deploy-$kind" --query '[0].id' -o tsv
        Set-Item "env:$var" $sp
    }
}

if ($WhatIf) {
    $env:RELAY_DEPLOY_APPS = 'true'
    az deployment group what-if -g $ResourceGroup -f $template -p $paramFile
    return
}

Write-Host "pass 1: shared resources, identities, vaults and role assignments"
$env:RELAY_DEPLOY_APPS = 'false'
$out = az deployment group create -g $ResourceGroup -n "relay-stamp-base-$Region" -f $template -p $paramFile --query properties.outputs -o json | ConvertFrom-Json

# Data-plane calls go straight to Key Vault's REST API, so no secret is ever on a command line.
$vaultToken = az account get-access-token --resource https://vault.azure.net --query accessToken -o tsv
function Test-VaultSecret([string]$vault, [string]$name) {
    try {
        Invoke-RestMethod -Uri "https://$vault.vault.azure.net/secrets/$name/?api-version=7.4" -Headers @{ Authorization = "Bearer $vaultToken" } | Out-Null
        return $true
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 404) { return $false }
        throw
    }
}
function Set-VaultSecret([string]$vault, [string]$name, [string]$value) {
    $body = @{ value = $value; contentType = 'base64' } | ConvertTo-Json
    # A fresh role assignment takes a minute or two to reach Key Vault.
    for ($i = 0; ; $i++) {
        try {
            Invoke-RestMethod -Method Put -Uri "https://$vault.vault.azure.net/secrets/$name/?api-version=7.4" -Headers @{ Authorization = "Bearer $vaultToken" } -ContentType 'application/json' -Body $body | Out-Null
            return
        } catch {
            if ($i -ge 12 -or $_.Exception.Response.StatusCode.value__ -ne 403) { throw }
            Start-Sleep -Seconds 15
        }
    }
}
function New-TokenKey { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
function New-VapidKey {
    $k = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try { [Convert]::ToBase64String($k.ExportPkcs8PrivateKey()) } finally { $k.Dispose() }
}

# The live app's secret behind one of its Relay__ settings, if it has one.
function Get-LiveSecret($app, [string]$setting) {
    $ref = @($app.properties.template.containers[0].env) | Where-Object { $_.name -eq $setting -and $_.PSObject.Properties['secretRef'] } | Select-Object -First 1
    if (-not $ref) { return $null }
    return az containerapp secret show -g $ResourceGroup -n $app.name --secret-name $ref.secretRef --query value -o tsv
}

foreach ($name in 'token-key', 'vapid-key') {
    if (-not (Test-VaultSecret $out.stagingVault.value $name)) {
        Write-Host "seeding staging $name"
        Set-VaultSecret $out.stagingVault.value $name $(if ($name -eq 'token-key') { New-TokenKey } else { New-VapidKey })
    }
}

# Production's keys must be the ones it already signs with: a new token key signs every host
# and phone out (hosts exit on 4001), and a new VAPID key orphans every push subscription.
$liveProduction = Get-AppJson $productionCfg.name
foreach ($pair in @(@('token-key', 'Relay__TokenKey'), @('vapid-key', 'Relay__VapidPrivateKey'))) {
    if (Test-VaultSecret $out.productionVault.value $pair[0]) { continue }
    $value = if ($liveProduction) { Get-LiveSecret $liveProduction $pair[1] } else { $null }
    if (-not $value) {
        if ($liveProduction) { throw "the live production app has no $($pair[1]) secret to carry over; see docs/relay-deploy.md before generating one" }
        Write-Host "seeding production $($pair[0]) (new stamp: no live app)"
        $value = if ($pair[0] -eq 'token-key') { New-TokenKey } else { New-VapidKey }
    } else {
        Write-Host "carrying production $($pair[0]) over from the live app"
    }
    Set-VaultSecret $out.productionVault.value $pair[0] $value
}

Write-Host "pass 2: apps$(if ($Production) { ' (including production: the cutover)' } else { ' (staging only)' })"
$env:RELAY_DEPLOY_APPS = 'true'
$out = az deployment group create -g $ResourceGroup -n "relay-stamp-$Region" -f $template -p $paramFile --query properties.outputs -o json | ConvertFrom-Json
Write-Host "staging:    https://$($out.stagingFqdn.value)"
if ($Production) { Write-Host "production: https://$($out.productionFqdn.value)" }
