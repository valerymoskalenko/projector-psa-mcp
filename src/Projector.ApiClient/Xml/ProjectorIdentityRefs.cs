using System.Xml.Linq;
using Projector.Domain.Exceptions;

namespace Projector.ApiClient.Xml;

public static class ProjectorIdentityRefs
{
    /// <summary>
    /// Builds a PwsResourceRef. Rejects email — resolve via list search first.
    /// </summary>
    public static XElement BuildResourceRef(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var refEl = new XElement(SoapNamespaces.Com + "PwsResourceRef");

        if (long.TryParse(id, out _) && id.Length >= 15)
        {
            refEl.Add(new XElement(SoapNamespaces.Com + "ResourceUid", id));
            return refEl;
        }

        if (id.Contains('@', StringComparison.Ordinal))
        {
            throw new ProjectorApiException(
                "Email cannot be used as a resource identity. Use get_resource with email first.",
                "InvalidResourceId");
        }

        if (id.Length <= 64 && !id.Contains(' ', StringComparison.Ordinal))
        {
            refEl.Add(new XElement(SoapNamespaces.Com + "ResourceReferenceSystemId", id));
            return refEl;
        }

        refEl.Add(new XElement(SoapNamespaces.Com + "ResourceDisplayName", id));
        return refEl;
    }

    public static XElement BuildUserRef(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var refEl = new XElement(SoapNamespaces.Com + "PwsUserRef");

        if (long.TryParse(id, out _) && id.Length >= 15)
        {
            refEl.Add(new XElement(SoapNamespaces.Com + "UserUid", id));
            return refEl;
        }

        if (id.Contains('@', StringComparison.Ordinal))
        {
            refEl.Add(new XElement(SoapNamespaces.Com + "EmailAddress", id));
            return refEl;
        }

        if (id.Length <= 64 && !id.Contains(' ', StringComparison.Ordinal))
        {
            refEl.Add(new XElement(SoapNamespaces.Com + "UserReferenceSystemId", id));
            return refEl;
        }

        refEl.Add(new XElement(SoapNamespaces.Com + "UserDisplayName", id));
        return refEl;
    }

    public static XElement BuildEngagementRef(string code) =>
        new(SoapNamespaces.Com + "PwsEngagementRef",
            new XElement(SoapNamespaces.Com + "EngagementCode", code));

    public static XElement BuildProjectRef(string code) =>
        new(SoapNamespaces.Com + "PwsProjectRef",
            new XElement(SoapNamespaces.Com + "ProjectCode", code));

    /// <summary>String form matching PowerShell New-ProjectorResourceRefXml for tests.</summary>
    public static string ResourceRefXml(string id)
    {
        var el = BuildResourceRef(id);
        return ToPrefixedComXml(el);
    }

    public static string UserRefXml(string id)
    {
        var el = BuildUserRef(id);
        return ToPrefixedComXml(el);
    }

    private static string ToPrefixedComXml(XElement element)
    {
        // Standalone XElement.ToString() collapses to a default xmlns; PowerShell emits com: prefixes.
        var clone = new XElement(element);
        clone.Add(new XAttribute(XNamespace.Xmlns + "com", SoapNamespaces.Com.NamespaceName));
        return clone.ToString(SaveOptions.DisableFormatting);
    }
}
