// West US 2, in the "Visual Studio Enterprise Subscription" (0e38f344-908e-4ff3-88ac-4d499a1f51f8),
// resource group rg-agent-bridge-relay. infra/deploy.ps1 deploys here by default. Production
// is served at relay.bad-duck.com (DNS at Cloudflare; see docs/relay-deploy.md).
using '../main.bicep'

param location = 'westus2'
// deploy.ps1 adopts whatever environment and workspace a live production app runs on; these
// names are what a stamp built from nothing gets.
param logAnalyticsName = readEnvironmentVariable('RELAY_LOG_ANALYTICS_NAME', 'log-agent-bridge-relay-wus2')
param environmentName = readEnvironmentVariable('RELAY_ENVIRONMENT_NAME', 'cae-agent-bridge-relay-wus2')
param workloadProfiles = [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
param manageEnvironment = bool(readEnvironmentVariable('RELAY_MANAGE_ENVIRONMENT', 'true'))
param registryName = 'abrelaywus2'
// The "Agent Bridge (dev)" GitHub App, device flow. A client id is public.
param githubClientId = 'Iv23liXL0Mvqs2dckn5U'
param allowedOrigins = ['https://danswett.github.io']

param production = {
  name: 'agent-bridge-relay'
  image: readEnvironmentVariable('RELAY_PRODUCTION_IMAGE', '')
  version: readEnvironmentVariable('RELAY_PRODUCTION_VERSION', '')
  identityName: 'id-agent-bridge-relay'
  keyVaultName: 'kv-abrelay-prod-wus2'
  minReplicas: 1
  maxReplicas: 1
  cpu: '0.25'
  memory: '0.5Gi'
  customDomain: 'relay.bad-duck.com'
  blobStorageName: 'abblobprodwus2'
}

param staging = {
  name: 'agent-bridge-relay-staging'
  image: readEnvironmentVariable('RELAY_STAGING_IMAGE', '')
  version: readEnvironmentVariable('RELAY_STAGING_VERSION', '')
  identityName: 'id-agent-bridge-relay-staging'
  keyVaultName: 'kv-abrelay-stg-wus2'
  blobStorageName: 'abblobstgwus2'
  minReplicas: 0
  maxReplicas: 1
  cpu: '0.25'
  memory: '0.5Gi'
}

param productionDomainState = readEnvironmentVariable('RELAY_PRODUCTION_DOMAIN_STATE', 'none')
param stagingDomainState = readEnvironmentVariable('RELAY_STAGING_DOMAIN_STATE', 'none')
param stagingDeployerPrincipalId = readEnvironmentVariable('RELAY_STAGING_DEPLOYER', '')
param productionDeployerPrincipalId = readEnvironmentVariable('RELAY_PRODUCTION_DEPLOYER', '')
param deployApps = bool(readEnvironmentVariable('RELAY_DEPLOY_APPS', 'true'))
param deployProduction = bool(readEnvironmentVariable('RELAY_DEPLOY_PRODUCTION', 'false'))

param operatorPrincipalId = readEnvironmentVariable('RELAY_OPERATOR', '')
