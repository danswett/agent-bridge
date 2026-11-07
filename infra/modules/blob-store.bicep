// One relay app's blob store (design §4.6): a storage account that holds only ciphertext, for
// at most a day or two, reachable by the app's managed identity alone. No account key is ever
// used (shared key access is off), so there is no key or SAS secret to configure, leak or
// rotate: the relay signs short-lived, single-blob user delegation SAS with a key it gets from
// Entra. Clients upload and download straight to and from here, never through the relay.

@description('Azure region.')
param location string

@description('Storage account name: globally unique, 3-24 lowercase letters and digits.')
param name string

@description('Principal id of the relay app\'s managed identity.')
param relayPrincipalId string

@description('Web origins that upload and download in the browser: the PWA.')
param allowedOrigins array

@description('Tags for every resource.')
param tags object

var container = 'blobs'
// Beta feedback (docs/protocol/feedback.md): sealed bundles the relay can only create, kept
// for the 30-day retention the owner chose in #731. Reading them is the feedback lead's role.
var feedbackContainer = 'feedback'

var roles = {
  // Read, write and delete blobs: given on the one container only.
  blobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  // Get a user delegation key, to sign SAS with: needs the account (blob service) scope.
  blobDelegator: 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
}

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  // LRS, Hot: blobs live a day, so geo-redundancy buys nothing, and Cool's 30-day early
  // deletion charge and dearer operations would cost more for less.
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
    // Nothing kept after a delete: no soft delete, versions or change feed. A deleted blob is gone.
    deleteRetentionPolicy: { enabled: false }
    containerDeleteRetentionPolicy: { enabled: false }
    isVersioningEnabled: false
    changeFeed: { enabled: false }
    // The PWA uploads and downloads in the browser. Only what Put Blob and Get Blob need.
    cors: {
      corsRules: [
        {
          allowedOrigins: allowedOrigins
          allowedMethods: ['PUT', 'GET', 'OPTIONS']
          allowedHeaders: ['content-type', 'x-ms-blob-type', 'x-ms-version', 'x-ms-client-request-id']
          exposedHeaders: ['content-length', 'x-ms-request-id']
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: container
  properties: { publicAccess: 'None' }
}

resource feedback 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: feedbackContainer
  properties: { publicAccess: 'None' }
}

// Write-once for the retention: a blob in this container cannot be overwritten or deleted by
// anyone, the relay included, until its 30 days are up. (The condition on the relay's role below
// covers read, list and delete; this covers overwrite, which no role condition can express.)
resource feedbackRetention 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = {
  parent: feedback
  name: 'default'
  properties: {
    immutabilityPeriodSinceCreationInDays: 30
    allowProtectedAppendWrites: false
  }
}

// The backstop behind the relay's own deletes and its 24-hour tickets: every blob goes a day
// after it was written. Lifecycle runs once a day, so a blob lasts 1 to 2 days at most.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-after-a-day'
          enabled: true
          type: 'Lifecycle'
          definition: {
            // Scoped to the blob container: feedback must outlive this rule.
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['${container}/'] }
            actions: { baseBlob: { delete: { daysAfterCreationGreaterThan: 1 } } }
          }
        }
        {
          name: 'delete-feedback-after-31-days'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'], prefixMatch: ['${feedbackContainer}/'] }
            actions: { baseBlob: { delete: { daysAfterCreationGreaterThan: 31 } } }
          }
        }
      ]
    }
  }
}

resource dataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(blobs.id, relayPrincipalId, roles.blobDataContributor)
  scope: blobs
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
    principalId: relayPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The relay only ever creates feedback: the same role as above, but a condition takes away
// read, list and delete (and tags), so a compromised relay cannot read or destroy what the
// testers sent. The relay holds the bytes only while it checks their size and writes them.
resource feedbackAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(feedback.id, relayPrincipalId, roles.blobDataContributor)
  scope: feedback
  properties: {
    conditionVersion: '2.0'
    condition: '((!(ActionMatches{\'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read\'}) AND !(ActionMatches{\'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete\'}) AND !(ActionMatches{\'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read\'}) AND !(SubOperationMatches{\'Blob.List\'})))'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
    principalId: relayPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource delegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, relayPrincipalId, roles.blobDelegator)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDelegator)
    principalId: relayPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output accountUrl string = account.properties.primaryEndpoints.blob
output container string = container
output feedbackContainer string = feedbackContainer
