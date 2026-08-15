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

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${resourceToken}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: { minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false }
}

// ---------- Azure AI Foundry (unified AIServices account + project) ----------
// NOTE: this pattern deliberately does NOT use the shared infra/modules/openai.bicep module.
// Azure.AI.Projects' AIProjectClient (used by FoundryAgentService.cs to drive the Persistent
// Agents API) requires the newer unified Foundry resource model: a Microsoft.CognitiveServices
// account with kind 'AIServices' and allowProjectManagement: true, plus a `projects` child
// resource, reachable at https://<account>.services.ai.azure.com/api/projects/<project>. The
// shared module deploys kind 'OpenAI' (a plain Cognitive Services OpenAI account, no project
// support), and a separate Microsoft.MachineLearningServices/workspaces hub+project (the older,
// now-legacy Azure AI Studio model) does not expose that endpoint shape at all -- combining them
// is what caused the deployed app to fail DNS resolution against a hostname that was never real.
resource aiFoundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: 'aif-${resourceToken}'
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: 'aif-${resourceToken}'
    publicNetworkAccess: 'Enabled'
    allowProjectManagement: true
  }
}

resource chatModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: aiFoundryAccount
  name: chatModelName
  sku: { name: 'Standard', capacity: 10 }
  properties: {
    model: { format: 'OpenAI', name: chatModelName, version: chatModelVersion }
  }
}

resource aiFoundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: aiFoundryAccount
  name: 'orchestrator-sample'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'orchestrator-sample'
    description: 'Orchestrator pattern sample: central agent delegating to research/data/analytics tools.'
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
        { name: 'AZURE_OPENAI_ENDPOINT', value: aiFoundryAccount.properties.endpoint }
        { name: 'AZURE_OPENAI_DEPLOYMENT', value: chatModelName }
        { name: 'AZURE_AI_PROJECT_ENDPOINT', value: 'https://${aiFoundryAccount.name}.services.ai.azure.com/api/projects/${aiFoundryProject.name}' }
        { name: 'AZURE_SEARCH_ENDPOINT', value: 'https://${search.name}.search.windows.net' }
        { name: 'AZURE_SEARCH_INDEX', value: 'knowledge-base' }
        { name: 'SQL_CONNECTION_STRING', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=orchestratordb;Authentication=Active Directory Managed Identity;' }
      ]
    }
  }
}

resource openAiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundryAccount.id, functionApp.id, 'Cognitive Services OpenAI User')
  scope: aiFoundryAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

// "Azure AI User" (recently renamed "Foundry User" -- same role ID) -- grants the data-plane
// actions Persistent Agents needs: create/run agents, threads, messages against the project.
resource aiFoundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundryAccount.id, functionApp.id, 'Azure AI User')
  scope: aiFoundryAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
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

output AZURE_OPENAI_ENDPOINT string = aiFoundryAccount.properties.endpoint
output AZURE_OPENAI_DEPLOYMENT string = chatModelName
output AZURE_AI_PROJECT_ENDPOINT string = 'https://${aiFoundryAccount.name}.services.ai.azure.com/api/projects/${aiFoundryProject.name}'
output AZURE_SEARCH_ENDPOINT string = 'https://${search.name}.search.windows.net'
output SQL_SERVER_FQDN string = sqlServer.properties.fullyQualifiedDomainName
output FUNCTION_APP_NAME string = functionApp.name
