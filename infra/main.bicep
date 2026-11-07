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

@description('Origins allowed on staging only (its relay and blob store), added to allowedOrigins. A preview or test site goes here so production never accepts it.')
param stagingExtraAllowedOrigins array = []

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

@description('Custom-domain step for each app; see modules/relay-app.bicep.')
@allowed(['none', 'added', 'bound'])
param productionDomainState string = 'none'
@allowed(['none', 'added', 'bound'])
param stagingDomainState string = 'none'
@description('Steps for production.extraCustomDomains, by position (modules/relay-app.bicep).')
param productionExtraDomainStates string[] = []
@description('Steps for staging.extraCustomDomains, by position.')
param stagingExtraDomainStates string[] = []

@description('Object id of the person running deploy.ps1, who seeds the vaults\' secrets.')
param operatorPrincipalId string = ''

@description('Where production alerts are emailed (modules/monitoring.bicep). Empty: no monitoring is deployed. Never committed: infra/deploy.ps1 passes it.')
@secure()
param alertEmail string = ''

@description('Application Insights resource for production\'s availability test, and later its metrics (#569).')
param appInsightsName string = ''

@description('Monthly budget for the resource group, in USD (owner, #568).')
param budgetAmount int = 40

@description('First day of the budget\'s first month (yyyy-MM-dd). It must be in the current month when the budget is created and can\'t change later, so infra/deploy.ps1 passes the existing budget\'s date or the first of this month. Empty: no budget.')
param budgetStartDate string = ''

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

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  // Stated as they are on the existing registry, so adopting it changes nothing but tags.
  properties: {
    adminUserEnabled: registryAdminUserEnabled
    anonymousPullEnabled: false
    dataEndpointEnabled: false
    encryption: { status: 'disabled' }
    policies: { azureADAuthenticationAsArmPolicy: { status: 'enabled' } }
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
    allowedOrigins: concat(allowedOrigins, stagingExtraAllowedOrigins)
    deployerPrincipalId: stagingDeployerPrincipalId
    deployerReadsSecrets: true
    deployBlobStore: true
    deployRosterStore: true
    domainState: stagingDomainState
    extraDomainStates: stagingExtraDomainStates
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
    deployBlobStore: deployProduction
    deployRosterStore: deployProduction
    domainState: productionDomainState
    extraDomainStates: productionExtraDomainStates
    operatorPrincipalId: operatorPrincipalId
    tags: union(tags, { 'agent-bridge-env': 'production' })
  }
  dependsOn: [environment]
}

// Production's alerts, availability test and budget (ops/alerts.md). With production, like the
// app itself, and only once there's an address to send alerts to.
module monitoring 'modules/monitoring.bicep' = if (deployApps && deployProduction && !empty(alertEmail) && !empty(appInsightsName)) {
  name: 'relay-monitoring'
  params: {
    location: location
    alertEmail: alertEmail
    productionAppName: production.name
    productionHostname: production.?customDomain ?? productionApp.outputs.fqdn
    workspaceId: logs.id
    appInsightsName: appInsightsName
    budgetAmount: budgetAmount
    budgetStartDate: budgetStartDate
    tags: union(tags, { 'agent-bridge-env': 'production' })
  }
}

output stagingFqdn string = stagingApp.outputs.fqdn
output productionFqdn string = productionApp.outputs.fqdn
output environmentDomain string = manageEnvironment ? environment!.properties.defaultDomain : ''
output verificationId string = manageEnvironment ? environment!.properties.customDomainConfiguration.customDomainVerificationId : ''
output productionCustomDomain string = productionApp.outputs.customDomain
output productionExtraCustomDomains array = productionApp.outputs.extraCustomDomains
output stagingExtraCustomDomains array = stagingApp.outputs.extraCustomDomains
output stagingCustomDomain string = stagingApp.outputs.customDomain
output stagingVault string = stagingApp.outputs.vaultName
output productionVault string = productionApp.outputs.vaultName
output stagingBlobAccountUrl string = stagingApp.outputs.blobAccountUrl
output productionBlobAccountUrl string = productionApp.outputs.blobAccountUrl
output stagingRosterAccountUrl string = stagingApp.outputs.rosterAccountUrl
output productionRosterAccountUrl string = productionApp.outputs.rosterAccountUrl
