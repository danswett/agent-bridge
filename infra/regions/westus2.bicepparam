// West US 2: the first stamp. It adopts the resources the S4/S5 dev relay was created with
// (resource group rg-agent-bridge-relay), so production keeps its URL.
using '../main.bicep'

param location = 'westus2'
param logAnalyticsName = 'LOG_ANALYTICS_NAME'
param environmentName = 'ENVIRONMENT_NAME'
param registryName = 'agentbridgerelay59731'
// The "Agent Bridge (dev)" GitHub App, device flow. A client id is public.
param githubClientId = 'Iv23liXL0Mvqs2dckn5U'
param allowedOrigins = ['https://danswett.github.io']

param production = {
  name: 'agent-bridge-relay'
  image: readEnvironmentVariable('RELAY_PRODUCTION_IMAGE', '')
  version: readEnvironmentVariable('RELAY_PRODUCTION_VERSION', '')
  identityName: 'id-agent-bridge-relay'
  keyVaultName: 'kv-abrelay-wus2'
  minReplicas: 1
  maxReplicas: 1
  cpu: '0.25'
  memory: '0.5Gi'
}

param staging = {
  name: 'agent-bridge-relay-staging'
  image: readEnvironmentVariable('RELAY_STAGING_IMAGE', '')
  version: readEnvironmentVariable('RELAY_STAGING_VERSION', '')
  identityName: 'id-agent-bridge-relay-staging'
  keyVaultName: 'kv-abrelay-wus2-stg'
  minReplicas: 0
  maxReplicas: 1
  cpu: '0.25'
  memory: '0.5Gi'
}

param stagingDeployerPrincipalId = readEnvironmentVariable('RELAY_STAGING_DEPLOYER', '')
param productionDeployerPrincipalId = readEnvironmentVariable('RELAY_PRODUCTION_DEPLOYER', '')
param deployApps = bool(readEnvironmentVariable('RELAY_DEPLOY_APPS', 'true'))
param deployProduction = bool(readEnvironmentVariable('RELAY_DEPLOY_PRODUCTION', 'false'))

param operatorPrincipalId = readEnvironmentVariable('RELAY_OPERATOR', '')
