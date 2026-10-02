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
/// <see cref="RunAsmxAsync"/> does the same for a short list of legacy (ASMX) report and export methods.
/// </summary>
public sealed class PwsCliRunner
{
    private static readonly string[] ReadPrefixes = ["PwsGet", "PwsSearch"];

    /// <summary>Legacy (ASMX) methods that only read.</summary>
    private static readonly string[] AsmxReadMethods =
    [
        "GetReportStatus", "ExportProjectList", "ExportTimeCards", "ExportOlapGinsuRecords", "ExportResources",
        "ExportScheduledTimeoff"
    ];

    /// <summary>
    /// Legacy methods that start a report run or an export batch: no business data changes, but each call leaves
    /// a run in Projector, so they are sent only with <c>--run</c>.
    /// </summary>
    private static readonly string[] AsmxRunMethods = ["SubmitReportSpec", "SubmitOlapGinsuExport"];

    private readonly LocalOAuthLoginService _login;
    private readonly ProjectorSoapHttp _soap;
    private readonly ProjectorSoapWriteHttp _submit;
    private readonly IHostEnvironment _environment;

    public PwsCliRunner(
        LocalOAuthLoginService login, ProjectorSoapHttp soap, ProjectorSoapWriteHttp submit, IHostEnvironment environment)
    {
        _login = login;
        _soap = soap;
        _submit = submit;
        _environment = environment;
    }

    /// <summary>Null when the legacy method may be sent; else why not.</summary>
    internal static string? AsmxRefusal(string? method, bool allowRun)
    {
        var name = method?.Trim() ?? string.Empty;
        if (AsmxReadMethods.Contains(name, StringComparer.Ordinal))
        {
            return null;
        }

        if (AsmxRunMethods.Contains(name, StringComparer.Ordinal))
        {
            return allowRun
                ? null
                : $"'{name}' starts a run in Projector; add --run to send it. Nothing was sent.";
        }

        return $"asmx sends only {string.Join(", ", AsmxReadMethods)} and, with --run, {string.Join(", ", AsmxRunMethods)}; " +
            $"'{name}' was refused and nothing was sent.";
    }

    /// <summary>
    /// Dev only: posts a legacy (ASMX) request with the local OAuth session and prints the response XML. The file
    /// holds the parameter elements, e.g. <c>&lt;data:MaxRowsToReturn&gt;10&lt;/data:MaxRowsToReturn&gt;</c>; an
    /// empty file sends no parameters.
    /// </summary>
    public async Task<int> RunAsmxAsync(string method, string paramsFile, bool allowRun, CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            Console.Error.WriteLine(
                $"asmx is a development tool and runs only with ASPNETCORE_ENVIRONMENT=Development (now: {_environment.EnvironmentName}).");
            return 2;
        }

        if (AsmxRefusal(method, allowRun) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 2;
        }

        var text = await File.ReadAllTextAsync(paramsFile, cancellationToken);
        var parameters = XElement.Parse($"<x xmlns:data=\"{SoapNamespaces.Data}\">{text}</x>").Elements().ToArray();

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

        var name = method.Trim();
        var envelope = ProjectorReportEnvelopes.Asmx(connection.SessionTicket, name, parameters);
        // A run is sent once, never retried.
        var transport = AsmxRunMethods.Contains(name, StringComparer.Ordinal) ? _submit.Soap : _soap;
        try
        {
            var response = await transport.PostAsmxAsync(connection, SoapNamespaces.AsmxAction(name), envelope, name, cancellationToken);
            Console.WriteLine(response.ToString());
            return 0;
        }
        catch (ProjectorApiException ex)
        {
            Console.WriteLine($"ProjectorApiException code={ex.ErrorCode}: {ex.Message}");
            return 1;
        }
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
