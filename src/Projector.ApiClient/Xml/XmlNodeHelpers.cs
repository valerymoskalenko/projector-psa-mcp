using System.Xml.Linq;

namespace Projector.ApiClient.Xml;

internal static class XmlNodeHelpers
{
    public static IEnumerable<XElement> LocalNodes(XContainer? parent, string localName)
    {
        if (parent is null)
        {
            return [];
        }

        return parent.Descendants().Where(e => e.Name.LocalName == localName);
    }

    public static IEnumerable<XElement> ChildLocalNodes(XElement? parent, string localName)
    {
        if (parent is null)
        {
            return [];
        }

        return parent.Elements().Where(e => e.Name.LocalName == localName);
    }

    public static XElement? LocalNode(XContainer? parent, string localName) =>
        LocalNodes(parent, localName).FirstOrDefault();

    public static string? Value(XElement? parent, string localName)
    {
        var node = LocalNode(parent, localName);
        if (node is null)
        {
            return null;
        }

        var value = node.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool Bool(XElement? parent, string localName)
    {
        var raw = Value(parent, localName);
        return bool.TryParse(raw, out var parsed) && parsed;
    }

    public static string? NestedValue(XElement? parent, params string[] localNames)
    {
        if (parent is null)
        {
            return null;
        }

        XElement? current = parent;
        foreach (var name in localNames)
        {
            current = LocalNode(current, name);
            if (current is null)
            {
                return null;
            }
        }

        var value = current.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static int Int(XElement? parent, string localName)
    {
        var raw = Value(parent, localName);
        return int.TryParse(raw, out var n) ? n : 0;
    }

    /// <summary>Parses an int element; returns null when missing or blank (never coerce to 0).</summary>
    public static int? NullableInt(XElement? parent, string localName)
    {
        var raw = Value(parent, localName);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>Parses a double element; returns null when missing or blank (never coerce to 0).</summary>
    public static double? NullableDouble(XElement? parent, string localName)
    {
        var raw = Value(parent, localName);
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    public static string? ShortDate(string? dateRaw)
    {
        if (string.IsNullOrWhiteSpace(dateRaw))
        {
            return null;
        }

        return dateRaw.Length >= 10 ? dateRaw[..10] : dateRaw;
    }
}
