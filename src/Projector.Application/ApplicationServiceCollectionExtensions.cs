using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Projector.Application.Auth;
using Projector.Application.Persistence;
using Projector.Application.Resources;
using Projector.Application.Tools;
using Projector.Domain.Auth;

namespace Projector.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddProjectorApplication(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddMemoryCache();
        services.AddSingleton<TokenEncryptionService>();
        services.AddSingleton<ILocalOAuthSessionStore, DpapiLocalOAuthSessionStore>();
        services.AddSingleton<ProjectorConnectionService>();
        services.AddSingleton<LocalOAuthLoginService>();
        services.AddSingleton<ResourceService>();
        services.AddSingleton<ProjectorToolService>();
        services.AddSingleton<IValidator<Contracts.Resources.ListResourcesRequest>, ListResourcesValidator>();
        services.AddSingleton<IValidator<Contracts.Resources.GetResourceRequest>, GetResourceValidator>();

        // Prefer bound options so App Settings / env (Projector__*) match IOptions later used by the host.
        var projector = configuration?.GetSection("Projector");
        var useSql = string.Equals(projector?["UseSqlTokenStore"], "true", StringComparison.OrdinalIgnoreCase)
            || projector?.GetValue<bool>("UseSqlTokenStore") == true;
        var sqlConnectionString = projector?["SqlConnectionString"];

        if (useSql && !string.IsNullOrWhiteSpace(sqlConnectionString))
        {
            services.AddDbContext<ProjectorTokenDbContext>(options =>
                options.UseSqlServer(sqlConnectionString));
            services.AddSingleton<IProjectorConnectionStore, SqlProjectorConnectionStore>();
            services.AddSingleton<IPendingAuthorizationStore, SqlPendingAuthorizationStore>();
        }
        else
        {
            // App Service appsettings can be wiped by portal App Insights applies; keep the site up.
            services.AddSingleton<IProjectorConnectionStore, InMemoryProjectorConnectionStore>();
            services.AddSingleton<IPendingAuthorizationStore, InMemoryPendingAuthorizationStore>();
        }

        return services;
    }
}
