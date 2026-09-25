using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Projector.ApiClient.OAuth;
using Projector.ApiClient.Xml;
using Projector.Domain.Auth;

namespace Projector.ApiClient;

public static class ApiClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers live HTTP clients for Projector OAuth + XML PWS.
    /// </summary>
    public static IServiceCollection AddProjectorApiClient(this IServiceCollection services)
    {
        services.AddHttpClient<IProjectorTokenClient, ProjectorTokenClient>()
            .AddStandardResilienceHandler();

        // Shared SOAP transport.
        services.AddHttpClient<ProjectorSoapHttp>()
            .AddStandardResilienceHandler(ConfigureSoapRetry);

        // Existing resource list/get (keeps working for MCP tools already wired).
        services.AddTransient<IProjectorResourceClient, ProjectorXmlResourceClient>();

        // Full SOAP surface.
        services.AddHttpClient<ProjectorSoapClient>()
            .AddStandardResilienceHandler(ConfigureSoapRetry);
        services.AddTransient<IProjectorSoapClient>(sp => sp.GetRequiredService<ProjectorSoapClient>());
        services.AddTransient<IProjectorUserClient>(sp => sp.GetRequiredService<ProjectorSoapClient>());

        return services;
    }

    private static void ConfigureSoapRetry(HttpStandardResilienceOptions options)
    {
        // Projector returns HTTP 500 for business SOAP faults — do not retry those.
        options.Retry.ShouldHandle = args =>
        {
            if (args.Outcome.Exception is not null)
            {
                return PredicateResult.True();
            }

            var status = args.Outcome.Result?.StatusCode;
            return new ValueTask<bool>(
                status is System.Net.HttpStatusCode.RequestTimeout
                    or System.Net.HttpStatusCode.TooManyRequests
                    or System.Net.HttpStatusCode.BadGateway
                    or System.Net.HttpStatusCode.ServiceUnavailable
                    or System.Net.HttpStatusCode.GatewayTimeout);
        };
    }
}
