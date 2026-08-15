// Sequential Chain pattern — Logic Apps Standard + Azure OpenAI + Blob + Service Bus
targetScope = 'resourceGroup'

@description('Environment name used to derive resource names')
param environmentName string

@description('Azure region for all resources')
param location string = resourceGroup().location

param chatModelName string = 'gpt-4.1'
param chatModelVersion string = '2025-04-14'

var resourceToken = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))
var tags = { 'azd-env-name': environmentName, pattern: 'sequential-chain' }

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

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource intakeContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'intake'
  properties: { publicAccess: 'None' }
}

resource outputContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'output'
  properties: { publicAccess: 'None' }
}

// ---------- Service Bus (downstream hand-off) ----------
resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: 'sb-${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
}

resource sbQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'chain-output'
}

// ---------- Logic Apps Standard ----------
resource logicAppPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'WS1', tier: 'WorkflowStandard' }
  properties: { reserved: true }
}

resource logicApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'logic-${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'workflow' })
  kind: 'functionapp,workflowapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: logicAppPlan.id
    httpsOnly: true
    siteConfig: {
      minimumElasticInstanceCount: 1
      functionAppScaleLimit: 1
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'APP_KIND', value: 'workflowApp' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }
        { name: 'WEBSITE_NODE_DEFAULT_VERSION', value: '~18' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: observability.outputs.appInsightsConnectionString }
        { name: 'AZURE_OPENAI_ENDPOINT', value: openai.outputs.endpoint }
        { name: 'AZURE_OPENAI_DEPLOYMENT', value: chatModelName }
        { name: 'AZURE_STORAGE_ACCOUNT', value: storage.name }
        { name: 'AZURE_STORAGE_CONNECTION_STRING', value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}' }
        { name: 'SERVICEBUS_NAMESPACE', value: '${serviceBus.name}.servicebus.windows.net' }
        { name: 'SERVICEBUS_QUEUE', value: 'chain-output' }
      ]
    }
  }
}

resource openAiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiAccount.id, logicApp.id, 'Cognitive Services OpenAI User')
  scope: openAiAccount
  properties: {
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, logicApp.id, 'Storage Blob Data Contributor')
  scope: storage
  properties: {
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource storageQueueRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, logicApp.id, 'Storage Queue Data Contributor')
  scope: storage
  properties: {
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  }
}

resource storageTableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, logicApp.id, 'Storage Table Data Contributor')
  scope: storage
  properties: {
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
  }
}

resource sbRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, logicApp.id, 'Azure Service Bus Data Sender')
  scope: serviceBus
  properties: {
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}

output AZURE_OPENAI_ENDPOINT string = openai.outputs.endpoint
output AZURE_OPENAI_DEPLOYMENT string = chatModelName
output STORAGE_ACCOUNT_NAME string = storage.name
output LOGIC_APP_NAME string = logicApp.name
output SERVICEBUS_NAMESPACE string = '${serviceBus.name}.servicebus.windows.net'
