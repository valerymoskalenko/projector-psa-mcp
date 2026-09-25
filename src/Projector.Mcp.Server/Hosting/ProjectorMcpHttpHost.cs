using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;
using OpenTelemetry.Trace;
using Projector.ApiClient;
using Projector.Application;
using Projector.Application.Persistence;
using Projector.Mcp.Server.Auth;
using Projector.Mcp.Server.Cli;
using Projector.Mcp.Server.Hosting;
using Projector.Mcp.Server.Tools;

namespace Projector.Mcp.Server;

/// <summary>
/// Builds the Streamable HTTP MCP host. Used by CLI <c>serve --http</c> and tests.
/// </summary>
public static class ProjectorMcpHttpHost
{
    public static async Task<WebApplication> CreateAsync(
        string[]? args = null,
        Action<IWebHostBuilder>? configureWebHost = null,
        CancellationToken cancellationToken = default)
    {
        var onAppService = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

        // App Service must run as Production even if app settings were wiped (defaults localhost otherwise).
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? (onAppService ? "Production" : "Development");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            EnvironmentName = environmentName
        });

        configureWebHost?.Invoke(builder.WebHost);

        if (configureWebHost is null
            && !onAppService
            && !string.Equals(
                System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
                "testhost",
                StringComparison.OrdinalIgnoreCase))
        {
            builder.WebHost.UseUrls("http://127.0.0.1:5180");
        }

        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        // Application Insights via Azure Monitor OpenTelemetry (HTTP + SQL Client dependencies).
        var aiConnection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
            ?? builder.Configuration["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(aiConnection))
        {
            builder.Services.AddOpenTelemetry()
                .UseAzureMonitor(o => o.ConnectionString = aiConnection)
                .WithTracing(t => t.AddSqlClientInstrumentation());
        }

        await ConfigureServicesAsync(builder.Services, builder.Configuration, builder.Environment);

        var projectorOptions = builder.Configuration.GetSection(ProjectorOptions.SectionName).Get<ProjectorOptions>()
            ?? new ProjectorOptions();

        EnsurePublicOriginAllowed(projectorOptions);
        builder.Services.PostConfigure<ProjectorOptions>(EnsurePublicOriginAllowed);

        if (builder.Environment.IsProduction()
            && string.IsNullOrWhiteSpace(projectorOptions.JwtSigningKey))
        {
            throw new InvalidOperationException(
                "Production requires Projector:JwtSigningKey from Key Vault (ProjectorMcpJwtSigningKey).");
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient("EntraToken");
        builder.Services.AddSingleton<McpJwtIssuer>();

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer()
            .AddMcp(options =>
            {
                var baseUrl = projectorOptions.PublicBaseUrl.TrimEnd('/');
                options.ResourceMetadata = new()
                {
                    Resource = projectorOptions.McpAudience.Contains("://", StringComparison.Ordinal)
                        ? projectorOptions.McpAudience
                        : $"{baseUrl}/mcp",
                    AuthorizationServers = { baseUrl },
                    ScopesSupported = { projectorOptions.McpScope, "mcp:tools" }
                };
            });

        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<McpJwtIssuer>((options, issuer) =>
            {
                options.TokenValidationParameters = issuer.CreateValidationParameters();
            });

        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "Projector PSA MCP Server", Version = "0.5.1" };
            })
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .AddAuthorizationFilters()
            .WithToolsFromAssembly()
            .WithRequestFilters(filters => filters.AddCallToolFilter(CopilotToolNameFilter.Filter))
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("McpCors", policy =>
            {
                policy.SetIsOriginAllowed(origin =>
                    {
                        if (string.IsNullOrEmpty(origin) || origin == "null")
                        {
                            return true;
                        }

                        return projectorOptions.AllowedOrigins.Any(a =>
                            string.Equals(a, origin, StringComparison.OrdinalIgnoreCase));
                    })
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        var app = builder.Build();

        projectorOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProjectorOptions>>().Value;

        if (projectorOptions.UseSqlTokenStore)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetService<ProjectorTokenDbContext>();
            if (db is not null)
            {
                await db.Database.EnsureCreatedAsync(cancellationToken);
            }
        }

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                var origin = context.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin)
                    && origin != "null"
                    && !projectorOptions.AllowedOrigins.Any(a =>
                        string.Equals(a, origin, StringComparison.OrdinalIgnoreCase)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Invalid Origin", context.RequestAborted);
                    return;
                }
            }

            await next();
        });

        app.UseCors("McpCors");
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/", () => Results.Json(new
        {
            name = "Projector PSA MCP Server",
            mcp = "/mcp",
            oauthAuthorize = "/oauth/authorize",
            oauthToken = "/oauth/token",
            entraCallback = "/oauth/entra/callback",
            projectorCallback = "/oauth/projector/callback",
            requestedScopes = projectorOptions.RequestedScopes,
            docs = "See README.md and infrastructure/README.md"
        }));

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapOAuthBroker();
        app.MapMcp("/mcp").RequireAuthorization().RequireCors("McpCors");

        return app;
    }

    private static void EnsurePublicOriginAllowed(ProjectorOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return;
        }

        var baseUrl = options.PublicBaseUrl.TrimEnd('/');
        if (options.AllowedOrigins.Any(a => string.Equals(a, baseUrl, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        options.AllowedOrigins = options.AllowedOrigins
            .Append(baseUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task ConfigureServicesAsync(
        IServiceCollection services,
        ConfigurationManager configuration,
        IHostEnvironment? environment = null)
    {
        using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("KeyVault");
        await KeyVaultSecretsLoader.ApplyAsync(configuration, bootstrapLogger, environment);

        services.AddOptions<ProjectorOptions>()
            .Bind(configuration.GetSection(ProjectorOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.AccountCode),
                "Projector:AccountCode is required (your Projector account/company code).")
            .ValidateOnStart();
        services.AddProjectorApplication(configuration);
        services.AddProjectorApiClient();
        services.AddSingleton<ToolCliRunner>();
        services.AddSingleton<ConnectionResolver>();
        services.AddSingleton<Projector.Mcp.Server.Resources.ProjectorLiveResources>();
        services.AddHttpContextAccessor();
    }
}
