# Copy to deploy.settings.psd1 (git-ignored) and fill in. Used by Deploy-Infrastructure.ps1 and Publish-App.ps1.
@{
    # --- Required ---
    ResourceGroup = 'rg-projector-mcp'          # must already exist
    WebAppName    = 'contoso-projector-mcp'     # globally unique -> https://<WebAppName>.azurewebsites.net
    KeyVaultName  = 'contoso-projector-kv'      # must already exist and hold the Projector OAuth secrets
    SqlServerName = 'contoso-projector-mcp-sql' # globally unique
    AccountCode   = 'Contoso'                   # your Projector account (company) code

    # --- Optional (empty = default) ---
    SubscriptionId     = ''                     # default: current `az account show`
    Location           = ''                     # default: resource group region
    SqlLocation        = ''                     # default: Location
    AppServicePlanName = ''                     # default: <WebAppName>-plan
    AppServiceSku      = 'B1'
    SqlDatabaseName    = 'ProjectorMcp'
    EntraHopAppName    = 'Projector-MCP-EntraHop'     # Entra app for the optional tenant sign-in step
    McpOAuthAppName    = 'Projector-MCP-OAuthClient'  # Entra app used as the static client for M365 Copilot

    # Extra MCP client redirect URIs to allow (e.g. a Copilot Studio connector callback).
    AllowedMcpRedirectUris = @()

    # Folder with your own reference catalogs (*.json) that replace the samples in
    # src/Projector.Mcp.Server/resources/catalogs at publish time. Relative to this file.
    CatalogsPath = ''
}
