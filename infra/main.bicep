// Infrastructure for the Invoice Processing & Approval Pipeline.
// Deployed independently of the Logic App workflow / Function code (see docs/architecture.md
// for why): this template provisions the resources; a separate CI/CD pipeline
// (.github/workflows/deploy-app.yml) deploys and re-deploys the workflow + function zip into it.
//
// Deploy with:
//   az deployment group create -g rg-invoiceproc-<env> -f infra/main.bicep -p infra/main.parameters.<env>.json

targetScope = 'resourceGroup'

@description('Environment short name, used in every resource name (dev, test, prod).')
@allowed(['dev', 'test', 'prod'])
param environmentName string = 'dev'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('SQL administrator login for initial provisioning only — day-to-day access from the Logic App and Function use managed identity, not this login.')
param sqlAdministratorLogin string

@secure()
@description('SQL administrator password. Pass via --parameters sqlAdministratorPassword=... at deploy time; never commit this.')
param sqlAdministratorPassword string

@description('Email address that receives approval requests and failure alerts.')
param financeApproverEmail string

var namePrefix = 'invoiceproc'
var tags = {
  system: 'invoice-processing-pipeline'
  environment: environmentName
  managedBy: 'bicep'
}

// ---------- Observability ----------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
  }
}

// ---------- Storage (Logic App Standard runtime storage + invoice blob containers) ----------

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${namePrefix}${environmentName}'
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }

  resource blobService 'blobServices' = {
    name: 'default'
    resource incoming 'containers' = {
      name: 'incoming-invoices'
      properties: { publicAccess: 'None' }
    }
    resource processed 'containers' = {
      name: 'processed'
      properties: { publicAccess: 'None' }
    }
    resource failed 'containers' = {
      name: 'failed'
      properties: { publicAccess: 'None' }
    }
  }
}

// ---------- Key Vault ----------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
  }
}

// ---------- Service Bus (dead-letter queue for failed invoices) ----------

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2023-01-01-preview' = {
  name: 'sb-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }

  resource failuresQueue 'queues' = {
    name: 'invoice-failures'
    properties: {
      maxDeliveryCount: 5
      deadLetteringOnMessageExpiration: true
      defaultMessageTimeToLive: 'P14D'
    }
  }
}

// ---------- SQL (invoice records) ----------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdministratorLogin
    administratorLoginPassword: sqlAdministratorPassword
    minimalTlsVersion: '1.2'
  }

  resource database 'databases' = {
    name: 'sqldb-${namePrefix}-${environmentName}'
    location: location
    sku: { name: 'Basic', tier: 'Basic' }
  }

  resource allowAzureServices 'firewallRules' = {
    name: 'AllowAzureServices'
    properties: {
      startIpAddress: '0.0.0.0'
      endIpAddress: '0.0.0.0'
    }
  }
}

// ---------- Function App (InvoiceExtractionFunction) — Elastic Premium for VNET + no cold start on the extraction path ----------

resource functionPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-func-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  sku: { name: 'EP1', tier: 'ElasticPremium' }
  kind: 'elastic'
}

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2023-05-01' = {
  name: 'docint-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: 'docint-${namePrefix}-${environmentName}'
    publicNetworkAccess: 'Enabled'
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  kind: 'functionapp'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__accountName', value: storageAccount.name }
        { name: 'InvoiceStorage__blobServiceUri', value: storageAccount.properties.primaryEndpoints.blob }
        { name: 'DocumentIntelligence__endpoint', value: documentIntelligence.properties.endpoint }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// ---------- Logic App Standard (ProcessInvoiceWorkflow + HandleInvoiceFailure) ----------

resource logicAppPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-logicapp-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  sku: { name: 'WS1', tier: 'WorkflowStandard' }
  kind: 'elastic'
}

resource logicApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'logicapp-${namePrefix}-${environmentName}'
  location: location
  tags: tags
  kind: 'functionapp,workflowapp'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: logicAppPlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'AzureWebJobsStorage__accountName', value: storageAccount.name }
        { name: 'WORKFLOWS_SUBSCRIPTION_ID', value: subscription().subscriptionId }
        { name: 'WORKFLOWS_RESOURCE_GROUP_NAME', value: resourceGroup().name }
        { name: 'WORKFLOWS_LOCATION_NAME', value: location }
        { name: 'invoiceStorage_connectionString', value: '@Microsoft.KeyVault(SecretUri=${keyVault.properties.vaultUri}secrets/invoice-storage-connstr/)' }
        { name: 'invoiceSqlDb_connectionString', value: '@Microsoft.KeyVault(SecretUri=${keyVault.properties.vaultUri}secrets/invoice-sql-connstr/)' }
        { name: 'invoiceServiceBus_connectionString', value: '@Microsoft.KeyVault(SecretUri=${keyVault.properties.vaultUri}secrets/invoice-sb-connstr/)' }
        { name: 'invoiceExtractionFunction_baseUrl', value: 'https://${functionApp.properties.defaultHostName}/api' }
        { name: 'invoiceExtractionFunction_key', value: '@Microsoft.KeyVault(SecretUri=${keyVault.properties.vaultUri}secrets/invoice-func-key/)' }
        { name: 'approvalThresholdAmount', value: '5000' }
        { name: 'financeApproverEmail', value: financeApproverEmail }
        { name: 'APPINSIGHTS_INSTRUMENTATIONKEY', value: appInsights.properties.InstrumentationKey }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

// ---------- RBAC: least-privilege managed identity role assignments ----------

var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'

// AzureWebJobsStorage identity-based connections (the __accountName app setting pattern used
// below) need blob + queue + table data-plane access, not just blob, because the Functions/Logic
// Apps runtime uses all three for its own bookkeeping (leases, triggers, timer state) —
// blob-only is the single most common cause of a Standard Logic App that deploys fine but fails
// to start.
resource logicAppStorageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, logicApp.id, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource logicAppStorageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, logicApp.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionStorageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionStorageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource logicAppStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, logicApp.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource logicAppKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, logicApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionDocIntelligenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documentIntelligence.id, functionApp.id, cognitiveServicesUserRoleId)
  scope: documentIntelligence
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource logicAppServiceBusRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, logicApp.id, serviceBusDataSenderRoleId)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
    principalId: logicApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Note: SQL data-plane access for the
// Logic App's SQL connector is granted via a contained database user mapped to the Logic App's
// managed identity (T-SQL `CREATE USER [logicapp-invoiceproc-<env>] FROM EXTERNAL PROVIDER;`
// `ALTER ROLE db_datawriter ADD MEMBER ...`), which Bicep/ARM cannot express — see
// docs/deployment-guide.md step 6 for the exact script to run once after this template deploys.

// ---------- Diagnostic settings: send platform logs to Log Analytics ----------

resource logicAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-logicapp'
  scope: logicApp
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      { categoryGroup: 'allLogs', enabled: true }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

resource functionDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-function'
  scope: functionApp
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      { categoryGroup: 'allLogs', enabled: true }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

output logicAppName string = logicApp.name
output functionAppName string = functionApp.name
output logicAppPrincipalId string = logicApp.identity.principalId
output functionAppPrincipalId string = functionApp.identity.principalId
output keyVaultName string = keyVault.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output documentIntelligenceEndpoint string = documentIntelligence.properties.endpoint
