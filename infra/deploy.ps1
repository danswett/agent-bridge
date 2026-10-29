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

An app with a customDomain in the parameter file gets it in steps, as DNS allows: nothing
until the CNAME and asuid TXT records exist (the script prints both), then the hostname on
the app, then an Azure-managed certificate bound with SNI.

.EXAMPLE
./infra/deploy.ps1 -WhatIf
./infra/deploy.ps1
./infra/deploy.ps1 -Production -ProductionImage abrelaywus2.azurecr.io/agent-bridge-relay@sha256:... -ProductionVersion <sha>
./infra/deploy.ps1 -BaseOnly -SeedProductionFrom kv-abrelay-wus2   # a new stamp taking over another's keys
#>
[CmdletBinding()]
param(
    [string]$Region = 'westus2',
    # The stamp's subscription ("Visual Studio Enterprise Subscription"). Every call names it,
    # so the CLI's default subscription never decides where anything goes.
    [string]$Subscription = '0e38f344-908e-4ff3-88ac-4d499a1f51f8',
    [string]$ResourceGroup = 'rg-agent-bridge-relay',
    # Define production from infra/ too: the cutover. Without it production is untouched.
    [switch]$Production,
    [string]$ProductionImage,
    [string]$ProductionVersion,
    # Only needed while staging does not exist yet.
    [string]$StagingImage,
    [string]$StagingVersion,
    # Production's keys for a new stamp come from this vault (another stamp's, any
    # subscription), so its account tokens and push subscriptions stay valid here.
    [string]$SeedProductionFrom,
    # Shared resources, identities, vaults and keys only; no apps. For a new stamp whose
    # registry has to receive an image before the apps can start.
    [switch]$BaseOnly,
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$paramFile = Join-Path $PSScriptRoot "regions/$Region.bicepparam"
$template = Join-Path $PSScriptRoot 'main.bicep'
if (-not (Test-Path $paramFile)) { throw "no parameter file for region $Region ($paramFile)" }

function Get-AppJson([string]$name) {
    $json = az containerapp list --subscription $Subscription -g $ResourceGroup --query "[?name=='$name'] | [0]" -o json
    if (-not $json -or $json -eq 'null') { return $null }
    return $json | ConvertFrom-Json
}

function Get-AppVersion($app) {
    $envVars = @($app.properties.template.containers[0].env)
    $v = $envVars | Where-Object { $_.name -eq 'Relay__Version' } | Select-Object -First 1
    if ($v) { return [string]$v.value }
    return ''
}

$script:builtParams = $null
function Get-ParamValue([string]$name) {
    if (-not $script:builtParams) { $script:builtParams = (az bicep build-params -f $paramFile --stdout | ConvertFrom-Json).parametersJson | ConvertFrom-Json }
    return $script:builtParams.parameters.$name.value
}

# What each app runs now, so this deploy re-states it rather than rolling it.
$stagingCfg = Get-ParamValue 'staging'
$productionCfg = Get-ParamValue 'production'
$stagingApp = Get-AppJson $stagingCfg.name
if (-not $StagingImage -and -not $BaseOnly) {
    if (-not $stagingApp) { throw "staging ($($stagingCfg.name)) does not exist yet: pass -StagingImage and -StagingVersion for its first revision" }
    $StagingImage = $stagingApp.properties.template.containers[0].image
    $StagingVersion = Get-AppVersion $stagingApp
}
# Adopting means the very resources production runs on: a different environment would give
# the app a new URL. So when production exists, its environment is the stamp's, whatever the
# parameter file's defaults (for a new stamp) say, and that environment is left exactly as it
# is until the cutover: updating it is updating what production runs on. Then it also starts
# sending logs to the stamp's workspace (the hand-made one sends them nowhere).
$liveProduction = Get-AppJson $productionCfg.name
$env:RELAY_MANAGE_ENVIRONMENT = 'true'
if ($liveProduction) {
    $liveEnv = az containerapp env show --subscription $Subscription --ids $liveProduction.properties.managedEnvironmentId -o json | ConvertFrom-Json
    if ($liveEnv.resourceGroup -ne $ResourceGroup) { throw "$($productionCfg.name) runs in environment $($liveEnv.name) in resource group $($liveEnv.resourceGroup), not $ResourceGroup" }
    $env:RELAY_ENVIRONMENT_NAME = $liveEnv.name
    $env:RELAY_MANAGE_ENVIRONMENT = if ($Production) { 'true' } else { 'false' }
    $logs = $liveEnv.properties.appLogsConfiguration
    $customerId = if ($logs -and $logs.logAnalyticsConfiguration) { $logs.logAnalyticsConfiguration.customerId } else { $null }
    if ($customerId) {
        $workspace = az monitor log-analytics workspace list --subscription $Subscription -g $ResourceGroup --query "[?customerId=='$customerId'].name | [0]" -o tsv
        if (-not $workspace) { throw "environment $($liveEnv.name) logs to a workspace outside $ResourceGroup ($customerId)" }
        $env:RELAY_LOG_ANALYTICS_NAME = $workspace
    }
    $script:builtParams = $null
    Write-Host "adopting environment $($liveEnv.name)$(if ($env:RELAY_MANAGE_ENVIRONMENT -eq 'false') { ' (left unchanged until the cutover)' })"
}
if ($Production -and -not $BaseOnly) {
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

# Custom domains go in steps (see the help above): what is in DNS and whether a managed
# certificate already exists decide the step, never a flag a person has to remember.
function Get-DnsAnswer([string]$name, [string]$type) {
    $r = Invoke-RestMethod -Uri "https://cloudflare-dns.com/dns-query?name=$name&type=$type" -Headers @{ accept = 'application/dns-json' }
    if ($r.PSObject.Properties['Answer']) { return @($r.Answer | ForEach-Object { ([string]$_.data).Trim('"').TrimEnd('.') }) }
    return @()
}
function Get-DomainState($cfg) {
    $domain = if ($cfg.PSObject.Properties['customDomain']) { [string]$cfg.customDomain } else { '' }
    if (-not $domain) { return 'none' }
    $envName = Get-ParamValue 'environmentName'
    $envJson = az containerapp env list --subscription $Subscription -g $ResourceGroup --query "[?name=='$envName'] | [0]" -o json
    if (-not $envJson -or $envJson -eq 'null') { Write-Host "${domain}: no environment yet; the domain is added on a later run"; return 'none' }
    $stampEnv = $envJson | ConvertFrom-Json
    $cert = az containerapp env certificate list --subscription $Subscription -g $ResourceGroup -n $envName --managed-certificates-only --query "[?properties.subjectName=='$domain'] | [0].properties.provisioningState" -o tsv
    if ($cert -eq 'Succeeded') { return 'bound' }
    $fqdn = "$($cfg.name).$($stampEnv.properties.defaultDomain)"
    $verification = $stampEnv.properties.customDomainConfiguration.customDomainVerificationId
    if ((Get-DnsAnswer $domain 'CNAME') -contains $fqdn -and (Get-DnsAnswer "asuid.$domain" 'TXT') -contains $verification) { return 'ready' }
    Write-Host "${domain} needs these DNS records first (DNS only, not proxied: the certificate is validated against the CNAME):"
    Write-Host "  CNAME $domain -> $fqdn"
    Write-Host "  TXT   asuid.$domain = $verification"
    return 'none'
}
function Set-DomainStep([string]$productionState, [string]$stagingState) {
    $env:RELAY_PRODUCTION_DOMAIN_STATE = if ($productionState -eq 'ready') { 'added' } else { $productionState }
    $env:RELAY_STAGING_DOMAIN_STATE = if ($stagingState -eq 'ready') { 'added' } else { $stagingState }
}
$productionDomain = if ($Production) { Get-DomainState $productionCfg } else { 'none' }
$stagingDomain = Get-DomainState $stagingCfg
Set-DomainStep $productionDomain $stagingDomain

if ($WhatIf) {
    # The preview of the deployment actually requested: -BaseOnly never reaches the apps.
    $env:RELAY_DEPLOY_APPS = if ($BaseOnly) { 'false' } else { 'true' }
    az deployment group what-if --subscription $Subscription -g $ResourceGroup -f $template -p $paramFile
    return
}

Write-Host "pass 1: shared resources, identities, vaults and role assignments"
$env:RELAY_DEPLOY_APPS = 'false'
$out = az deployment group create --subscription $Subscription -g $ResourceGroup -n "relay-stamp-base-$Region" -f $template -p $paramFile --query properties.outputs -o json | ConvertFrom-Json

# Data-plane calls go straight to Key Vault's REST API, so no secret is ever on a command line.
$vaultHeaders = @{ Authorization = ('Bearer', (az account get-access-token --resource https://vault.azure.net --query accessToken -o tsv)) -join ' ' }

# A role assignment made by pass 1 takes a minute or two to reach Key Vault, so a 403 is
# retried for a while; anything else is an answer (404: no such secret) or an error.
function Invoke-Vault([string]$method, [string]$uri, [string]$body) {
    for ($i = 0; ; $i++) {
        try {
            if ($body) { return Invoke-RestMethod -Method $method -Uri $uri -Headers $vaultHeaders -ContentType 'application/json' -Body $body }
            return Invoke-RestMethod -Method $method -Uri $uri -Headers $vaultHeaders
        } catch {
            if ($i -ge 12 -or $_.Exception.Response.StatusCode.value__ -ne 403) { throw }
            Start-Sleep -Seconds 15
        }
    }
}
function Test-VaultSecret([string]$vault, [string]$name) {
    try {
        Invoke-Vault Get "https://$vault.vault.azure.net/secrets/$name/?api-version=7.4" | Out-Null
        return $true
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 404) { return $false }
        throw
    }
}
function Set-VaultSecret([string]$vault, [string]$name, [string]$value) {
    $body = @{ value = $value; contentType = 'base64' } | ConvertTo-Json
    Invoke-Vault Put "https://$vault.vault.azure.net/secrets/$name/?api-version=7.4" $body | Out-Null
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
    return az containerapp secret show --subscription $Subscription -g $ResourceGroup -n $app.name --secret-name $ref.secretRef --query value -o tsv
}

foreach ($name in 'token-key', 'vapid-key') {
    if (-not (Test-VaultSecret $out.stagingVault.value $name)) {
        Write-Host "seeding staging $name"
        Set-VaultSecret $out.stagingVault.value $name $(if ($name -eq 'token-key') { New-TokenKey } else { New-VapidKey })
    }
}

# Production's keys must be the ones it already signs with: a new token key signs every host
# and phone out (hosts exit on 4001), and a new VAPID key orphans every push subscription.
# Only hash prefixes are ever shown, to compare keys across vaults.
function Get-Fingerprint([string]$value) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))).Substring(0, 8).ToLowerInvariant() }
foreach ($pair in @(@('token-key', 'Relay__TokenKey'), @('vapid-key', 'Relay__VapidPrivateKey'))) {
    if (Test-VaultSecret $out.productionVault.value $pair[0]) {
        if ($SeedProductionFrom) {
            $here = Get-Fingerprint (Invoke-Vault Get "https://$($out.productionVault.value).vault.azure.net/secrets/$($pair[0])/?api-version=7.4").value
            $there = Get-Fingerprint (Invoke-Vault Get "https://$SeedProductionFrom.vault.azure.net/secrets/$($pair[0])/?api-version=7.4").value
            Write-Host "production $($pair[0]) already set ($here); $SeedProductionFrom has $there$(if ($here -ne $there) { ' - DIFFERENT, left as it is' })"
        }
        continue
    }
    if ($SeedProductionFrom) {
        $value = (Invoke-Vault Get "https://$SeedProductionFrom.vault.azure.net/secrets/$($pair[0])/?api-version=7.4").value
        Set-VaultSecret $out.productionVault.value $pair[0] $value
        Write-Host "production $($pair[0]) copied from $SeedProductionFrom ($(Get-Fingerprint $value))"
        $value = $null
        continue
    }
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

if ($BaseOnly) {
    Write-Host "base only: push an image to the registry, then run again without -BaseOnly"
    return
}

# Pass 1 may just have created the environment, which is what DNS has to point at.
$productionDomain = if ($Production) { Get-DomainState $productionCfg } else { 'none' }
$stagingDomain = Get-DomainState $stagingCfg
Set-DomainStep $productionDomain $stagingDomain

Write-Host "pass 2: apps$(if ($Production) { ' (including production)' } else { ' (staging only)' })"
$env:RELAY_DEPLOY_APPS = 'true'
$out = az deployment group create --subscription $Subscription -g $ResourceGroup -n "relay-stamp-$Region" -f $template -p $paramFile --query properties.outputs -o json | ConvertFrom-Json

# A managed certificate can only be issued for a hostname already on the app: pass 2 put it
# there, so pass 3 issues the certificate and binds it.
if ($productionDomain -eq 'ready' -or $stagingDomain -eq 'ready') {
    Write-Host "pass 3: managed certificates (a few minutes)"
    Set-DomainStep $(if ($productionDomain -eq 'ready') { 'bound' } else { $productionDomain }) $(if ($stagingDomain -eq 'ready') { 'bound' } else { $stagingDomain })
    $out = az deployment group create --subscription $Subscription -g $ResourceGroup -n "relay-stamp-domains-$Region" -f $template -p $paramFile --query properties.outputs -o json | ConvertFrom-Json
}
Write-Host "staging:    https://$($out.stagingFqdn.value)$(if ($env:RELAY_STAGING_DOMAIN_STATE -eq 'bound') { " and https://$($out.stagingCustomDomain.value)" })"
if ($Production) { Write-Host "production: https://$($out.productionFqdn.value)$(if ($env:RELAY_PRODUCTION_DOMAIN_STATE -eq 'bound') { " and https://$($out.productionCustomDomain.value)" })" }
