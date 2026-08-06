// Planning pattern — Azure OpenAI + Durable Functions + Table Storage + Logic Apps escalation
targetScope = 'resourceGroup'

@description('Environment name used to derive resource names (azd convention)')
param environmentName string

@description('Azure region for all resources')
param location string = resourceGroup().location

param chatModelName string = 'gpt-4o'
param chatModelVersion string = '2024-08-06'

var resourceToken = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))
var tags = { 'azd-env-name': environmentName, pattern: 'planning' }

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
  name: openai.outputs.name
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${resourceToken}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: { minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource planStepsTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'planstatus'
}

// ---------- Logic Apps Standard (failure escalation) ----------
resource logicAppPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-la-${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'WS1', tier: 'WorkflowStandard' }
  properties: { reserved: true }
}

resource logicApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'logic-${resourceToken}'
  location: location
  tags: tags
  kind: 'functionapp,workflowapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: logicAppPlan.id
    httpsOnly: true
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'APP_KIND', value: 'workflowApp' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: observability.outputs.appInsightsConnectionString }
      ]
    }
  }
}

// ---------- Function App (Durable Functions) ----------
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-fn-${resourceToken}'
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
        { name: 'AZURE_STORAGE_ACCOUNT', value: storage.name }
        { name: 'PLAN_STATUS_TABLE', value: 'planstatus' }
        { name: 'ESCALATION_WORKFLOW_URL', value: 'https://${logicApp.properties.defaultHostName}/api/notify-failure/triggers/When_a_HTTP_request_is_received/invoke' }
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

resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, 'Storage Table Data Contributor')
  scope: storage
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
  }
}

output AZURE_OPENAI_ENDPOINT string = openai.outputs.endpoint
output AZURE_OPENAI_DEPLOYMENT string = chatModelName
output FUNCTION_APP_NAME string = functionApp.name
output LOGIC_APP_NAME string = logicApp.name
