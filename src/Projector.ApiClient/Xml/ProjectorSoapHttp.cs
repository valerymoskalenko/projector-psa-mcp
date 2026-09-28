using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Shared SOAP 1.1 HTTP POST helper (WCF PWS and ASMX).
/// </summary>
public sealed class ProjectorSoapHttp
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Wait before the one retry of a read that Projector refused as busy.</summary>
    internal static TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly ILogger<ProjectorSoapHttp> _logger;
    private readonly ProjectorCallLimiter? _limiter;
    private readonly bool _isWrite;

    /// <param name="limiter">Per-user cap on concurrent calls (see <see cref="ProjectorCallLimiter"/>); none when null.</param>
    /// <param name="isWrite">
    /// Write transport: a call Projector refused as busy is never retried (reads are retried once), and the error
    /// says nothing was saved.
    /// </param>
    public ProjectorSoapHttp(
        HttpClient http,
        ILogger<ProjectorSoapHttp> logger,
        ProjectorCallLimiter? limiter = null,
        bool isWrite = false)
    {
        _http = http;
        _logger = logger;
        _limiter = limiter;
        _isWrite = isWrite;
    }

    public static string GetWcfUrl(ProjectorConnection connection) =>
        $"{connection.SoapServiceAuthority.TrimEnd('/')}/OpsProjectorWcfSvc/PwsProjectorServices.svc";

    public static string GetAsmxUrl(string wcfUrl)
    {
        // https://secureN.projectorpsa.com/OpsProjectorWcfSvc/PwsProjectorServices.svc
        // -> https://secureN.projectorpsa.com/OpsProjectorWebSvc/OpsProjectorSvc.asmx
        var match = System.Text.RegularExpressions.Regex.Match(wcfUrl, @"^(https?://[^/]+)/");
        if (!match.Success)
        {
            throw new ProjectorApiException($"Cannot derive ASMX URL from WCF URL: {wcfUrl}", "InvalidUrl");
        }

        return $"{match.Groups[1].Value}/OpsProjectorWebSvc/OpsProjectorSvc.asmx";
    }

    public static string GetAsmxUrl(ProjectorConnection connection) =>
        GetAsmxUrl(GetWcfUrl(connection));

    public Task<XDocument> PostWcfAsync(
        ProjectorConnection connection,
        string method,
        XElement body,
        CancellationToken cancellationToken = default)
    {
        var url = GetWcfUrl(connection);
        var soapAction = SoapNamespaces.WcfSoapActionPrefix + method;
        var envelope = BuildWcfEnvelope(body);
        return SendAsync(url, soapAction, envelope, method, connection.SessionTicket.Length, connection.UserKey, cancellationToken);
    }

    public Task<XDocument> PostAsmxAsync(
        ProjectorConnection connection,
        string soapAction,
        XDocument envelope,
        string context,
        CancellationToken cancellationToken = default)
    {
        var url = GetAsmxUrl(connection);
        return SendAsync(url, soapAction, envelope, context, connection.SessionTicket.Length, connection.UserKey, cancellationToken);
    }

    /// <summary>One call without a user (no per-user limit).</summary>
    public Task<XDocument> PostAsync(
        string url,
        string soapAction,
        XDocument envelope,
        string context,
        int sessionTicketLen,
        CancellationToken cancellationToken = default) =>
        SendAsync(url, soapAction, envelope, context, sessionTicketLen, userKey: null, cancellationToken);

    private async Task<XDocument> SendAsync(
        string url,
        string soapAction,
        XDocument envelope,
        string context,
        int sessionTicketLen,
        string? userKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendLimitedAsync(url, soapAction, envelope, context, sessionTicketLen, userKey, cancellationToken);
        }
        catch (ProjectorApiException ex) when (ProjectorCallLimiter.IsBusy(ex))
        {
            if (_isWrite)
            {
                throw new ProjectorApiException(
                    "Nothing was saved: Projector refused the save because you have too many requests running at once " +
                    "(its limit is 4 per user, including your Projector browser session). Retry the save in a few seconds.",
                    ProjectorCallLimiter.BusyErrorCode,
                    ex);
            }

            _logger.LogWarning("PWS {Context}: Projector busy ({Message}); retrying once", context, ex.Message);
        }

        await Task.Delay(BusyRetryDelay, cancellationToken);
        try
        {
            return await SendLimitedAsync(url, soapAction, envelope, context, sessionTicketLen, userKey, cancellationToken);
        }
        catch (ProjectorApiException ex) when (ProjectorCallLimiter.IsBusy(ex))
        {
            throw new ProjectorApiException(
                "Projector is busy with your other requests (its limit is 4 at a time per user, including your Projector " +
                "browser session). Retry this call in a few seconds.",
                ProjectorCallLimiter.BusyErrorCode,
                ex);
        }
    }

    private async Task<XDocument> SendLimitedAsync(
        string url,
        string soapAction,
        XDocument envelope,
        string context,
        int sessionTicketLen,
        string? userKey,
        CancellationToken cancellationToken)
    {
        using var slot = _limiter is null || userKey is null
            ? null
            : await _limiter.EnterAsync(userKey, cancellationToken);
        return await SendOnceAsync(url, soapAction, envelope, context, sessionTicketLen, cancellationToken);
    }

    private async Task<XDocument> SendOnceAsync(
        string url,
        string soapAction,
        XDocument envelope,
        string context,
        int sessionTicketLen,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("SOAPAction", soapAction);

        var payload = envelope.ToString(SaveOptions.DisableFormatting);
        var content = new ByteArrayContent(Utf8NoBom.GetBytes(payload));
        content.Headers.TryAddWithoutValidation("Content-Type", "text/xml;charset=\"utf-8\"");
        request.Content = content;

        _logger.LogDebug(
            "PWS {Context} → {Url} sessionTicketLen={TicketLen}",
            context,
            url,
            sessionTicketLen);

        using var response = await _http.SendAsync(request, cancellationToken);
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ProjectorApiException("Projector rate limit exceeded.", "RateLimited");
        }

        if (!response.IsSuccessStatusCode)
        {
            if (TryParseSoapFault(xml, out var code, out var text))
            {
                _logger.LogWarning(
                    "PWS {Context} SOAP fault {Status}: {Code} — {Text}",
                    context,
                    response.StatusCode,
                    code,
                    text);
                throw new ProjectorApiException(text ?? $"Projector call {context} failed.", code);
            }

            _logger.LogWarning(
                "PWS {Context} failed with {Status}: {Body}",
                context,
                response.StatusCode,
                xml.Length > 500 ? xml[..500] + "…" : xml);
            throw new ProjectorApiException($"Projector call {context} failed: {response.StatusCode}.");
        }

        var doc = XDocument.Parse(xml);
        AssertNoSoapFault(doc, context);
        ThrowIfBusy(doc);
        return doc;
    }

    /// <summary>Projector can also refuse a call for too many active requests with a message in a normal result.</summary>
    private static void ThrowIfBusy(XDocument doc)
    {
        foreach (var message in XmlNodeHelpers.LocalNodes(doc, "PwsMessage"))
        {
            var code = XmlNodeHelpers.Value(message, "ErrorCode");
            var text = XmlNodeHelpers.Value(message, "ErrorText") ?? XmlNodeHelpers.Value(message, "MessageText");
            if (ProjectorCallLimiter.IsBusyText(code) || ProjectorCallLimiter.IsBusyText(text))
            {
                throw new ProjectorApiException(text ?? code ?? "Too many requests.", "TooManyRequests");
            }
        }
    }

    public static XDocument BuildWcfEnvelope(XElement body) =>
        new(
            new XElement(SoapNamespaces.SoapEnv + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", SoapNamespaces.SoapEnv),
                new XAttribute(XNamespace.Xmlns + "pws", SoapNamespaces.Pws),
                new XAttribute(XNamespace.Xmlns + "req", SoapNamespaces.Req),
                new XAttribute(XNamespace.Xmlns + "com", SoapNamespaces.Com),
                new XAttribute(XNamespace.Xmlns + "tim", SoapNamespaces.Tim),
                new XAttribute(XNamespace.Xmlns + "sch", SoapNamespaces.Sch),
                new XElement(SoapNamespaces.SoapEnv + "Header"),
                new XElement(SoapNamespaces.SoapEnv + "Body", body)));

    public static SoapFaultInfo? GetSoapFault(XDocument response)
    {
        var fault = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is null)
        {
            return null;
        }

        var message = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "PwsMessage");
        if (message is not null)
        {
            var code = XmlNodeHelpers.Value(message, "ErrorCode");
            var text = XmlNodeHelpers.Value(message, "ErrorText")
                ?? XmlNodeHelpers.Value(message, "AdditionalErrorText")
                ?? XmlNodeHelpers.Value(message, "MessageText")
                ?? code;
            return new SoapFaultInfo(code ?? "SoapFault", text ?? "Unknown SOAP fault.");
        }

        var faultString = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
        if (!string.IsNullOrWhiteSpace(faultString))
        {
            return new SoapFaultInfo("SoapFault", faultString);
        }

        return new SoapFaultInfo("SoapFault", "Unknown SOAP fault.");
    }

    public static void AssertNoSoapFault(XDocument response, string context = "Projector call")
    {
        var fault = GetSoapFault(response);
        if (fault is not null)
        {
            throw new ProjectorApiException($"{context} failed ({fault.Code}): {fault.Message}", fault.Code);
        }
    }

    public static bool TryParseSoapFault(string xml, out string? code, out string? text)
    {
        code = null;
        text = null;
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("Fault", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var doc = XDocument.Parse(xml);
            var fault = GetSoapFault(doc);
            if (fault is null)
            {
                return false;
            }

            code = fault.Code;
            text = fault.Message;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ThrowIfResultError(XElement? result)
    {
        if (result is null)
        {
            return;
        }

        foreach (var message in XmlNodeHelpers.LocalNodes(result, "PwsMessage"))
        {
            var code = XmlNodeHelpers.Value(message, "ErrorCode");
            var text = XmlNodeHelpers.Value(message, "ErrorText")
                ?? XmlNodeHelpers.Value(message, "MessageText")
                ?? code;
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            if (string.Equals(code, "AtLeastOneItemNotFound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "RowCountExceeded", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            throw new ProjectorApiException(text ?? code, code);
        }
    }

    public static bool IsRowCountExceeded(XDocument response) =>
        XmlNodeHelpers.LocalNodes(response, "PwsMessage")
            .Any(m => string.Equals(XmlNodeHelpers.Value(m, "ErrorCode"), "RowCountExceeded", StringComparison.OrdinalIgnoreCase));
}

public sealed record SoapFaultInfo(string Code, string Message);

public sealed record SoapRequestItem(string Key, string Url, string SoapAction, XDocument Envelope);
