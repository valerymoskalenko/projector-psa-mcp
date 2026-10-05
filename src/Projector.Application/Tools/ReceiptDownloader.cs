using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Projector.Application.Tools;

/// <summary>A receipt file downloaded from receipt.source_url.</summary>
public sealed record DownloadedReceipt(byte[] Content, string? FileName);

/// <summary>Downloads receipt.source_url for save_expenses.</summary>
public interface IReceiptDownloader
{
    /// <summary>The file, or an exception whose message is for the user (<see cref="ReceiptDownloadException"/>).</summary>
    Task<DownloadedReceipt> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken);
}

public sealed class ReceiptDownloadException(string message) : Exception(message);

/// <summary>
/// Downloads a receipt from a public https link. The server runs inside Azure, so the link must not reach anything
/// internal: https on port 443 only, no user name in the URL, and the address actually connected to (after DNS, on
/// every redirect) must be public. No cookies or credentials are sent. At most 3 redirects, 15 s and the receipt quota.
/// </summary>
public sealed class ReceiptDownloader : IReceiptDownloader, IDisposable
{
    public const int MaxRedirects = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly ILogger<ReceiptDownloader> _logger;

    public ReceiptDownloader(ILogger<ReceiptDownloader> logger)
    {
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectToPublicAddressAsync
        };
        _http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectorPsaMcp-ReceiptFetch/1.0");
    }

    public async Task<DownloadedReceipt> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        var current = CheckUrl(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (IsRedirect(response.StatusCode))
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new ReceiptDownloadException($"The receipt link redirects more than {MaxRedirects} times.");
                    }

                    var location = response.Headers.Location
                        ?? throw new ReceiptDownloadException("The receipt link redirects without a target.");
                    current = CheckUrl(new Uri(current, location).AbsoluteUri);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ReceiptDownloadException(
                        $"The receipt link answered {(int)response.StatusCode} {response.ReasonPhrase}; it must be a public link that downloads the file.");
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new ReceiptDownloadException(
                        $"The file behind the receipt link is {response.Content.Headers.ContentLength / 1024} KB; Projector accepts up to {maxBytes / 1024} KB.");
                }

                var content = await ReadLimitedAsync(response, maxBytes, timeout.Token);
                var name = ReceiptFiles.NameFromContentDisposition(response.Content.Headers.ContentDisposition?.ToString())
                    ?? ReceiptFiles.NameFromUrl(current);
                return new DownloadedReceipt(content, name);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReceiptDownloadException($"The receipt link did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (IOException ex)
        {
            // The connection broke while the body was read (HttpIOException and the like).
            _logger.LogWarning("Receipt download broke off: {Host} {ExceptionType}", current.Host, ex.GetType().Name);
            throw new ReceiptDownloadException($"The download from the receipt link broke off ({current.Host}); try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Receipt download failed: {Host} {ExceptionType}", current.Host, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            throw new ReceiptDownloadException(
                ex.InnerException is ReceiptDownloadException inner ? inner.Message : $"The receipt link could not be downloaded ({current.Host}).");
        }
    }

    /// <summary>The rules a receipt link must meet before anything is sent; returns the parsed URL.</summary>
    public static Uri CheckUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ReceiptDownloadException("receipt.source_url is not a valid URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ReceiptDownloadException("receipt.source_url must be an https link.");
        }

        if (!uri.IsDefaultPort)
        {
            throw new ReceiptDownloadException("receipt.source_url must use the standard https port.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ReceiptDownloadException("receipt.source_url must not contain a user name or password.");
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal) && !IsPublic(literal))
        {
            throw new ReceiptDownloadException("receipt.source_url points to a private or local address.");
        }

        if (uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReceiptDownloadException("receipt.source_url points to a private or local address.");
        }

        return uri;
    }

    /// <summary>true for an address on the public internet; false for private, loopback, link-local and the like.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // this network
                || b[0] == 10                                    // private
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)    // carrier-grade NAT
                || b[0] == 127                                   // loopback
                || (b[0] == 169 && b[1] == 254)                  // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)     // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // IETF protocol assignments
                || (b[0] == 192 && b[1] == 168)                  // private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))   // benchmarking
                || b[0] >= 224);                                 // multicast, reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC                         // unique local fc00::/7
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) // NAT64 64:ff9b::/96
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)); // documentation
        }

        return false;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0)
        {
            throw new ReceiptDownloadException($"The receipt link's host {host} was not found.");
        }

        // Every address must be public: DNS answers can mix them to slip a private one in.
        if (addresses.Any(a => !IsPublic(a)))
        {
            throw new ReceiptDownloadException("The receipt link points to a private or local address.");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException($"Could not connect to {host}.", last);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new ReceiptDownloadException($"The file behind the receipt link is larger than {maxBytes / 1024} KB, Projector's limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose() => _http.Dispose();
}
