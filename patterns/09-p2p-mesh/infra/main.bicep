// P2P Mesh pattern — Event Grid custom topic + Event Grid-triggered Functions + Cosmos DB state
targetScope = 'resourceGroup'

@description('Environment name used to derive resource names (azd convention)')
param environmentName string

@description('Azure region for all resources')
param location string = resourceGroup().location

param chatModelName string = 'gpt-4.1'
param chatModelVersion string = '2025-04-14'

var resourceToken = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))
var tags = { 'azd-env-name': environmentName, pattern: 'p2p-mesh' }

// ---------- Shared modules ----------
module observability '../../../infra/modules/observability.bicep' = {
  name: 'observability'
  params: { resourceToken: resourceToken, location: location, tags: tags }
}

module openai '../../../infra/modules/openai.bicep' = {
  name: 'openai'
  params: {
    resourceToken: resourceToken
    location: location
    tags: tags
    deployments: [
      { name: chatModelName, format: 'OpenAI', version: chatModelVersion, skuName: 'Standard', capacity: 10 }
    ]
  }
}

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: 'aoai-${resourceToken}'
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${resourceToken}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: { minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false }
}

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: 'srch-${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'basic' }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http403'
      }
    }
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' = {
  name: 'cosmos-${resourceToken}'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [{ locationName: location, failoverPriority: 0 }]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
  }
}

// Database/container the app's CorrelationStateService expects to already exist (mesh/correlations,
// partitioned on /id since the app always sets the item's id equal to the partition key value).
resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-08-15' = {
  parent: cosmos
  name: 'mesh'
  properties: { resource: { id: 'mesh' } }
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  parent: cosmosDatabase
  name: 'correlations'
  properties: {
    resource: {
      id: 'correlations'
      partitionKey: { paths: ['/id'], kind: 'Hash' }
    }
  }
}

// ---------- Event Grid custom topic (the mesh's connective tissue) ----------
resource eventGridTopic 'Microsoft.EventGrid/topics@2024-06-01-preview' = {
  name: 'evgt-${resourceToken}'
  location: location
  tags: tags
  properties: { inputSchema: 'EventGridSchema' }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'EP1', tier: 'ElasticPremium' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: observability.outputs.appInsightsConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AZURE_OPENAI_ENDPOINT', value: openai.outputs.endpoint }
        { name: 'AZURE_OPENAI_DEPLOYMENT', value: chatModelName }
        { name: 'AZURE_SEARCH_ENDPOINT', value: 'https://${search.name}.search.windows.net' }
        { name: 'AZURE_SEARCH_INDEX', value: 'knowledge-base' }
        { name: 'COSMOS_ENDPOINT', value: cosmos.properties.documentEndpoint }
        { name: 'EVENTGRID_TOPIC_ENDPOINT', value: eventGridTopic.properties.endpoint }
      ]
    }
  }
}

resource openAiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiAccount.id, functionApp.id, 'Cognitive Services OpenAI User')
  scope: openAiAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

resource eventGridRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(eventGridTopic.id, functionApp.id, 'EventGrid Data Sender')
  scope: eventGridTopic
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'd5a91429-5739-47e2-a06b-3470a27159e7')
  }
}

resource searchRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, functionApp.id, 'Search Index Data Reader')
  scope: search
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  }
}

resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, 'Storage Blob Data Owner')
  scope: storage
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  }
}

// Cosmos DB uses its own native RBAC system, separate from Microsoft.Authorization role assignments.
// Without this, the function app's managed identity gets 403 Forbidden on every data-plane call.
resource cosmosDataContributorRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = {
  parent: cosmos
  name: guid(cosmos.id, functionApp.id, 'CosmosBuiltInDataContributor')
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: functionApp.identity.principalId
    scope: cosmos.id
  }
}

output AZURE_OPENAI_ENDPOINT string = openai.outputs.endpoint
output AZURE_OPENAI_DEPLOYMENT string = chatModelName
output AZURE_SEARCH_ENDPOINT string = 'https://${search.name}.search.windows.net'
output COSMOS_ENDPOINT string = cosmos.properties.documentEndpoint
output EVENTGRID_TOPIC_ENDPOINT string = eventGridTopic.properties.endpoint
output EVENTGRID_TOPIC_NAME string = eventGridTopic.name
output FUNCTION_APP_NAME string = functionApp.name
