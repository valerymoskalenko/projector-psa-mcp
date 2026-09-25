using System.CommandLine;
using System.Reflection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Projector.Application.Auth;
using Projector.Mcp.Server.Cli;
using Projector.Mcp.Server.Hosting;
using Projector.Mcp.Server.Tools;
using ProjectorMcpHttpHost = Projector.Mcp.Server.ProjectorMcpHttpHost;

// WebApplicationFactory runs under testhost and must reach WebApplication.CreateBuilder.
var underTestHost = string.Equals(
    Assembly.GetEntryAssembly()?.GetName().Name,
    "testhost",
    StringComparison.OrdinalIgnoreCase);

if (underTestHost)
{
    var app = await ProjectorMcpHttpHost.CreateAsync(args);
    await app.RunAsync();
    return 0;
}

var root = new RootCommand("Projector PSA MCP Server — OAuth-only local MCP for Projector Web Services 2.0");

var authCommand = new Command("auth", "Manage local Projector OAuth session (DPAPI cache)");
var loginCommand = new Command("login", "Interactive PKCE login (writes %LOCALAPPDATA%\\ProjectorMcp\\oauth-sessions)");
var forceOption = new Option<bool>("--force") { Description = "Force a fresh browser login" };
loginCommand.Options.Add(forceOption);
loginCommand.SetAction(async (parseResult, ct) =>
{
    var force = parseResult.GetValue(forceOption);
    return await RunWithHostAsync(async sp =>
    {
        var login = sp.GetRequiredService<LocalOAuthLoginService>();
        var connection = await login.LoginAsync(forceLogin: force, openBrowser: true, ct);
        Console.Error.WriteLine($"Logged in. connectionId={connection.ConnectionId} granted={connection.GrantedScope}");
        return 0;
    }, ct);
});
var logoutCommand = new Command("logout", "Revoke and clear the local OAuth cache");
logoutCommand.SetAction(async (_, ct) =>
{
    return await RunWithHostAsync(async sp =>
    {
        var login = sp.GetRequiredService<LocalOAuthLoginService>();
        await login.LogoutAsync(ct);
        Console.Error.WriteLine("Logged out and cleared OAuth cache.");
        return 0;
    }, ct);
});
authCommand.Subcommands.Add(loginCommand);
authCommand.Subcommands.Add(logoutCommand);
root.Subcommands.Add(authCommand);

var toolCommand = new Command("tool", "Run a single MCP tool against the local OAuth cache (JSON on stdout)");
var toolNameArg = new Argument<string>("name") { Description = "Canonical or alias tool name (e.g. list_upcoming_pto)" };
toolCommand.Arguments.Add(toolNameArg);
var toolArgs = new Argument<string[]>("args") { Arity = ArgumentArity.ZeroOrMore };
toolCommand.Arguments.Add(toolArgs);
toolCommand.SetAction(async (parseResult, ct) =>
{
    var name = parseResult.GetValue(toolNameArg)!;
    var cliArgs = parseResult.GetValue(toolArgs) ?? [];
    return await RunWithHostAsync(async sp =>
    {
        var runner = sp.GetRequiredService<ToolCliRunner>();
        return await runner.RunAsync(name, cliArgs, ct);
    }, ct);
});
root.Subcommands.Add(toolCommand);

var serveCommand = new Command("serve", "Run the MCP server");
var stdioOption = new Option<bool>("--stdio") { Description = "Serve over stdio (logs on stderr)" };
var httpOption = new Option<bool>("--http") { Description = "Serve Streamable HTTP on 127.0.0.1:5180" };
serveCommand.Options.Add(stdioOption);
serveCommand.Options.Add(httpOption);
serveCommand.SetAction(async (parseResult, ct) =>
{
    var stdio = parseResult.GetValue(stdioOption);
    var http = parseResult.GetValue(httpOption);
    if (!stdio && !http)
    {
        http = true;
    }

    if (stdio && http)
    {
        Console.Error.WriteLine("Choose either --stdio or --http, not both.");
        return 1;
    }

    if (stdio)
    {
        return await RunStdioAsync(ct);
    }

    var app = await ProjectorMcpHttpHost.CreateAsync(cancellationToken: ct);
    await app.RunAsync(ct);
    return 0;
});
root.Subcommands.Add(serveCommand);

root.SetAction(async (parseResult, ct) =>
{
    // App Service / bare `dotnet Projector.Mcp.Server.dll` has no args — default to HTTP MCP.
    Console.Error.WriteLine("No command specified; starting HTTP MCP server (serve --http).");
    var app = await ProjectorMcpHttpHost.CreateAsync(cancellationToken: ct);
    await app.RunAsync(ct);
    return 0;
});

return await root.Parse(args).InvokeAsync();

static async Task<int> RunWithHostAsync(Func<IServiceProvider, Task<int>> action, CancellationToken ct)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [],
        EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"
    });
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    await ProjectorMcpHttpHost.ConfigureServicesAsync(builder.Services, builder.Configuration, builder.Environment);
    using var host = builder.Build();
    return await action(host.Services);
}

static async Task<int> RunStdioAsync(CancellationToken ct)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"
    });
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    await ProjectorMcpHttpHost.ConfigureServicesAsync(builder.Services, builder.Configuration, builder.Environment);

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "Projector PSA MCP Server", Version = "0.5.1" };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithRequestFilters(filters => filters.AddCallToolFilter(CopilotToolNameFilter.Filter))
        .WithResourcesFromAssembly()
        .WithPromptsFromAssembly();

    var host = builder.Build();
    await host.RunAsync(ct);
    return 0;
}

/// <summary>Marker for WebApplicationFactory / test discovery.</summary>
public partial class Program;
