@description('Existing resource group name. Must already exist. (Informational — RG scope is the deployment target.)')
param resourceGroupName string = resourceGroup().name

@description('Azure region for App Service, Insights, and Log Analytics.')
param location string = resourceGroup().location

@description('Azure region for SQL. Defaults to location; override when the region has no SQL capacity.')
param sqlLocation string = location

@description('App Service web app name (globally unique; becomes https://<name>.azurewebsites.net).')
param webAppName string

@description('App Service plan name.')
param appServicePlanName string = '${webAppName}-plan'

@description('Azure SQL server name (globally unique).')
param sqlServerName string

@description('Azure SQL database name.')
param sqlDatabaseName string = 'ProjectorMcp'

@description('Existing Key Vault name. MUST already exist. This template never creates or deletes it.')
param keyVaultName string

@description('Object ID of the Entra user who will be SQL Entra admin (signed-in deployer).')
param sqlEntraAdminObjectId string

@description('Display name of the SQL Entra admin.')
param sqlEntraAdminLogin string

@description('Entra tenant ID.')
param tenantId string

@description('Entra hop (OIDC) application (client) ID. Empty = no Entra sign-in step.')
param entraClientId string = ''

@description('Static MCP OAuth client ID for Microsoft 365 Copilot / declarative agents.')
param mcpOAuthClientId string = ''

@description('Allowed MCP OAuth redirect URIs (in addition to the server defaults).')
param allowedMcpRedirectUris array = [
  'https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect'
]

@description('App Service SKU. B1 or higher for Always On.')
param appServiceSku string = 'B1'

@description('Log Analytics workspace name (hosts Application Insights + SQL diagnostics).')
param logAnalyticsWorkspaceName string = '${webAppName}-law'

@description('Application Insights component name.')
param appInsightsName string = '${webAppName}-ai'

// ----- Existing Key Vault (reference only — never declare Microsoft.KeyVault/vaults) -----
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

var publicBaseUrl = 'https://${webAppName}.azurewebsites.net'
var mcpAudience = 'projector-mcp'
var projectorRedirectUri = '${publicBaseUrl}/oauth/projector/callback'
var keyVaultUri = keyVault.properties.vaultUri

// SQL connection uses App Service system-assigned Managed Identity (no SQL password).
var sqlMiConnectionString = 'Server=tcp:${sqlServerName}.database.windows.net,1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: appServiceSku
    tier: 'Basic'
    capacity: 1
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: sqlLocation
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlEntraAdminLogin
      sid: sqlEntraAdminObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlFirewallAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: sqlLocation
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 2147483648
  }
}

// SQL platform metrics/logs → same Log Analytics workspace as App Insights (Azure Monitor).
resource sqlDatabaseDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'sql-to-law'
  scope: sqlDatabase
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'Basic'
        enabled: true
      }
    ]
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
    }
  }
}

/*
  APP SETTINGS ARE NOT SET IN BICEP.

  Who wiped settings: enabling ApplicationInsightsAgent_EXTENSION_VERSION=~3 lets the App Insights
  codeless site extension (platform MI) issue Microsoft.Web/sites/write and replace the bag with
  AI keys only. Separately, Microsoft.Web/sites/config name=appsettings is always a FULL PUT —
  union(list(appsettings)) in the same template is a circular dependency, and a raw PUT without
  every key deletes unmanaged settings.

  Fix: Deploy-Infrastructure.ps1 / Publish-App.ps1 apply settings with
  `az webapp config appsettings set` (MERGE). They set ApplicationInsightsAgent_EXTENSION_VERSION=disabled
  and APPLICATIONINSIGHTS_CONNECTION_STRING for OpenTelemetry SDK telemetry (no site extension).
*/

// Key Vault Secrets User on the EXISTING vault only (role assignment; never create/delete vault).
// App Service system-assigned MI reads secrets via ManagedIdentityCredential.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output webAppName string = webApp.name
output deployedResourceGroup string = resourceGroupName
output webAppHostname string = webApp.properties.defaultHostName
output publicBaseUrl string = publicBaseUrl
output mcpUrl string = '${publicBaseUrl}/mcp'
output oauthAuthorizeUrl string = '${publicBaseUrl}/oauth/authorize'
output oauthTokenUrl string = '${publicBaseUrl}/oauth/token'
output projectorCallbackUrl string = projectorRedirectUri
output entraCallbackUrl string = '${publicBaseUrl}/oauth/entra/callback'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output webAppPrincipalId string = webApp.identity.principalId
output keyVaultUri string = keyVaultUri
output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
output logAnalyticsWorkspaceId string = logAnalytics.id
output sqlUsesManagedIdentity bool = true
output keyVaultUsesManagedIdentity bool = true
// Documented for scripts (app settings applied via merge, not Bicep PUT):
output intendedEntraClientId string = entraClientId
output intendedMcpOAuthClientId string = mcpOAuthClientId
output intendedMcpAudience string = mcpAudience
output intendedSqlMiConnectionString string = sqlMiConnectionString
output intendedAllowedMcpRedirectUris array = allowedMcpRedirectUris
output appInsightsAgentExtensionVersion string = 'disabled'
