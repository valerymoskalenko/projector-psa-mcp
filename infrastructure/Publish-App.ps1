<#
.SYNOPSIS
  Build and zip-deploy the Projector MCP server to Azure App Service.

.PARAMETER SettingsFile
  Deployment settings (.psd1). Default: deploy.settings.psd1 next to this script.

.PARAMETER Configuration
  Build configuration. Default: Release.
#>
[CmdletBinding()]
param(
    [string] $SettingsFile = '',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
. (Join-Path $scriptDir 'DeploySettings.ps1')
if (-not $SettingsFile) { $SettingsFile = Join-Path $scriptDir 'deploy.settings.psd1' }
$cfg = Import-DeploySettings -Path $SettingsFile

$ResourceGroup = $cfg.ResourceGroup
$SubscriptionId = $cfg.SubscriptionId
$WebAppName = $cfg.WebAppName
$KeyVaultName = $cfg.KeyVaultName

Write-Host "=== Projector MCP Publish-App ===" -ForegroundColor Cyan

$accountJson = az account show -o json 2>$null
if (-not $accountJson) {
    throw "Not logged in to Azure CLI. Run: az login"
}
if ($SubscriptionId) {
    az account set --subscription $SubscriptionId | Out-Null
}

# Safety: never delete RG / vault
az group show -n $ResourceGroup -o none
az keyvault show -n $KeyVaultName -o none

$project = Join-Path $repoRoot 'src\Projector.Mcp.Server\Projector.Mcp.Server.csproj'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$zipPath = Join-Path $repoRoot 'artifacts\projector-mcp.zip'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $zipPath) -Force | Out-Null

Write-Host "dotnet publish $project ..."
dotnet publish $project -c $Configuration -o $publishDir --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if ($cfg.CatalogsPath) {
    if (-not (Test-Path $cfg.CatalogsPath)) { throw "CatalogsPath not found: $($cfg.CatalogsPath)" }
    $catalogTarget = Join-Path $publishDir 'resources\catalogs'
    $catalogFiles = Get-ChildItem -Path $cfg.CatalogsPath -Filter '*.json' -File
    Write-Host "Replacing sample catalogs with $($catalogFiles.Count) file(s) from $($cfg.CatalogsPath)"
    $catalogFiles | Copy-Item -Destination $catalogTarget -Force
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "Creating zip $zipPath (forward-slash entries for Linux)..."
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# ZipFile.CreateFromDirectory on Windows embeds backslashes; Linux Kudu rsync then fails (EINVAL).
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
try {
    $archive = new-object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Resolve-Path $publishDir).Path.TrimEnd('\')
        Get-ChildItem -Path $publishDir -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
}
finally { $zipStream.Dispose() }

# az webapp deploy can fail (Kudu 400) when the path contains spaces; copy to a short temp path.
$deployZip = Join-Path $env:TEMP 'projector-mcp-deploy.zip'
Copy-Item -Path $zipPath -Destination $deployZip -Force

Write-Host "Deploying zip to $WebAppName ..."
$publicBaseUrl = "https://$WebAppName.azurewebsites.net"
$tenantId = (az account show --query tenantId -o tsv)
$entraAppId = az keyvault secret show --vault-name $KeyVaultName --name ProjectorMcpEntraClientId --query value -o tsv 2>$null
$mcpClientId = az keyvault secret show --vault-name $KeyVaultName --name ProjectorMcpOAuthClientId --query value -o tsv 2>$null
$sqlConn = "Server=tcp:$($cfg.SqlServerName).database.windows.net,1433;Database=$($cfg.SqlDatabaseName);Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;"
$teamsRedirect = 'https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect'
# Preserve APIM / BYO redirects already on the site (do not wipe to Teams-only).
$existingRedirectCsv = az webapp config appsettings list -g $ResourceGroup -n $WebAppName --query "[?name=='Projector__AllowedMcpRedirectUrisCsv'].value | [0]" -o tsv 2>$null
$redirectSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$redirectSet.Add($teamsRedirect)
foreach ($u in @($cfg.AllowedMcpRedirectUris)) {
    if ($u) { [void]$redirectSet.Add($u.Trim()) }
}
if ($existingRedirectCsv) {
    foreach ($p in ($existingRedirectCsv -split '[;,]')) {
        $t = $p.Trim()
        if ($t) { [void]$redirectSet.Add($t) }
    }
}
# The server splits this setting on ';' (and ',').
$allowedRedirectCsv = ($redirectSet | Sort-Object) -join ';'

# Prefer connection string from the Bicep-managed App Insights resource (same PUT bag as Projector settings).
$aiConn = ''
$aiKey = ''
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$aiJson = az monitor app-insights component show -g $ResourceGroup -a "${WebAppName}-ai" -o json 2>$null
$ErrorActionPreference = $prev
if ($LASTEXITCODE -eq 0 -and $aiJson) {
    $ai = $aiJson | ConvertFrom-Json
    $aiConn = $ai.connectionString
    $aiKey = $ai.instrumentationKey
}

# Merge full critical settings — portal App Insights applies have wiped Projector__* before.
$settingsArgs = @(
    "SCM_DO_BUILD_DURING_DEPLOYMENT=false",
    "WEBSITE_RUN_FROM_PACKAGE=0",
    "ASPNETCORE_ENVIRONMENT=Production",
    "WEBSITE_HEALTHCHECK_PATH=/health",
    "Projector__AccountCode=$($cfg.AccountCode)",
    "Projector__KeyVaultUri=https://$KeyVaultName.vault.azure.net/",
    "Projector__ClientIdSecretName=ProjectorPSAOauthClientID",
    "Projector__ClientSecretSecretName=ProjectorPSAOauthSecret",
    "Projector__JwtSigningKeySecretName=ProjectorMcpJwtSigningKey",
    "Projector__TokenEncryptionKeySecretName=ProjectorTokenEncryptionKey",
    "Projector__PublicBaseUrl=$publicBaseUrl",
    "Projector__McpAudience=projector-mcp",
    "Projector__RedirectUri=$publicBaseUrl/oauth/projector/callback",
    "Projector__McpScope=projector.mcp",
    "Projector__RequestedScopes=allowFullPermissions",
    "Projector__SqlConnectionString=$sqlConn",
    "Projector__UseSqlTokenStore=true",
    "Projector__AllowedMcpRedirectUrisCsv=$allowedRedirectCsv",
    "Projector__EntraTenantId=$tenantId"
)
if ($entraAppId) { $settingsArgs += "Projector__EntraClientId=$entraAppId" }
if ($mcpClientId) { $settingsArgs += "Projector__McpOAuthClientId=$mcpClientId" }
if ($aiConn) {
    $settingsArgs += "APPLICATIONINSIGHTS_CONNECTION_STRING=$aiConn"
    $settingsArgs += "APPINSIGHTS_INSTRUMENTATIONKEY=$aiKey"
    # Never enable ~3 — the codeless site extension rewrites/wipes app settings (see infrastructure/README.md).
    $settingsArgs += "ApplicationInsightsAgent_EXTENSION_VERSION=disabled"
    $settingsArgs += "XDT_MicrosoftApplicationInsights_Mode=disabled"
}

az webapp config appsettings set `
    --resource-group $ResourceGroup `
    --name $WebAppName `
    --settings @settingsArgs `
    -o none
if ($LASTEXITCODE -ne 0) { throw "Failed to merge App Service settings" }

# Validate MI wiring still present after settings merge
$miPrincipalId = az webapp identity show -g $ResourceGroup -n $WebAppName --query principalId -o tsv
$sqlCheck = az webapp config appsettings list -g $ResourceGroup -n $WebAppName --query "[?name=='Projector__SqlConnectionString'].value | [0]" -o tsv
if ($sqlCheck -notmatch 'Managed Identity') {
    throw "SQL connection string must use Managed Identity after publish."
}
Write-Host "Managed Identity principalId: $miPrincipalId (SQL uses AD Managed Identity)"

az webapp config set --resource-group $ResourceGroup --name $WebAppName --startup-file "dotnet Projector.Mcp.Server.dll serve --http" -o none

$creds = az webapp deployment list-publishing-credentials --resource-group $ResourceGroup --name $WebAppName -o json | ConvertFrom-Json
$pair = "$($creds.publishingUserName):$($creds.publishingPassword)"
$b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($pair))
$scm = "https://$WebAppName.scm.azurewebsites.net/api/zipdeploy?isAsync=false"
Write-Host "Kudu zipdeploy $scm"
# The settings merge above restarts the site (and Kudu), so the first upload can fail; retry.
$deployed = $false
for ($attempt = 1; $attempt -le 4 -and -not $deployed; $attempt++) {
    curl.exe -sS -X POST $scm -H "Authorization: Basic $b64" -H "Content-Type: application/octet-stream" --data-binary "@$deployZip" -f -w "\nHTTP:%{http_code}\n"
    if ($LASTEXITCODE -eq 0) { $deployed = $true }
    elseif ($attempt -lt 4) {
        Write-Warning "Kudu zipdeploy attempt $attempt failed; retrying in 20 s..."
        Start-Sleep -Seconds 20
    }
}
if (-not $deployed) { throw "Kudu zipdeploy failed" }

Write-Host "Restarting web app..."
az webapp restart --resource-group $ResourceGroup --name $WebAppName -o none

$baseUrl = "https://$WebAppName.azurewebsites.net"
Write-Host "Waiting for health at $baseUrl/health ..."
$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri "$baseUrl/health" -UseBasicParsing -TimeoutSec 15
        if ($resp.StatusCode -eq 200) {
            Write-Host "Health OK: $($resp.Content)" -ForegroundColor Green
            $ok = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 5
    }
}
if (-not $ok) {
    Write-Warning "Health check did not return 200 yet. Inspect logs: az webapp log tail -g $ResourceGroup -n $WebAppName"
}

Write-Host ""
Write-Host "Published: $baseUrl"
Write-Host "MCP:       $baseUrl/mcp"
Write-Host "Key Vault $KeyVaultName and RG $ResourceGroup were not deleted."
