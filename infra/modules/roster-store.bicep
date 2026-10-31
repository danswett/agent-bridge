// One relay app's machine roster (docs/protocol/roster.md): a small JSON blob per account
// listing its hosts and when each was last seen, so a device can show a machine that is
// offline and a person can forget one that is gone. Kept apart from the blob store, whose
// lifecycle rule deletes every blob a day after it is written.
//
// Reachable by the app's managed identity alone, with Storage Blob Data Contributor on the
// one container: no key exists anywhere (shared key access is off), no SAS is signed (no
// delegator role), and no browser reaches it (no CORS). Blob names are an HMAC of the account
// id under a key only the relay holds, so a name says nothing about whose roster it is.

@description('Azure region.')
param location string

@description('Storage account name: globally unique, 3-24 lowercase letters and digits.')
param name string

@description('Principal id of the relay app\'s managed identity.')
param relayPrincipalId string

@description('Tags for every resource.')
param tags object

var container = 'roster'

var roles = {
  // Read, write and delete blobs: given on the one container only.
  blobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  // LRS, Hot: a few KiB per account, rewritten every few minutes while a host is online. Losing
  // it loses only "last seen" for offline machines; online ones are listed again as they connect.
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    encryption: {
      keySource: 'Microsoft.Storage'
      services: { blob: { enabled: true, keyType: 'Account' } }
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    // A forgotten machine is gone when its roster is rewritten; nothing keeps an old copy.
    deleteRetentionPolicy: { enabled: false }
    containerDeleteRetentionPolicy: { enabled: false }
    isVersioningEnabled: false
    changeFeed: { enabled: false }
    // Only the relay reads and writes here; no browser does.
    cors: { corsRules: [] }
  }
}

resource roster 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: container
  properties: { publicAccess: 'None' }
}

// An online host's roster is rewritten every ten minutes, so only an account none of whose
// hosts has connected for half a year is ever old enough to go: its list of offline machines
// goes with it, and comes back as they connect. Also what clears a smoke test's throwaway
// accounts. Lifecycle runs once a day.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-untouched-for-180-days'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'] }
            actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 180 } } }
          }
        }
      ]
    }
  }
}

resource dataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(roster.id, relayPrincipalId, roles.blobDataContributor)
  scope: roster
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
    principalId: relayPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output accountUrl string = account.properties.primaryEndpoints.blob
output container string = container
