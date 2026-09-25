using System.Security.Cryptography;
using System.Text;

namespace Projector.Domain.Auth;

/// <summary>
/// PKCE helpers aligned with the Projector OAuth developer guide (S256).
/// </summary>
public static class Pkce
{
    private const string ValidChars =
        "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ-._~";

    public static string GenerateCodeVerifier()
    {
        var length = RandomNumberGenerator.GetInt32(43, 129);
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = ValidChars[RandomNumberGenerator.GetInt32(0, ValidChars.Length)];
        }

        return new string(chars);
    }

    public static string GetS256CodeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static bool VerifyS256(string codeChallenge, string codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(codeChallenge) || string.IsNullOrWhiteSpace(codeVerifier))
        {
            return false;
        }

        var expected = GetS256CodeChallenge(codeVerifier.Trim());
        var actual = codeChallenge.Trim();
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        if (expectedBytes.Length != actualBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var incoming = value.Replace('_', '/').Replace('-', '+');
        incoming += (value.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Convert.FromBase64String(incoming);
    }
}
