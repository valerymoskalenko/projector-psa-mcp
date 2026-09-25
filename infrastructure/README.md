# Infrastructure

| File | Purpose |
|------|---------|
| `main.bicep` | App Service, Azure SQL, Log Analytics, Application Insights, Key Vault role assignment (Incremental deploy; never declares the Key Vault) |
| `deploy.settings.example.psd1` | Template for your deployment settings; copy to `deploy.settings.psd1` (git-ignored) |
| `DeploySettings.ps1` | Loads and validates the settings file (shared by both scripts) |
| `Deploy-Infrastructure.ps1` | Entra apps, Key Vault secrets, Bicep deploy, app settings, SQL managed-identity user |
| `Publish-App.ps1` | Build, optional catalog overlay, zip deploy, app settings merge, health check |

Step-by-step guide: [../docs/deploy-azure.md](../docs/deploy-azure.md).
