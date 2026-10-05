using System.Globalization;
using System.Security.Cryptography;

namespace Projector.Application.Tools;

/// <summary>
/// Checks for a receipt file, whichever way it arrives (base64 in save_expenses, a download from source_url, or the
/// receipt upload endpoint): the type comes from the file's first bytes, not from its name, so a web page or a
/// corrupted transfer is refused before it reaches Projector.
/// </summary>
public static class ReceiptFiles
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The type found in the file's first bytes: ".pdf", ".png", ".jpg" or ".gif"; null for anything else.</summary>
    public static string? DetectType(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(PngSignature))
        {
            return ".png";
        }

        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return ".jpg";
        }

        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return ".gif";
        }

        // PDF readers accept the header anywhere in the first 1024 bytes.
        return content[..Math.Min(content.Length, 1024)].IndexOf("%PDF-"u8) >= 0 ? ".pdf" : null;
    }

    /// <summary>
    /// Checks a receipt file and returns the name to store it under, or an error for the user. A name without an
    /// extension gets the detected one (download links often have none).
    /// </summary>
    public static (string? Name, string? Error) Check(string? fileName, byte[] content, long maxBytes, string? sha256)
    {
        if (content.Length == 0)
        {
            return (null, "The receipt file is empty.");
        }

        if (content.Length > maxBytes)
        {
            return (null, $"The receipt is {Mb(content.Length)} MB; Projector accepts up to {Mb(maxBytes)} MB.");
        }

        if (!string.IsNullOrWhiteSpace(sha256) && !Sha256Matches(content, sha256))
        {
            return (null, $"The receipt's SHA-256 is {Sha256(content)}, not {sha256.Trim().ToLowerInvariant()}: the file changed on the way. Nothing was uploaded; send it again.");
        }

        var detected = DetectType(content);
        if (detected is null)
        {
            return (null, "The receipt is not a PDF, PNG, JPEG or GIF file (checked by its content).");
        }

        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
        {
            name = (string.IsNullOrEmpty(name) ? "receipt" : name) + detected;
        }
        else if (!ExpenseToolService.ReceiptExtensions.Contains(extension))
        {
            return (null, $"receipt.file_name must end in {string.Join(", ", ExpenseToolService.ReceiptExtensions.Order(StringComparer.Ordinal))}.");
        }
        else if ((detected == ".pdf") != extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"The receipt's name ends in {extension} but the file is a {detected.TrimStart('.').ToUpperInvariant()}.");
        }

        return (name, null);
    }

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static bool Sha256Matches(byte[] content, string expected)
    {
        var text = expected.Trim();
        var actual = SHA256.HashData(content);
        if (text.Length == 64)
        {
            try
            {
                return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(text));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Also accept the base64 form some tools print.
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A file name from a Content-Disposition header (filename* first), or null.</summary>
    public static string? NameFromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)
            || !System.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(header, out var value))
        {
            return null;
        }

        var name = value.FileNameStar ?? value.FileName?.Trim('"');
        return string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name);
    }

    /// <summary>The URL's last path segment when it already names a receipt file, else null.</summary>
    public static string? NameFromUrl(Uri url)
    {
        var segment = Uri.UnescapeDataString(url.Segments.LastOrDefault() ?? string.Empty).Trim('/');
        return ExpenseToolService.ReceiptExtensions.Contains(Path.GetExtension(segment)) ? Path.GetFileName(segment) : null;
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture);
}
