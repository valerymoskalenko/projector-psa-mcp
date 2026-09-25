using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Projector.ApiClient;

namespace Projector.Application.Auth;

/// <summary>AES-GCM encryption for Projector tokens at rest.</summary>
public sealed class TokenEncryptionService
{
    private readonly byte[] _key;
    public string KeyVersion { get; } = "1";

    public TokenEncryptionService(IOptions<ProjectorOptions> options)
    {
        var raw = options.Value.TokenEncryptionKey;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Local/dev: derive a deterministic key from JwtSigningKey so SQL path can still work in tests.
            _key = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.JwtSigningKey + ":token-enc"));
        }
        else
        {
            try
            {
                _key = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                _key = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            }

            if (_key.Length is not (16 or 24 or 32))
            {
                _key = SHA256.HashData(_key);
            }
        }
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string ciphertext)
    {
        var payload = Convert.FromBase64String(ciphertext);
        if (payload.Length < 28)
        {
            throw new CryptographicException("Ciphertext too short.");
        }

        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var cipher = payload.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
