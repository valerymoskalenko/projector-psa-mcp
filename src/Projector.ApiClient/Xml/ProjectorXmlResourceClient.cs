using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Projector.Domain.Auth;
using Projector.Domain.Resources;

namespace Projector.ApiClient.Xml;

/// <summary>
/// HTTP POST of SOAP 1.1 envelopes (no WCF client).
/// </summary>
public sealed class ProjectorXmlResourceClient : IProjectorResourceClient
{
    private readonly ProjectorSoapHttp _soap;
    private readonly ILogger<ProjectorXmlResourceClient> _logger;

    public ProjectorXmlResourceClient(ProjectorSoapHttp soap, ILogger<ProjectorXmlResourceClient> logger)
    {
        _soap = soap;
        _logger = logger;
    }

    public async Task<ResourceListResult> ListResourcesAsync(
        ProjectorConnection connection,
        string? query,
        bool includeInactive,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResourceList",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", connection.SessionTicket),
                new XElement(SoapNamespaces.Req + "IncludeInactiveFlag", includeInactive ? "true" : "false"),
                new XElement(SoapNamespaces.Req + "MaxRowsToReturn", maxRows),
                string.IsNullOrWhiteSpace(query) ? null : new XElement(SoapNamespaces.Req + "QueryString", query)));

        var doc = await _soap.PostWcfAsync(connection, "PwsGetResourceList", body, cancellationToken);
        var result = doc.Descendants(SoapNamespaces.Pws + "PwsGetResourceListResult").FirstOrDefault()
            ?? XmlNodeHelpers.LocalNode(doc, "PwsGetResourceListResult");

        ProjectorSoapHttp.ThrowIfResultError(result);
        return new ResourceListResult
        {
            Resources = ProjectorResponseParsers.ParseResourceList(doc),
            ServerTruncated = ProjectorSoapHttp.IsRowCountExceeded(doc)
        };
    }

    public async Task<ResourceDetail?> GetResourceAsync(
        ProjectorConnection connection,
        string id,
        bool includeHistory,
        bool includeUdfs,
        CancellationToken cancellationToken = default)
    {
        var identity = ProjectorIdentityRefs.BuildResourceRef(id);
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResource",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", connection.SessionTicket),
                new XElement(SoapNamespaces.Req + "ResourceIdentities", identity)));

        var doc = await _soap.PostWcfAsync(connection, "PwsGetResource", body, cancellationToken);
        var result = XmlNodeHelpers.LocalNode(doc, "PwsGetResourceResult");
        ProjectorSoapHttp.ThrowIfResultError(result);

        _logger.LogDebug("PwsGetResource parsed for id length {IdLen}", id.Length);
        return ProjectorResponseParsers.ParseResource(doc, includeHistory, includeUdfs);
    }
}
