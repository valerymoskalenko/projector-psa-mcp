<#
.SYNOPSIS
  Loads deployment settings (a .psd1 file) shared by Deploy-Infrastructure.ps1 and Publish-App.ps1.

.DESCRIPTION
  Copy deploy.settings.example.psd1 to deploy.settings.psd1 (git-ignored) and fill in your values,
  or pass -SettingsFile to either script. Empty optional values get the defaults below.
#>

function Import-DeploySettings {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path $Path)) {
        throw "Deployment settings file not found: $Path. Copy infrastructure/deploy.settings.example.psd1 to deploy.settings.psd1 and fill it in, or pass -SettingsFile."
    }

    $s = Import-PowerShellDataFile -Path $Path

    foreach ($required in 'ResourceGroup', 'WebAppName', 'KeyVaultName', 'SqlServerName', 'AccountCode') {
        if ([string]::IsNullOrWhiteSpace($s[$required])) {
            throw "Deployment setting '$required' is required ($Path)."
        }
    }

    $defaults = [ordered]@{
        SubscriptionId         = ''
        Location               = ''
        SqlLocation            = ''
        AppServicePlanName     = "$($s.WebAppName)-plan"
        AppServiceSku          = 'B1'
        SqlDatabaseName        = 'ProjectorMcp'
        EntraHopAppName        = 'Projector-MCP-EntraHop'
        McpOAuthAppName        = 'Projector-MCP-OAuthClient'
        AllowedMcpRedirectUris = @()
        CatalogsPath           = ''
    }
    foreach ($key in $defaults.Keys) {
        if (-not $s.ContainsKey($key) -or $null -eq $s[$key] -or ($s[$key] -is [string] -and [string]::IsNullOrWhiteSpace($s[$key]))) {
            $s[$key] = $defaults[$key]
        }
    }

    # Relative CatalogsPath is resolved against the settings file's folder.
    if ($s.CatalogsPath -and -not [System.IO.Path]::IsPathRooted($s.CatalogsPath)) {
        $s.CatalogsPath = Join-Path (Split-Path -Parent (Resolve-Path $Path)) $s.CatalogsPath
    }

    return $s
}
