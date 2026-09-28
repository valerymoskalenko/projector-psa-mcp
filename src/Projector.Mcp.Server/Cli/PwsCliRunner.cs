using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Projector.ApiClient.Xml;
using Projector.Application.Auth;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.Mcp.Server.Cli;

/// <summary>
/// Dev only, read-only: posts a raw PWS request body with the local OAuth session and prints the response XML.
/// Used to check real response shapes (new fields, flags) before writing envelope builders and parsers.
/// Locked down so it can't change Projector data by accident: it runs only in the Development environment and sends
/// only read methods (PwsGet*, PwsSearch*); saves, deletes, submits and approvals are refused before any call.
/// </summary>
public sealed class PwsCliRunner
{
    private static readonly string[] ReadPrefixes = ["PwsGet", "PwsSearch"];

    private readonly LocalOAuthLoginService _login;
    private readonly ProjectorSoapHttp _soap;
    private readonly IHostEnvironment _environment;

    public PwsCliRunner(LocalOAuthLoginService login, ProjectorSoapHttp soap, IHostEnvironment environment)
    {
        _login = login;
        _soap = soap;
        _environment = environment;
    }

    /// <summary>True for read methods only: PwsGet* and PwsSearch* (ordinal, so "pwssave…" can't slip through).</summary>
    internal static bool IsReadMethod(string? method) =>
        !string.IsNullOrWhiteSpace(method)
        && ReadPrefixes.Any(p => method.Trim().StartsWith(p, StringComparison.Ordinal)
            && method.Trim().Length > p.Length
            && char.IsUpper(method.Trim()[p.Length]));

    public async Task<int> RunAsync(string method, string bodyFile, CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            Console.Error.WriteLine(
                $"pws is a development tool and runs only with ASPNETCORE_ENVIRONMENT=Development (now: {_environment.EnvironmentName}).");
            return 2;
        }

        if (!IsReadMethod(method))
        {
            Console.Error.WriteLine(
                $"pws sends read methods only (PwsGet..., PwsSearch...); '{method}' was refused and nothing was sent. " +
                "Change Projector data through the MCP tools or in Projector.");
            return 2;
        }

        var text = await File.ReadAllTextAsync(bodyFile, cancellationToken);
        var wrapper = XElement.Parse(
            $"<x xmlns:pws=\"{SoapNamespaces.Pws}\" xmlns:req=\"{SoapNamespaces.Req}\" xmlns:com=\"{SoapNamespaces.Com}\" " +
            $"xmlns:tim=\"{SoapNamespaces.Tim}\" xmlns:sch=\"{SoapNamespaces.Sch}\">{text}</x>");
        var body = wrapper.Elements().Single();
        if (!string.Equals(body.Name.LocalName, method.Trim(), StringComparison.Ordinal))
        {
            // The SOAP action names the method, but the body element is what Projector executes.
            Console.Error.WriteLine(
                $"The body element <{body.Name.LocalName}> does not match the method '{method}'; nothing was sent.");
            return 2;
        }

        ProjectorConnection connection;
        try
        {
            connection = await _login.LoginAsync(forceLogin: false, openBrowser: false, cancellationToken);
        }
        catch (ProjectorAuthorizationException)
        {
            Console.Error.WriteLine("No usable OAuth cache. Run: auth login");
            return 2;
        }

        body = XElement.Parse(body.ToString().Replace("{{ticket}}", connection.SessionTicket, StringComparison.Ordinal));
        try
        {
            var response = await _soap.PostWcfAsync(connection, method.Trim(), body, cancellationToken);
            Console.WriteLine(response.ToString());
            return 0;
        }
        catch (ProjectorApiException ex)
        {
            Console.WriteLine($"ProjectorApiException code={ex.ErrorCode}: {ex.Message}");
            return 1;
        }
    }
}
