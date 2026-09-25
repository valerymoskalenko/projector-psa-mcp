using System.Text.RegularExpressions;

namespace Projector.Domain.Common;

/// <summary>
/// Redacts session tickets, refresh tokens, and secrets from user-facing / logged strings.
/// </summary>
public static class SecretRedactor
{
    private static readonly Regex SecretPatterns = new(
        @"(?i)(session[_-]?ticket|refresh[_-]?token|access[_-]?token|client[_-]?secret|password|bearer)\s*[=:]\s*[^\s,;""']+",
        RegexOptions.Compiled);

    public static string Redact(string? message, int maxLength = 400)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? "";
        }

        var redacted = SecretPatterns.Replace(message, "$1=[redacted]");
        if (redacted.Length > maxLength)
        {
            redacted = redacted[..maxLength] + "…";
        }

        return redacted;
    }
}
