using System.Diagnostics;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Projector.ApiClient;
using Projector.Application.Persistence;
using Projector.Mcp.Server.Auth;

namespace Projector.Mcp.Server.Hosting;

/// <summary>
/// GET /health/components: checks what the server depends on (configuration, SQL token store, Key Vault, the Projector
/// sign-in host, Application Insights) and answers with one line per component. <c>GET /health</c> stays a plain liveness
/// answer for the App Service health check and only links here: a Projector or SQL outage must not make App Service
/// replace a healthy instance. Anonymous, so the answer has fixed descriptions only (details go to the logs), and one run
/// is shared for <see cref="ComponentHealthRunner.CacheFor"/> so the endpoint can't be used to hammer SQL or Projector.
/// </summary>
public static class ComponentHealth
{
    public const string Path = "/health/components";
    public const string Tag = "component";
    public const string ProbeClient = "HealthProbe";
    internal static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddComponentHealth(this IServiceCollection services)
    {
        services.AddHttpClient(ProbeClient, c => c.Timeout = CheckTimeout);
        services.AddSingleton<ComponentHealthRunner>();
        services.AddHealthChecks()
            .AddCheck<ServiceCheck>("service", tags: [Tag])
            .AddCheck<ConfigurationCheck>("configuration", tags: [Tag])
            .AddCheck<SqlCheck>("sql", tags: [Tag], timeout: CheckTimeout)
            .AddCheck<KeyVaultCheck>("keyVault", tags: [Tag], timeout: CheckTimeout)
            .AddCheck<ProjectorCheck>("projector", tags: [Tag], timeout: CheckTimeout)
            .AddCheck<AppInsightsCheck>("appInsights", tags: [Tag]);
        return services;
    }

    public static void MapComponentHealth(this WebApplication app)
    {
        app.MapGet(Path, async (ComponentHealthRunner runner, HttpContext http) =>
        {
            var (report, checkedAt, cached) = await runner.GetAsync(http.RequestAborted);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                new
                {
                    status = report.Status.ToString(),
                    version = ServerVersion.Current,
                    checkedAt = checkedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    cached,
                    durationMs = (int)report.TotalDuration.TotalMilliseconds,
                    components = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new
                        {
                            status = e.Value.Status.ToString(),
                            durationMs = (int)e.Value.Duration.TotalMilliseconds,
                            description = PublicDescription(e.Value)
                        })
                },
                statusCode: report.Status == HealthStatus.Unhealthy
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK);
        });
    }

    /// <summary>An exception message (host names, identities) stays in the logs; the answer gets a fixed text.</summary>
    private static string? PublicDescription(HealthReportEntry entry) => entry.Exception switch
    {
        null => entry.Description,
        OperationCanceledException => $"No answer within {CheckTimeout.TotalSeconds:0} s.",
        var ex when entry.Description == ex.Message => "Check failed; details are in the server logs.",
        _ => entry.Description
    };
}

/// <summary>Runs the component checks at most once per <see cref="CacheFor"/>; callers in between get the last answer.</summary>
public sealed class ComponentHealthRunner(HealthCheckService health, ILogger<ComponentHealthRunner> logger)
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private sealed record Snapshot(HealthReport Report, DateTimeOffset At);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot? _last;
    private int _runs;

    /// <summary>How many times the checks really ran (tests: <c>GET /health</c> must not run them).</summary>
    public int Runs => _runs;

    public async Task<(HealthReport Report, DateTimeOffset CheckedAt, bool Cached)> GetAsync(CancellationToken ct)
    {
        if (Fresh() is { } hit)
        {
            return (hit.Report, hit.At, true);
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh() is { } waited)
            {
                return (waited.Report, waited.At, true);
            }

            Interlocked.Increment(ref _runs);
            // Not the caller's token: a closed browser tab must not cancel a run other callers wait for.
            var report = await health.CheckHealthAsync(r => r.Tags.Contains(ComponentHealth.Tag), CancellationToken.None);
            foreach (var (name, entry) in report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy))
            {
                logger.LogWarning(entry.Exception, "Component {Component} is {Status}: {Description}",
                    name, entry.Status, entry.Description);
            }

            var snapshot = new Snapshot(report, DateTimeOffset.UtcNow);
            _last = snapshot;
            return (snapshot.Report, snapshot.At, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Snapshot? Fresh() => _last is { } last && DateTimeOffset.UtcNow - last.At < CacheFor ? last : null;
}

/// <summary>The process answers; reports how long it has been up.</summary>
internal sealed class ServiceCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var up = DateTime.Now - Process.GetCurrentProcess().StartTime;
        var text = up.TotalHours >= 1 ? $"{(int)up.TotalHours} h {up.Minutes} min" : $"{up.Minutes} min {up.Seconds} s";
        return Task.FromResult(HealthCheckResult.Healthy($"Server {ServerVersion.Current} up {text}."));
    }
}

/// <summary>
/// The settings a hosted server can't work without. When the App Service settings get wiped (2026-10-05) the server
/// usually doesn't start at all, but a partial wipe shows up here.
/// </summary>
internal sealed class ConfigurationCheck(IOptions<ProjectorOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(o.AccountCode))
        {
            missing.Add("Projector:AccountCode");
        }

        if (KeyVaultSecretsLoader.OnAppService)
        {
            if (o.PublicBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                missing.Add("Projector:PublicBaseUrl");
            }

            if (string.IsNullOrWhiteSpace(o.KeyVaultUri))
            {
                missing.Add("Projector:KeyVaultUri");
            }

            if (!o.UseSqlTokenStore || string.IsNullOrWhiteSpace(o.SqlConnectionString))
            {
                missing.Add("Projector:UseSqlTokenStore / SqlConnectionString");
            }

            if (string.IsNullOrWhiteSpace(o.EntraClientId))
            {
                missing.Add("Projector:EntraClientId");
            }

            if (string.IsNullOrWhiteSpace(o.McpOAuthClientId))
            {
                missing.Add("Projector:McpOAuthClientId");
            }
        }

        return Task.FromResult(missing.Count == 0
            ? HealthCheckResult.Healthy("Required settings are present.")
            : HealthCheckResult.Unhealthy("Missing settings: " + string.Join(", ", missing) + "."));
    }
}

/// <summary>The token store (Projector connections, pending sign-ins) answers.</summary>
internal sealed class SqlCheck(IOptions<ProjectorOptions> options, IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.UseSqlTokenStore)
        {
            return KeyVaultSecretsLoader.OnAppService
                ? HealthCheckResult.Degraded("SQL token store is off: sign-ins are kept in memory and lost on restart.")
                : HealthCheckResult.Healthy("Not used (in-memory token store).");
        }

        using var scope = services.CreateScope();
        if (scope.ServiceProvider.GetService<ProjectorTokenDbContext>() is not { } db)
        {
            return HealthCheckResult.Unhealthy("SQL token store is on but has no connection string.");
        }

        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Token database answers.")
                : HealthCheckResult.Unhealthy("Token database does not answer.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Token database does not answer.", ex);
        }
    }
}

/// <summary>
/// The vault answers to the server's identity: lists the versions of the JWT signing key secret (metadata only, no
/// secret value is read).
/// </summary>
internal sealed class KeyVaultCheck(IOptions<ProjectorOptions> options) : IHealthCheck
{
    private static SecretClient? s_client;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.KeyVaultUri))
        {
            return KeyVaultSecretsLoader.OnAppService
                ? HealthCheckResult.Unhealthy("Key Vault URI is not set.")
                : HealthCheckResult.Healthy("Not used (local settings).");
        }

        // One client for the process, so its credential caches the managed identity token.
        var client = s_client ??= KeyVaultSecretsLoader.CreateClient(o.KeyVaultUri);
        try
        {
            await foreach (var _ in client.GetPropertiesOfSecretVersionsAsync(o.JwtSigningKeySecretName, cancellationToken)
                               .AsPages(pageSizeHint: 1))
            {
                break;
            }

            return HealthCheckResult.Healthy("Vault answers to the server's identity.");
        }
        catch (AuthenticationFailedException ex)
        {
            return HealthCheckResult.Unhealthy("The server's identity can't get a token for the vault.", ex);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            return HealthCheckResult.Unhealthy("The server's identity has no access to the vault.", ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return HealthCheckResult.Unhealthy("The JWT signing key secret is missing in the vault.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Vault does not answer.", ex);
        }
    }
}

/// <summary>
/// The Projector sign-in host answers. Projector API calls need a user's sign-in, so they aren't tried here; their
/// failures show up per tool call in the logs.
/// </summary>
internal sealed class ProjectorCheck(IHttpClientFactory httpFactory, IOptions<ProjectorOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var host = new Uri(new Uri(options.Value.AuthorizeBaseUrl).GetLeftPart(UriPartial.Authority) + "/");
        try
        {
            using var response = await httpFactory.CreateClient(ComponentHealth.ProbeClient)
                .GetAsync(host, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var code = (int)response.StatusCode;
            return code < 500
                ? HealthCheckResult.Healthy($"Projector sign-in host answers (HTTP {code}). API calls need a user and aren't tried here.")
                : HealthCheckResult.Unhealthy($"Projector sign-in host answers HTTP {code}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Projector sign-in host does not answer.", ex);
        }
    }
}

/// <summary>Telemetry is configured. Whether it arrives can only be seen in Application Insights itself.</summary>
internal sealed class AppInsightsCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var configured = !string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
            ?? configuration["ApplicationInsights:ConnectionString"]);
        return Task.FromResult(configured
            ? HealthCheckResult.Healthy("Telemetry is configured (delivery isn't tested here).")
            : KeyVaultSecretsLoader.OnAppService
                ? HealthCheckResult.Degraded("No Application Insights connection string: no telemetry.")
                : HealthCheckResult.Healthy("Not used locally."));
    }
}
