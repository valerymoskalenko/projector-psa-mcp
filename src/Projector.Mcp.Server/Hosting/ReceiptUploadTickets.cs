using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Projector.ApiClient;
using Projector.Application.Tools;

namespace Projector.Mcp.Server.Hosting;

/// <summary>
/// Receipt upload tickets: list_expenses hands one out, POST /receipts/upload takes it instead of the MCP token
/// (an AI client's shell has no access to that token). A ticket names one connection, lasts 30 minutes and allows
/// 50 uploads. It is signed with a key derived from the JWT signing key and has its own format, so it is never
/// accepted as an MCP access token, and an access token is never accepted as a ticket.
/// </summary>
public sealed class ReceiptUploadTickets : IReceiptUploadTickets
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    public const int MaxUploadsPerTicket = 50;
    public const string Path = "/receipts/upload";
    private const string Prefix = "rut1.";

    private readonly byte[] _key;
    private readonly string _url;
    private readonly IMemoryCache _uses;
    private readonly TimeProvider _time;

    public ReceiptUploadTickets(IOptions<ProjectorOptions> options, IMemoryCache uses, TimeProvider? time = null)
    {
        var signingKey = options.Value.JwtSigningKey;
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("Projector:JwtSigningKey is missing; receipt upload tickets need it.");
        }

        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), "projector-mcp receipt upload ticket v1"u8);
        _url = options.Value.PublicBaseUrl.TrimEnd('/') + Path;
        _uses = uses;
        _time = time ?? TimeProvider.System;
    }

    public ReceiptUploadOffer Issue(string connectionId)
    {
        var expires = _time.GetUtcNow().Add(Lifetime);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new TicketPayload
        {
            C = connectionId,
            E = expires.ToUnixTimeSeconds(),
            N = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))
        });
        var body = Base64Url.EncodeToString(payload);
        var signature = Base64Url.EncodeToString(Sign(body));
        return new ReceiptUploadOffer(_url, Prefix + body + "." + signature, DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds()));
    }

    /// <summary>The connection id of a valid ticket, or the reason it is refused.</summary>
    public (string? ConnectionId, string? Error) Redeem(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || !ticket.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return (null, "ticket is missing or not an upload ticket: get one from list_expenses with include_options = true.");
        }

        var parts = ticket[Prefix.Length..].Split('.');
        byte[] signature;
        byte[] payloadBytes;
        try
        {
            if (parts.Length != 2)
            {
                throw new FormatException();
            }

            signature = Base64Url.DecodeFromChars(parts[1]);
            payloadBytes = Base64Url.DecodeFromChars(parts[0]);
        }
        catch (FormatException)
        {
            return (null, "ticket is not valid.");
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))
        {
            return (null, "ticket is not valid.");
        }

        TicketPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TicketPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return (null, "ticket is not valid.");
        }

        if (payload?.C is null || payload.N is null)
        {
            return (null, "ticket is not valid.");
        }

        var now = _time.GetUtcNow();
        if (now.ToUnixTimeSeconds() > payload.E)
        {
            return (null, "ticket has expired: get a new one from list_expenses with include_options = true.");
        }

        var key = "receipt-ticket:" + payload.N;
        var used = _uses.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpiration = DateTimeOffset.FromUnixTimeSeconds(payload.E).AddMinutes(1);
            return new StrongBox();
        })!;
        if (Interlocked.Increment(ref used.Count) > MaxUploadsPerTicket)
        {
            return (null, $"ticket has been used for {MaxUploadsPerTicket} uploads: get a new one from list_expenses with include_options = true.");
        }

        return (payload.C, null);
    }

    private byte[] Sign(string body) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(body));

    private sealed class TicketPayload
    {
        public string? C { get; set; }
        public long E { get; set; }
        public string? N { get; set; }
    }

    private sealed class StrongBox
    {
        public int Count;
    }
}
