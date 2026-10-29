// One relay app of a stamp (design §5.8): its identity, its Key Vault, and the Container App.
// Production and staging are two instances of this module, each with its own identity and
// vault, so staging can never read production's signing keys.

@description('Azure region.')
param location string

@description('Container Apps environment the app runs in.')
param environmentId string

@description('The stamp\'s registry; the app pulls with its own identity.')
param registryName string

@description('Settings for this app.')
param app appConfig

@description('Deploy the Container App itself. False on the first pass, which creates the identity, vault and role assignments the app needs before its first revision can start.')
param deployApp bool

@description('GitHub App (device flow) client id.')
param githubClientId string

@description('Web origins allowed to call the sign-in API and open sockets: the PWA.')
param allowedOrigins array

@description('Object id of the CI service principal that rolls this app\'s image (empty: none).')
param deployerPrincipalId string = ''

@description('Grant the deployer read access to this app\'s vault (the staging smoke test mints a token with the staging token key).')
param deployerReadsSecrets bool = false

@description('Object id of the person running infra/deploy.ps1, who seeds the vault\'s secrets (empty: none).')
param operatorPrincipalId string = ''

@description('Workload profile to run on, in an environment that has them (empty: none).')
param workloadProfileName string = ''

@description('Tags for every resource.')
param tags object

import { appConfig } from 'types.bicep'

var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
  managedIdentityOperator: 'f1a07417-d97a-45cb-824c-7a7467783830'
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: app.identityName
  location: location
  tags: tags
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: app.keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    // Purge protection is irreversible and pins the vault name for 90 days after a delete;
    // turn it on (infra/README) once the subscription is permanent.
    enablePurgeProtection: app.?purgeProtection == true ? true : null
    publicNetworkAccess: 'Enabled'
  }
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, roles.acrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource readSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, roles.keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId) && deployerReadsSecrets) {
  name: guid(vault.id, deployerPrincipalId, roles.keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: deployerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource operatorSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorPrincipalId)) {
  name: guid(vault.id, operatorPrincipalId, roles.keyVaultSecretsOfficer)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}

// The deployer may assign this identity (an image roll re-submits the app with it), and
// nothing else in the resource group.
resource deployerIdentity 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(identity.id, deployerPrincipalId, roles.managedIdentityOperator)
  scope: identity
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.managedIdentityOperator)
    principalId: deployerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

var secretRefs = concat(
  [
    { name: 'token-key', keyVaultUrl: '${vault.properties.vaultUri}secrets/token-key', identity: identity.id }
    { name: 'vapid-key', keyVaultUrl: '${vault.properties.vaultUri}secrets/vapid-key', identity: identity.id }
  ],
  app.?githubClientSecret == true
    ? [{ name: 'github-client-secret', keyVaultUrl: '${vault.properties.vaultUri}secrets/github-client-secret', identity: identity.id }]
    : []
)

var originEnv = [for (o, i) in allowedOrigins: { name: 'Relay__AllowedOrigins__${i}', value: o }]

resource relay 'Microsoft.App/containerApps@2025-01-01' = if (deployApp) {
  name: app.name
  location: location
  // The production job in relay.yml refuses an app without this tag: rolling a new image
  // onto an app infra/ has not adopted would skip its secrets and probes.
  tags: union(tags, { 'agent-bridge-managed': 'infra' })
  identity: {
    // Production keeps the system identity it was created with, so the adoption removes nothing
    // an earlier revision (the rollback target) might rely on.
    type: app.?keepSystemIdentity == true ? 'SystemAssigned,UserAssigned' : 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: empty(workloadProfileName) ? null : workloadProfileName
    configuration: {
      // One revision at a time: presence is in memory, so two live revisions would split an
      // account's devices between them (design §5.8; see docs/relay-deploy.md).
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [{ latestRevision: true, weight: 100 }]
      }
      registries: [{ server: registry.properties.loginServer, identity: identity.id }]
      secrets: secretRefs
    }
    template: {
      // The relay sends every device 1012 at SIGTERM and stops within 20 s (RelayApp).
      terminationGracePeriodSeconds: 30
      containers: [
        {
          name: 'relay'
          image: app.image
          resources: { cpu: json(app.cpu), memory: app.memory }
          env: concat(
            [
              { name: 'Relay__GitHubClientId', value: githubClientId }
              { name: 'Relay__TokenKey', secretRef: 'token-key' }
              { name: 'Relay__VapidPrivateKey', secretRef: 'vapid-key' }
              { name: 'Relay__Version', value: app.version }
              { name: 'Logging__Console__FormatterName', value: 'json' }
            ],
            app.?githubClientSecret == true ? [{ name: 'Relay__GitHubClientSecret', secretRef: 'github-client-secret' }] : [],
            originEnv
          )
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/healthz', port: 8080 }
              periodSeconds: 2
              failureThreshold: 30
              timeoutSeconds: 2
            }
            {
              type: 'Liveness'
              httpGet: { path: '/healthz', port: 8080 }
              periodSeconds: 10
              failureThreshold: 3
              timeoutSeconds: 3
            }
            {
              // Fails once the relay starts draining, so ingress stops sending it new sockets.
              type: 'Readiness'
              httpGet: { path: '/readyz', port: 8080 }
              periodSeconds: 5
              failureThreshold: 3
              successThreshold: 1
              timeoutSeconds: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: app.minReplicas
        maxReplicas: app.maxReplicas
      }
    }
  }
  dependsOn: [pull, readSecrets]
}

resource deployerApp 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApp && !empty(deployerPrincipalId)) {
  name: guid(resourceId('Microsoft.App/containerApps', app.name), deployerPrincipalId, roles.contributor)
  scope: relay
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor)
    principalId: deployerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output fqdn string = deployApp ? relay!.properties.configuration.ingress.fqdn : ''
output identityPrincipalId string = identity.properties.principalId
output vaultName string = vault.name
