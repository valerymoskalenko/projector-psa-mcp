using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Projector.ApiClient;

namespace Projector.Mcp.Server.Auth;

/// <summary>
/// Loads secrets from Azure Key Vault.
/// On App Service: <see cref="ManagedIdentityCredential"/> (system-assigned MI).
/// Locally: <see cref="DefaultAzureCredential"/> (Azure CLI / VS).
/// Never overwrites existing Projector OAuth secret names.
/// </summary>
public static class KeyVaultSecretsLoader
{
    public static async Task ApplyAsync(
        ConfigurationManager configuration,
        ILogger logger,
        IHostEnvironment? environment = null,
        CancellationToken cancellationToken = default)
    {
        var section = configuration.GetSection(ProjectorOptions.SectionName);
        var vaultUri = section["KeyVaultUri"];
        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            logger.LogInformation("Projector:KeyVaultUri not set; skipping Key Vault secret load.");
            return;
        }

        var clientIdName = section["ClientIdSecretName"] ?? "ProjectorPSAOauthClientID";
        var clientSecretName = section["ClientSecretSecretName"] ?? "ProjectorPSAOauthSecret";
        var jwtSecretName = section["JwtSigningKeySecretName"] ?? "ProjectorMcpJwtSigningKey";
        var encSecretName = section["TokenEncryptionKeySecretName"] ?? "ProjectorTokenEncryptionKey";
        var entraSecretName = section["EntraClientSecretSecretName"] ?? "ProjectorMcpEntraClientSecret";

        var onAppService = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

        // Prefer system-assigned Managed Identity on App Service (Key Vault + SQL use the same MI).
        // Locally: never probe IMDS — unreachable 169.254.169.254 can throw AuthenticationFailedException
        // and abort DefaultAzureCredential before Azure CLI / VS credentials run.
        TokenCredential credential = onAppService
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeInteractiveBrowserCredential = true,
                ExcludeManagedIdentityCredential = true,
                ExcludeWorkloadIdentityCredential = true,
                ExcludeAzureDeveloperCliCredential = true
            });

        var client = new SecretClient(new Uri(vaultUri), credential);

        logger.LogInformation(
            "Loading secrets from Key Vault {VaultUri} via {Credential}.",
            vaultUri,
            onAppService ? "ManagedIdentityCredential" : "DefaultAzureCredential");

        var overlay = new Dictionary<string, string?>();

        var clientId = await client.GetSecretAsync(clientIdName, cancellationToken: cancellationToken);
        var clientSecret = await client.GetSecretAsync(clientSecretName, cancellationToken: cancellationToken);
        overlay[$"{ProjectorOptions.SectionName}:ClientId"] = clientId.Value.Value.Trim();
        overlay[$"{ProjectorOptions.SectionName}:ClientSecret"] = clientSecret.Value.Value.Trim();

        try
        {
            var jwt = await client.GetSecretAsync(jwtSecretName, cancellationToken: cancellationToken);
            overlay[$"{ProjectorOptions.SectionName}:JwtSigningKey"] = jwt.Value.Value.Trim();
        }
        catch (Exception ex) when (environment?.IsProduction() == true)
        {
            throw new InvalidOperationException(
                $"Production requires Key Vault secret '{jwtSecretName}'. {ex.Message}", ex);
        }

        try
        {
            var enc = await client.GetSecretAsync(encSecretName, cancellationToken: cancellationToken);
            overlay[$"{ProjectorOptions.SectionName}:TokenEncryptionKey"] = enc.Value.Value.Trim();
        }
        catch (Exception ex) when (environment?.IsProduction() == true)
        {
            throw new InvalidOperationException(
                $"Production requires Key Vault secret '{encSecretName}'. {ex.Message}", ex);
        }

        try
        {
            var entraSecret = await client.GetSecretAsync(entraSecretName, cancellationToken: cancellationToken);
            overlay[$"{ProjectorOptions.SectionName}:EntraClientSecret"] = entraSecret.Value.Value.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Optional Entra client secret '{Name}' not loaded.", entraSecretName);
        }

        try
        {
            var entraClientId = await client.GetSecretAsync("ProjectorMcpEntraClientId", cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(section["EntraClientId"]))
            {
                overlay[$"{ProjectorOptions.SectionName}:EntraClientId"] = entraClientId.Value.Value.Trim();
            }
        }
        catch
        {
            // optional when set via App Settings
        }

        try
        {
            var mcpClientId = await client.GetSecretAsync("ProjectorMcpOAuthClientId", cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(section["McpOAuthClientId"]))
            {
                overlay[$"{ProjectorOptions.SectionName}:McpOAuthClientId"] = mcpClientId.Value.Value.Trim();
            }
        }
        catch
        {
            // optional
        }

        overlay[$"{ProjectorOptions.SectionName}:KeyVaultLoaded"] = "true";
        configuration.AddInMemoryCollection(overlay);

        logger.LogInformation(
            "Key Vault secrets loaded (Projector client id length {ClientIdLength}).",
            clientId.Value.Value.Trim().Length);
    }
}
