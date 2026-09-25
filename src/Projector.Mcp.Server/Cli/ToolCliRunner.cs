using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Projector.Application.Auth;
using Projector.Domain.Common;
using Projector.Domain.Exceptions;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Cli;

/// <summary>
/// Invokes application services for a single tool from the command line.
/// Args are --snake-case or PascalCase flags matching tools.json (e.g. --resource-id 10001).
/// </summary>
public sealed class ToolCliRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IServiceProvider _services;
    private readonly ConnectionResolver _connections;
    private readonly LocalOAuthLoginService _login;
    private readonly ILogger<ToolCliRunner> _logger;

    public ToolCliRunner(
        IServiceProvider services,
        ConnectionResolver connections,
        LocalOAuthLoginService login,
        ILogger<ToolCliRunner> logger)
    {
        _services = services;
        _connections = connections;
        _login = login;
        _logger = logger;
    }

    public async Task<int> RunAsync(string toolName, string[] args, CancellationToken cancellationToken)
    {
        // Ensure cache is loaded into in-memory store (refresh if needed).
        try
        {
            await _login.LoginAsync(forceLogin: false, openBrowser: false, cancellationToken);
        }
        catch (ProjectorAuthorizationException)
        {
            Console.Error.WriteLine("No usable OAuth cache. Run: auth login");
            return 2;
        }

        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var map = ParseArgs(args);
        var canonical = ToolCatalog.Canonicalize(toolName);

        try
        {
            var result = await ToolCatalog.InvokeAsync(_services, canonical, connectionId, map, cancellationToken);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} failed", canonical);
            var (code, message) = ex switch
            {
                ProjectorAuthorizationException auth => ("authorization_error", SecretRedactor.Redact(auth.Message)),
                ProjectorApiException api => (api.ErrorCode ?? "projector_error", SecretRedactor.Redact(api.Message)),
                FluentValidation.ValidationException validation => ("validation_error", SecretRedactor.Redact(validation.Message)),
                ArgumentException arg => ("invalid_argument", SecretRedactor.Redact(arg.Message)),
                _ => ("error", "An unexpected error occurred.")
            };
            var err = JsonSerializer.Serialize(new { error = code, message, isError = true }, JsonOptions);
            Console.WriteLine(err);
            return 1;
        }
    }

    internal static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-'))
            {
                continue;
            }

            var key = a.TrimStart('-').Replace('-', '_');
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                map[key] = args[++i];
            }
            else
            {
                map[key] = "true";
            }
        }

        return map;
    }
}
