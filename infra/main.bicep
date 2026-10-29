// One regional stamp of the relay at size `lean` (design §5.8): Log Analytics, a Container
// Apps environment (consumption, Linux), a registry, and two relay apps (production and
// staging), each with its own managed identity and Key Vault. A new region is a new
// parameter file in regions/ and a deploy; nothing here names a region.
//
// Deploy with infra/deploy.ps1, not directly: it runs the two passes (identities and
// vaults first, so the apps' first revisions can pull and read their secrets), seeds
// missing secrets, and passes the images already running so a stamp deploy never rolls one.
targetScope = 'resourceGroup'

import { appConfig } from 'modules/types.bicep'

@description('Azure region of this stamp.')
param location string = resourceGroup().location

@description('Stamp size. Only lean exists so far: Container Apps consumption, at most one replica per app. standard and massive need relay homes/forwarding (design §5.8) first.')
@allowed(['lean'])
param size string = 'lean'

param logAnalyticsName string

@description('Days. 30 is the minimum and is included in the ingestion price.')
@minValue(30)
param logRetentionDays int = 30

@description('Daily ingestion cap in GB, as a string (Bicep has no decimals). The relay logs a line per connect and disconnect; 0.1 GB is far above that at lean scale and caps a runaway at about $7/month.')
param logDailyCapGb string = '0.1'

param environmentName string

@description('Set when adopting an environment created with workload profiles; a consumption-only environment has none.')
param workloadProfiles array = []

@description('Create or update the environment. False leaves an existing one exactly as it is (production runs in it); infra/deploy.ps1 sets it only for a new stamp or the cutover.')
param manageEnvironment bool = true

param registryName string

@description('Must match the existing registry when adopting one.')
param registryAdminUserEnabled bool = false

@description('GitHub App (device flow) client id the relay signs in through.')
param githubClientId string

@description('Web origins of the PWA.')
param allowedOrigins array

param production appConfig
param staging appConfig

@description('False: create identities, vaults and role assignments only.')
param deployApps bool = true

@description('Production is only (re)defined from here at the cutover and later. While false, this deployment leaves the production app untouched.')
param deployProduction bool = false

@description('Object id of the staging deploy principal (GitHub OIDC, environment staging).')
param stagingDeployerPrincipalId string = ''

@description('Object id of the production deploy principal (GitHub OIDC, environment production).')
param productionDeployerPrincipalId string = ''

@description('Object id of the person running deploy.ps1, who seeds the vaults\' secrets.')
param operatorPrincipalId string = ''

var tags = {
  'agent-bridge-stamp': 'relay'
  'agent-bridge-size': size
}

var roles = {
  acrPush: '8311e382-0749-4cb8-b61a-304f252e45ec'
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: logRetentionDays
    workspaceCapping: { dailyQuotaGb: json(logDailyCapGb) }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = if (manageEnvironment) {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    zoneRedundant: false
    workloadProfiles: empty(workloadProfiles) ? null : workloadProfiles
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: registryAdminUserEnabled
  }
}

// Staging's principal builds and pushes every image; production's only rolls one that
// staging has already pushed and passed.
resource stagingPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(stagingDeployerPrincipalId)) {
  name: guid(registry.id, stagingDeployerPrincipalId, roles.acrPush)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPush)
    principalId: stagingDeployerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Both read the resource group, to find apps, revisions and FQDNs. Reader cannot list
// secrets or change anything.
resource deployersRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in filter([stagingDeployerPrincipalId, productionDeployerPrincipalId], p => !empty(p)): {
  name: guid(resourceGroup().id, p, roles.reader)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.reader)
    principalId: p
    principalType: 'ServicePrincipal'
  }
}]

// By id, not by symbol: the environment may be one this deployment leaves alone.
var environmentId = resourceId('Microsoft.App/managedEnvironments', environmentName)
var workloadProfileName = empty(workloadProfiles) ? '' : workloadProfiles[0].name

module stagingApp 'modules/relay-app.bicep' = {
  name: 'relay-staging'
  params: {
    location: location
    environmentId: environmentId
    workloadProfileName: workloadProfileName
    registryName: registry.name
    app: staging
    deployApp: deployApps
    githubClientId: githubClientId
    allowedOrigins: allowedOrigins
    deployerPrincipalId: stagingDeployerPrincipalId
    deployerReadsSecrets: true
    operatorPrincipalId: operatorPrincipalId
    tags: union(tags, { 'agent-bridge-env': 'staging' })
  }
  dependsOn: [environment]
}

module productionApp 'modules/relay-app.bicep' = {
  name: 'relay-production'
  params: {
    location: location
    environmentId: environmentId
    workloadProfileName: workloadProfileName
    registryName: registry.name
    app: production
    deployApp: deployApps && deployProduction
    githubClientId: githubClientId
    allowedOrigins: allowedOrigins
    deployerPrincipalId: productionDeployerPrincipalId
    deployerReadsSecrets: false
    operatorPrincipalId: operatorPrincipalId
    tags: union(tags, { 'agent-bridge-env': 'production' })
  }
  dependsOn: [environment]
}

output stagingFqdn string = stagingApp.outputs.fqdn
output productionFqdn string = productionApp.outputs.fqdn
output stagingVault string = stagingApp.outputs.vaultName
output productionVault string = productionApp.outputs.vaultName
