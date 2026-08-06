// Shared Azure OpenAI module: one Cognitive Services (OpenAI) account with one or more
// model deployments. Used by every pattern in this repo. Role assignments granting access
// to this account are deliberately left in each pattern's main.bicep (via an `existing`
// reference to this module's output name) rather than handled here, to avoid a circular
// dependency between this module and the compute resource whose managed identity needs
// the role (the compute resource typically also needs this module's `endpoint` output).

@description('Resource token used to derive the account name')
param resourceToken string

@description('Azure region for the account')
param location string = resourceGroup().location

@description('Tags applied to the account and its deployments')
param tags object = {}

@description('Model deployments to create on this account')
param deployments array = [
  {
    name: 'gpt-4o'
    format: 'OpenAI'
    version: '2024-08-06'
    skuName: 'Standard'
    capacity: 10
  }
]

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: 'aoai-${resourceToken}'
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: 'aoai-${resourceToken}'
    publicNetworkAccess: 'Enabled'
  }
}

// Deployments must be created one at a time -- the service rejects concurrent
// deployment creation against the same account.
@batchSize(1)
resource modelDeployments 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for d in deployments: {
  parent: openAi
  name: d.name
  sku: {
    name: d.skuName
    capacity: d.capacity
  }
  properties: {
    model: {
      format: d.format
      name: d.name
      version: d.version
    }
  }
}]

output id string = openAi.id
output name string = openAi.name
output endpoint string = openAi.properties.endpoint
