<#
.SYNOPSIS
  Deploy or update Projector MCP Azure infrastructure (Incremental only).

.DESCRIPTION
  - Never deletes the resource group.
  - Never creates, deletes, or recreates the Key Vault (it must already exist).
  - Creates Entra apps (optional sign-in hop + MCP OAuth client) and new Key Vault secrets if missing.
  - Deploys App Service + Azure SQL + Application Insights via Bicep in Incremental mode.

.PARAMETER SettingsFile
  Deployment settings (.psd1). Default: deploy.settings.psd1 next to this script.
  Start from deploy.settings.example.psd1.
#>
[CmdletBinding()]
param(
    [string] $SettingsFile = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'DeploySettings.ps1')
if (-not $SettingsFile) { $SettingsFile = Join-Path $scriptDir 'deploy.settings.psd1' }
$cfg = Import-DeploySettings -Path $SettingsFile

$ResourceGroup = $cfg.ResourceGroup
$SubscriptionId = $cfg.SubscriptionId
$WebAppName = $cfg.WebAppName
$KeyVaultName = $cfg.KeyVaultName
$SqlServerName = $cfg.SqlServerName
$SqlDatabaseName = $cfg.SqlDatabaseName
$AllowedMcpRedirectUris = @($cfg.AllowedMcpRedirectUris)

Write-Host "=== Projector MCP Deploy-Infrastructure ===" -ForegroundColor Cyan

# --- az login / subscription ---
$accountJson = az account show -o json 2>$null
if (-not $accountJson) {
    throw "Not logged in to Azure CLI. Run: az login"
}
$account = $accountJson | ConvertFrom-Json
if ($SubscriptionId) {
    az account set --subscription $SubscriptionId | Out-Null
    $account = az account show -o json | ConvertFrom-Json
}
Write-Host "Subscription: $($account.name) ($($account.id))"
Write-Host "Tenant: $($account.tenantId)"
Write-Host "User: $($account.user.name)"

# --- Never delete RG ---
$rg = az group show -n $ResourceGroup -o json 2>$null
if (-not $rg) {
    throw "Resource group '$ResourceGroup' does not exist. Aborting. Never create/delete this RG from this script."
}
Write-Host "Resource group OK: $ResourceGroup (exists - will not delete)"
$Location = if ($cfg.Location) { $cfg.Location } else { ($rg | ConvertFrom-Json).location }
$SqlLocation = if ($cfg.SqlLocation) { $cfg.SqlLocation } else { $Location }

# --- Never create/delete Key Vault ---
$kv = az keyvault show -n $KeyVaultName -o json 2>$null
if (-not $kv) {
    throw "Key Vault '$KeyVaultName' does not exist. Aborting. This script never creates or deletes the vault."
}
$kvObj = $kv | ConvertFrom-Json
Write-Host "Key Vault OK: $KeyVaultName ($($kvObj.properties.vaultUri)) - will not create/delete"

$signedIn = az ad signed-in-user show -o json | ConvertFrom-Json
$sqlAdminOid = $signedIn.id
$sqlAdminLogin = $signedIn.userPrincipalName
$tenantId = $account.tenantId
$publicBaseUrl = "https://$WebAppName.azurewebsites.net"
$entraRedirect = "$publicBaseUrl/oauth/entra/callback"
$teamsRedirect = 'https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect'

function Ensure-KeyVaultSecret {
    param([string] $Name, [string] $Value)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $existing = az keyvault secret show --vault-name $KeyVaultName --name $Name -o json 2>$null
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -eq 0 -and $existing) {
        Write-Host "Key Vault secret exists (leave unchanged): $Name"
        return
    }
    Write-Host "Creating Key Vault secret: $Name"
    az keyvault secret set --vault-name $KeyVaultName --name $Name --value $Value -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to set Key Vault secret $Name" }
}

function New-RandomSecret {
    param([int] $Bytes = 48)
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($buffer)
    }
    finally {
        $rng.Dispose()
    }
    return [Convert]::ToBase64String($buffer)
}

function Ensure-EntraApp {
    param(
        [string] $DisplayName,
        [string[]] $RedirectUris,
        [string] $SecretNameInVault
    )

    $appsJson = az ad app list --display-name $DisplayName --query "[?displayName=='$DisplayName']" -o json
    $apps = $appsJson | ConvertFrom-Json
    if ($apps -and $apps.Count -gt 0) {
        $appId = $apps[0].appId
        $objectId = $apps[0].id
        Write-Host "Entra app exists: $DisplayName ($appId)"
        # Ensure redirect URIs
        $uriArgs = @()
        foreach ($u in $RedirectUris) { $uriArgs += $u }
        az ad app update --id $objectId --web-redirect-uris @uriArgs | Out-Null
    }
    else {
        Write-Host "Creating Entra app: $DisplayName"
        $created = az ad app create `
            --display-name $DisplayName `
            --web-redirect-uris @RedirectUris `
            --sign-in-audience AzureADMyOrg `
            -o json | ConvertFrom-Json
        $appId = $created.appId
        $objectId = $created.id
    }

    # Ensure service principal
    $sp = az ad sp list --filter "appId eq '$appId'" -o json | ConvertFrom-Json
    if (-not $sp -or $sp.Count -eq 0) {
        az ad sp create --id $appId -o none
    }

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $secretExists = az keyvault secret show --vault-name $KeyVaultName --name $SecretNameInVault -o json 2>$null
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -ne 0) { $secretExists = $null }
    if (-not $secretExists) {
        Write-Host "Creating client secret for $DisplayName -> KV $SecretNameInVault"
        $cred = az ad app credential reset --id $objectId --append --display-name "mcp-deploy-$(Get-Date -Format yyyyMMdd)" -o json | ConvertFrom-Json
        az keyvault secret set --vault-name $KeyVaultName --name $SecretNameInVault --value $cred.password -o none
    }
    else {
        Write-Host "Client secret already in Key Vault: $SecretNameInVault"
    }

    # Always store/update client id secret name sibling
    $idSecretName = "${SecretNameInVault}Id"
    # For MCP OAuth we use fixed names below
    return $appId
}

# JWT + token encryption (never overwrite existing)
Ensure-KeyVaultSecret -Name 'ProjectorMcpJwtSigningKey' -Value (New-RandomSecret -Bytes 48)
Ensure-KeyVaultSecret -Name 'ProjectorTokenEncryptionKey' -Value (New-RandomSecret -Bytes 32)

# Entra hop app (OIDC for tid/oid)
$entraAppId = Ensure-EntraApp `
    -DisplayName $cfg.EntraHopAppName `
    -RedirectUris @($entraRedirect) `
    -SecretNameInVault 'ProjectorMcpEntraClientSecret'

# Store Entra client id in KV for app settings / local use
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$entraIdSecret = az keyvault secret show --vault-name $KeyVaultName --name 'ProjectorMcpEntraClientId' -o json 2>$null
$ErrorActionPreference = $prev
if ($LASTEXITCODE -ne 0) { $entraIdSecret = $null }
if (-not $entraIdSecret) {
    az keyvault secret set --vault-name $KeyVaultName --name 'ProjectorMcpEntraClientId' --value $entraAppId -o none
}

# MCP OAuth static client for Copilot / declarative agent
$mcpClientId = Ensure-EntraApp `
    -DisplayName $cfg.McpOAuthAppName `
    -RedirectUris @($teamsRedirect) `
    -SecretNameInVault 'ProjectorMcpOAuthClientSecret'

$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$mcpIdSecret = az keyvault secret show --vault-name $KeyVaultName --name 'ProjectorMcpOAuthClientId' -o json 2>$null
$ErrorActionPreference = $prev
if ($LASTEXITCODE -ne 0) { $mcpIdSecret = $null }
if (-not $mcpIdSecret) {
    az keyvault secret set --vault-name $KeyVaultName --name 'ProjectorMcpOAuthClientId' --value $mcpClientId -o none
}
else {
    # Prefer vault value if present (idempotent re-runs)
    $mcpClientId = (az keyvault secret show --vault-name $KeyVaultName --name 'ProjectorMcpOAuthClientId' -o tsv --query value)
}

$redirectList = @($teamsRedirect) + @($AllowedMcpRedirectUris) | Where-Object { $_ } | Select-Object -Unique

Write-Host "Deploying Bicep (Incremental - never Complete)..." -ForegroundColor Cyan
$deploymentName = "projector-mcp-$(Get-Date -Format 'yyyyMMddHHmmss')"

# Dynamic parameters file avoids PowerShell mangling of JSON array quotes.
$paramsPath = Join-Path $env:TEMP "projector-mcp-params-$deploymentName.json"
$paramsObj = [ordered]@{
    '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters = [ordered]@{
        location = @{ value = $Location }
        sqlLocation = @{ value = $SqlLocation }
        sqlEntraAdminObjectId = @{ value = $sqlAdminOid }
        sqlEntraAdminLogin = @{ value = $sqlAdminLogin }
        tenantId = @{ value = $tenantId }
        entraClientId = @{ value = $entraAppId }
        mcpOAuthClientId = @{ value = $mcpClientId }
        webAppName = @{ value = $WebAppName }
        appServicePlanName = @{ value = $cfg.AppServicePlanName }
        sqlServerName = @{ value = $SqlServerName }
        sqlDatabaseName = @{ value = $SqlDatabaseName }
        keyVaultName = @{ value = $KeyVaultName }
        appServiceSku = @{ value = $cfg.AppServiceSku }
        allowedMcpRedirectUris = @{ value = @($redirectList) }
    }
}
($paramsObj | ConvertTo-Json -Depth 8) | Set-Content -Path $paramsPath -Encoding UTF8

$deployOut = az deployment group create `
    --resource-group $ResourceGroup `
    --name $deploymentName `
    --mode Incremental `
    --template-file (Join-Path $scriptDir 'main.bicep') `
    --parameters "@$paramsPath" `
    -o json

if ($LASTEXITCODE -ne 0 -or -not $deployOut) {
    throw "Bicep deployment failed. See $paramsPath"
}
$outputs = ($deployOut | ConvertFrom-Json).properties.outputs
$principalId = $outputs.webAppPrincipalId.value
$sqlFqdn = $outputs.sqlServerFqdn.value

# Bicep sites/config appsettings is a full PUT. Codeless AI extension (~3) rewrote the bag to AI-only keys.
# Re-merge critical settings; keep ApplicationInsightsAgent_EXTENSION_VERSION=disabled (SDK telemetry only).
Write-Host "Merging App Service settings (Projector + Application Insights; AI extension disabled)..." -ForegroundColor Cyan
$sqlConn = "Server=tcp:$SqlServerName.database.windows.net,1433;Database=$SqlDatabaseName;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;"
$redirectCsv = ($redirectList -join ';')
$aiConn = $outputs.appInsightsConnectionString.value
$aiKey = $outputs.appInsightsInstrumentationKey.value
az webapp config appsettings set `
    --resource-group $ResourceGroup `
    --name $WebAppName `
    --settings `
        ASPNETCORE_ENVIRONMENT=Production `
        WEBSITE_HEALTHCHECK_PATH=/health `
        APPLICATIONINSIGHTS_CONNECTION_STRING=$aiConn `
        APPINSIGHTS_INSTRUMENTATIONKEY=$aiKey `
        ApplicationInsightsAgent_EXTENSION_VERSION=disabled `
        XDT_MicrosoftApplicationInsights_Mode=disabled `
        Projector__AccountCode=$($cfg.AccountCode) `
        Projector__KeyVaultUri=$($kvObj.properties.vaultUri) `
        Projector__ClientIdSecretName=ProjectorPSAOauthClientID `
        Projector__ClientSecretSecretName=ProjectorPSAOauthSecret `
        Projector__JwtSigningKeySecretName=ProjectorMcpJwtSigningKey `
        Projector__TokenEncryptionKeySecretName=ProjectorTokenEncryptionKey `
        Projector__PublicBaseUrl=$publicBaseUrl `
        Projector__McpAudience=projector-mcp `
        Projector__RedirectUri="$publicBaseUrl/oauth/projector/callback" `
        Projector__McpScope=projector.mcp `
        Projector__RequestedScopes=allowFullPermissions `
        Projector__SqlConnectionString=$sqlConn `
        Projector__EntraClientId=$entraAppId `
        Projector__EntraTenantId=$tenantId `
        Projector__McpOAuthClientId=$mcpClientId `
        Projector__UseSqlTokenStore=true `
        Projector__AllowedMcpRedirectUrisCsv=$redirectCsv `
    -o none
if ($LASTEXITCODE -ne 0) { throw "Failed to merge App Service settings" }

Write-Host "Granting App Service MI db access on $SqlDatabaseName..." -ForegroundColor Cyan
# Allow this client IP briefly so we can create the contained MI user.
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$clientIp = $null
try { $clientIp = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10) } catch { }
if ($clientIp) {
    az sql server firewall-rule create -g $ResourceGroup -s $SqlServerName -n DeployClientTemp --start-ip-address $clientIp --end-ip-address $clientIp -o none 2>$null
}
$ErrorActionPreference = $prev

# Create contained user for managed identity (requires AAD token as SQL admin)
$sqlCmd = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$WebAppName')
BEGIN
    CREATE USER [$WebAppName] FROM EXTERNAL PROVIDER;
END
ALTER ROLE db_datareader ADD MEMBER [$WebAppName];
ALTER ROLE db_datawriter ADD MEMBER [$WebAppName];
ALTER ROLE db_ddladmin ADD MEMBER [$WebAppName];
"@

$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
# Prefer sqlcmd if available; else Invoke-Sqlcmd / temporary approach with az
$sqlcmdPath = Get-Command sqlcmd -ErrorAction SilentlyContinue
if ($sqlcmdPath) {
    $tmpSql = Join-Path $env:TEMP "projector-mcp-grant.sql"
    Set-Content -Path $tmpSql -Value $sqlCmd -Encoding UTF8
    sqlcmd -S $sqlFqdn -d $SqlDatabaseName -G -Q $sqlCmd
}
else {
    Write-Host "sqlcmd not found. Attempting PowerShell SqlClient grant..." -ForegroundColor Yellow
    try {
        Add-Type -AssemblyName System.Data
        $conn = New-Object System.Data.SqlClient.SqlConnection
        $conn.ConnectionString = "Server=tcp:$sqlFqdn,1433;Database=$SqlDatabaseName;Encrypt=True;TrustServerCertificate=False;"
        $conn.AccessToken = $token
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sqlCmd
        [void]$cmd.ExecuteNonQuery()
        $conn.Close()
        Write-Host "SQL MI user granted."
    }
    catch {
        Write-Warning "Could not grant SQL MI user automatically: $_. Grant manually as Entra SQL admin:`n$sqlCmd"
    }
}

# --- Validate Managed Identity for Key Vault + SQL ---
Write-Host "Validating Managed Identity access..." -ForegroundColor Cyan
$miPrincipalId = az webapp identity show -g $ResourceGroup -n $WebAppName --query principalId -o tsv
if (-not $miPrincipalId) { throw "Web app system-assigned Managed Identity missing." }
Write-Host "  App Service MI principalId: $miPrincipalId"

$kvScope = az keyvault show -n $KeyVaultName --query id -o tsv
$kvRole = az role assignment list --scope $kvScope --assignee $miPrincipalId --role "Key Vault Secrets User" -o json | ConvertFrom-Json
if (-not $kvRole -or @($kvRole).Count -eq 0) {
    throw "Managed Identity $miPrincipalId is NOT Key Vault Secrets User on $KeyVaultName. Redeploy Bicep role assignment."
}
Write-Host "  Key Vault: MI has 'Key Vault Secrets User' OK"

$sqlConnSetting = az webapp config appsettings list -g $ResourceGroup -n $WebAppName --query "[?name=='Projector__SqlConnectionString'].value | [0]" -o tsv
if ($sqlConnSetting -notmatch 'Authentication=Active Directory Managed Identity') {
    throw "Projector__SqlConnectionString must use Authentication=Active Directory Managed Identity (got: $sqlConnSetting)"
}
Write-Host "  SQL connection string uses Active Directory Managed Identity OK"

$aiExt = az webapp config appsettings list -g $ResourceGroup -n $WebAppName --query "[?name=='ApplicationInsightsAgent_EXTENSION_VERSION'].value | [0]" -o tsv
if ($aiExt -and $aiExt -ne 'disabled') {
    throw "ApplicationInsightsAgent_EXTENSION_VERSION must be 'disabled' (got '$aiExt'). Codeless ~3 wipes Projector__* settings."
}
Write-Host "  App Insights site extension disabled OK (SDK telemetry only)"

$projCount = az webapp config appsettings list -g $ResourceGroup -n $WebAppName --query "[?starts_with(name, 'Projector__')] | length(@)" -o tsv
if ([int]$projCount -lt 5) {
    throw "Expected Projector__* app settings present; found $projCount. Settings bag may have been wiped."
}
Write-Host "  Projector__* settings count: $projCount OK"

try {
    Add-Type -AssemblyName System.Data
    $conn = New-Object System.Data.SqlClient.SqlConnection
    $conn.ConnectionString = "Server=tcp:$sqlFqdn,1433;Database=$SqlDatabaseName;Encrypt=True;TrustServerCertificate=False;"
    $conn.AccessToken = $token
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT dp.name AS principal_name, r.name AS role_name FROM sys.database_role_members rm INNER JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id INNER JOIN sys.database_principals dp ON rm.member_principal_id = dp.principal_id WHERE dp.name = N'$WebAppName'"
    $reader = $cmd.ExecuteReader()
    $roles = @()
    while ($reader.Read()) { $roles += [string]$reader['role_name'] }
    $reader.Close()
    $conn.Close()
    if ($roles.Count -eq 0) {
        throw "SQL contained user [$WebAppName] missing or has no roles."
    }
    Write-Host "  SQL contained user [$WebAppName] roles: $($roles -join ', ')"
}
catch {
    Write-Warning "SQL MI user validation skipped/failed: $_"
}

# Verify vault still exists and original secrets untouched
az keyvault show -n $KeyVaultName -o none
az group show -n $ResourceGroup -o none
$origId = az keyvault secret show --vault-name $KeyVaultName --name ProjectorPSAOauthClientID --query name -o tsv
$origSecret = az keyvault secret show --vault-name $KeyVaultName --name ProjectorPSAOauthSecret --query name -o tsv
if (-not $origId -or -not $origSecret) {
    throw "CRITICAL: Original Projector OAuth secrets missing from Key Vault after deploy."
}

Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Green
Write-Host "PublicBaseUrl:  $($outputs.publicBaseUrl.value)"
Write-Host "MCP URL:        $($outputs.mcpUrl.value)"
Write-Host "Authorize:      $($outputs.oauthAuthorizeUrl.value)"
Write-Host "Token:          $($outputs.oauthTokenUrl.value)"
Write-Host "Projector CB:   $($outputs.projectorCallbackUrl.value)"
Write-Host "Entra CB:       $($outputs.entraCallbackUrl.value)"
Write-Host "App Insights:   $($outputs.appInsightsName.value)"
Write-Host "Entra hop app:  $entraAppId"
Write-Host "MCP OAuth app:  $mcpClientId"
Write-Host "MI principal:   $miPrincipalId"
Write-Host ""
Write-Host "MANUAL: Register Projector OAuth redirect URI:"
Write-Host "  $($outputs.projectorCallbackUrl.value)"
Write-Host "Then run: .\Publish-App.ps1 -SettingsFile $SettingsFile"
Write-Host ""
Write-Host "Hard rules honored: RG and Key Vault were NOT deleted."
Write-Host "Managed Identity: Key Vault secrets + SQL (Entra-only, no SQL password)."
