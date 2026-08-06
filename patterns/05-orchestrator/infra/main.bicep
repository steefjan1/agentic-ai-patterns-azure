// Orchestrator pattern — Azure AI Foundry Agent Service + specialist tools (AI Search, SQL)
targetScope = 'resourceGroup'

@description('Environment name used to derive resource names (azd convention)')
param environmentName string

@description('Azure region for all resources')
param location string = resourceGroup().location

param chatModelName string = 'gpt-4.1'
param chatModelVersion string = '2025-04-14'

@secure()
param sqlAdminPassword string = newGuid()

var resourceToken = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))
var tags = { 'azd-env-name': environmentName, pattern: 'orchestrator' }

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

// ---------- Azure AI Foundry (hub + project) ----------
resource aiFoundryHub 'Microsoft.MachineLearningServices/workspaces@2024-10-01' = {
  name: 'hub-${resourceToken}'
  location: location
  tags: tags
  kind: 'Hub'
  identity: { type: 'SystemAssigned' }
  properties: {
    friendlyName: 'agentic-patterns-hub'
    storageAccount: storage.id
  }
}

resource aiFoundryProject 'Microsoft.MachineLearningServices/workspaces@2024-10-01' = {
  name: 'proj-${resourceToken}'
  location: location
  tags: tags
  kind: 'Project'
  identity: { type: 'SystemAssigned' }
  properties: {
    friendlyName: 'orchestrator-sample'
    hubResourceId: aiFoundryHub.id
  }
}

// ---------- Research specialist: Azure AI Search ----------
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

// ---------- Data specialist: Azure SQL Database ----------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${resourceToken}'
  location: location
  tags: tags
  properties: {
    administratorLogin: 'orchestratoradmin'
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'orchestratordb'
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic' }
}

resource sqlFirewallAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// ---------- Function App hosting the orchestrator + all three specialist tools ----------
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
        { name: 'AZURE_AI_PROJECT_ENDPOINT', value: 'https://${aiFoundryProject.name}.services.ai.azure.com' }
        { name: 'AZURE_SEARCH_ENDPOINT', value: 'https://${search.name}.search.windows.net' }
        { name: 'AZURE_SEARCH_INDEX', value: 'knowledge-base' }
        { name: 'SQL_CONNECTION_STRING', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=orchestratordb;Authentication=Active Directory Managed Identity;' }
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

output AZURE_OPENAI_ENDPOINT string = openai.outputs.endpoint
output AZURE_OPENAI_DEPLOYMENT string = chatModelName
output AZURE_AI_PROJECT_ENDPOINT string = 'https://${aiFoundryProject.name}.services.ai.azure.com'
output AZURE_SEARCH_ENDPOINT string = 'https://${search.name}.search.windows.net'
output SQL_SERVER_FQDN string = sqlServer.properties.fullyQualifiedDomainName
output FUNCTION_APP_NAME string = functionApp.name
