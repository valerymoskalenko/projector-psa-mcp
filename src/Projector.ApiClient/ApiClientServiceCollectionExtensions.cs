using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Projector.ApiClient.OAuth;
using Projector.ApiClient.Xml;
using Projector.Domain.Auth;

namespace Projector.ApiClient;

public static class ApiClientServiceCollectionExtensions
{
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Registers live HTTP clients for Projector OAuth + XML PWS.
    /// </summary>
    public static IServiceCollection AddProjectorApiClient(this IServiceCollection services)
    {
        services.AddHttpClient<IProjectorTokenClient, ProjectorTokenClient>()
            .AddStandardResilienceHandler();

        // Per-user cap on concurrent Projector calls, shared by every SOAP transport.
        services.AddSingleton<ProjectorCallLimiter>();

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

        // Writes: no resilience handler, so a save is never retried (a timed-out save may have committed).
        // One attempt, bounded below Copilot's own tool timeout.
        services.AddHttpClient<ProjectorSoapWriteHttp>(client => client.Timeout = WriteTimeout);
        services.AddTransient<IProjectorTimeEntryClient, ProjectorTimeEntryClient>();

        return services;
    }

    /// <summary>
    /// Reads Projector can take longer than the standard 10 s attempt to answer. They get one long attempt and no
    /// retry: 3 × 10 s gave up on a list that takes ~12 s (list_engagements, 13 of 52 calls over 8 s in 14 days).
    /// </summary>
    public static readonly TimeSpan LongReadTimeout = TimeSpan.FromSeconds(25);

    private static readonly HashSet<string> LongReadActions = new(StringComparer.Ordinal) { "PwsGetEngagementList" };

    /// <summary>True when the request is a SOAP call listed in <see cref="LongReadActions"/>.</summary>
    internal static bool IsLongRead(HttpRequestMessage? request) =>
        request is not null
        && request.Headers.TryGetValues("SOAPAction", out var values)
        && values.Any(v => LongReadActions.Contains(ProjectorSoapHttp.SoapActionName(v)));

    /// <summary>The attempt timeout for a request: <see cref="LongReadTimeout"/> for a long read, else the standard one.</summary>
    internal static TimeSpan AttemptTimeoutFor(HttpRequestMessage? request, TimeSpan standard) =>
        IsLongRead(request) ? LongReadTimeout : standard;

    private static void ConfigureSoapRetry(HttpStandardResilienceOptions options)
    {
        var standardAttempt = options.AttemptTimeout.Timeout;
        options.AttemptTimeout.TimeoutGenerator = args =>
            new ValueTask<TimeSpan>(AttemptTimeoutFor(args.Context.GetRequestMessage(), standardAttempt));

        // Projector returns HTTP 500 for business SOAP faults — do not retry those.
        options.Retry.ShouldHandle = args =>
        {
            if (IsLongRead(args.Context.GetRequestMessage()))
            {
                return PredicateResult.False();
            }

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
