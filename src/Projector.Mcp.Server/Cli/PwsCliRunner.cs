using System.Xml.Linq;
using Projector.ApiClient.Xml;
using Projector.Application.Auth;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.Mcp.Server.Cli;

/// <summary>
/// Dev only: posts a raw PWS request body with the local OAuth session and prints the response XML.
/// Used to capture real response shapes for fixtures before writing envelope builders and parsers.
/// </summary>
public sealed class PwsCliRunner
{
    private readonly LocalOAuthLoginService _login;
    private readonly ProjectorSoapHttp _soap;

    public PwsCliRunner(LocalOAuthLoginService login, ProjectorSoapHttp soap)
    {
        _login = login;
        _soap = soap;
    }

    public async Task<int> RunAsync(string method, string bodyFile, CancellationToken cancellationToken)
    {
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

        var text = (await File.ReadAllTextAsync(bodyFile, cancellationToken))
            .Replace("{{ticket}}", connection.SessionTicket, StringComparison.Ordinal);
        var wrapper = XElement.Parse(
            $"<x xmlns:pws=\"{SoapNamespaces.Pws}\" xmlns:req=\"{SoapNamespaces.Req}\" xmlns:com=\"{SoapNamespaces.Com}\" " +
            $"xmlns:tim=\"{SoapNamespaces.Tim}\" xmlns:sch=\"{SoapNamespaces.Sch}\">{text}</x>");
        var body = wrapper.Elements().Single();

        try
        {
            var response = await _soap.PostWcfAsync(connection, method, body, cancellationToken);
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
