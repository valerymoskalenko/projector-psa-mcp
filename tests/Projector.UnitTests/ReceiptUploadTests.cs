using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Projector.ApiClient;
using Projector.Application.Tools;
using Projector.Mcp.Server.Auth;
using Projector.Mcp.Server.Hosting;

namespace Projector.UnitTests;

/// <summary>
/// Receipt files from AI clients (v0.10.0): type detection, the rules for source_url links, upload tickets and the
/// POST /receipts/upload endpoint. The service side (save_expenses, the upload to the pool) is in ExpenseTests.
/// </summary>
public class ReceiptUploadTests : IClassFixture<ProjectorWebApplicationFactory>
{
    private static readonly byte[] Pdf = "%PDF-1.7\n1 0 obj\n"u8.ToArray();
    private static readonly string TestKey = new('t', 48);
    private readonly ProjectorWebApplicationFactory _factory;

    public ReceiptUploadTests(ProjectorWebApplicationFactory factory) => _factory = factory;

    // ---------------------------------------------------------------- file type

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, ".pdf")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ".gif")]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E }, null)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, null)]
    public void DetectType_ReadsTheFirstBytes(byte[] content, string? expected) =>
        ReceiptFiles.DetectType(content).Should().Be(expected);

    [Fact]
    public void DetectType_FindsAPdfHeaderAfterLeadingBytes() =>
        ReceiptFiles.DetectType([.. new byte[100], .. Pdf]).Should().Be(".pdf");

    [Theory]
    [InlineData(null, "receipt.pdf")]
    [InlineData("Sep12 Uber", "Sep12 Uber.pdf")]
    [InlineData("Sep08 - Hotel 55,67CAD.pdf", "Sep08 - Hotel 55,67CAD.pdf")]
    [InlineData("C:\\temp\\inv.PDF", "inv.PDF")]
    public void Check_NamesTheFile(string? name, string expected) =>
        ReceiptFiles.Check(name, Pdf, 1024, null).Name.Should().Be(expected);

    [Fact]
    public void Check_AcceptsTheSha256AsHexOrBase64()
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Pdf);
        ReceiptFiles.Check("a.pdf", Pdf, 1024, Convert.ToHexString(hash)).Error.Should().BeNull();
        ReceiptFiles.Check("a.pdf", Pdf, 1024, Convert.ToBase64String(hash)).Error.Should().BeNull();
        ReceiptFiles.Check("a.pdf", Pdf, 1024, "abc").Error.Should().Contain("SHA-256");
    }

    [Fact]
    public void Check_RefusesEmptyAndTooLarge()
    {
        ReceiptFiles.Check("a.pdf", [], 1024, null).Error.Should().Contain("empty");
        ReceiptFiles.Check("a.pdf", Pdf, 4, null).Error.Should().Contain("Projector accepts up to");
    }

    [Theory]
    [InlineData("attachment; filename=\"Uber Sep12.pdf\"", "Uber Sep12.pdf")]
    [InlineData("attachment; filename*=UTF-8''Re%C3%A7u.pdf", "Reçu.pdf")]
    [InlineData("inline", null)]
    public void NameFromContentDisposition_ReadsTheFileName(string header, string? expected) =>
        ReceiptFiles.NameFromContentDisposition(header).Should().Be(expected);

    [Theory]
    [InlineData("https://example.com/a/Uber%20Sep12.pdf?x=1", "Uber Sep12.pdf")]
    [InlineData("https://example.com/download?id=1", null)]
    public void NameFromUrl_UsesOnlyAReceiptFileName(string url, string? expected) =>
        ReceiptFiles.NameFromUrl(new Uri(url)).Should().Be(expected);

    // ---------------------------------------------------------------- source_url rules

    [Theory]
    [InlineData("http://example.com/a.pdf", "https")]
    [InlineData("https://example.com:8443/a.pdf", "standard https port")]
    [InlineData("https://user:pw@example.com/a.pdf", "user name")]
    [InlineData("https://127.0.0.1/a.pdf", "private or local")]
    [InlineData("https://169.254.169.254/metadata", "private or local")]
    [InlineData("https://10.1.2.3/a.pdf", "private or local")]
    [InlineData("https://[::1]/a.pdf", "private or local")]
    [InlineData("https://localhost/a.pdf", "private or local")]
    [InlineData("https://metadata.internal/a.pdf", "private or local")]
    [InlineData("file:///etc/passwd", "https")]
    [InlineData("not a url", "not a valid URL")]
    public void CheckUrl_RefusesLinksThatCouldReachInside(string url, string message)
    {
        var act = () => ReceiptDownloader.CheckUrl(url);
        act.Should().Throw<ReceiptDownloadException>().Which.Message.Should().Contain(message);
    }

    [Fact]
    public void CheckUrl_AcceptsAPublicHttpsLink() =>
        ReceiptDownloader.CheckUrl("https://files.example.com/s/abc?download=1").Host.Should().Be("files.example.com");

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("20.42.1.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void IsPublic_SortsAddresses(string address, bool expected) =>
        ReceiptDownloader.IsPublic(IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    [Trait("Category", "Live")]
    public async Task Download_APublicPdf_ThroughTheAddressCheck()
    {
        using var downloader = new ReceiptDownloader(Microsoft.Extensions.Logging.Abstractions.NullLogger<ReceiptDownloader>.Instance);

        var file = await downloader.DownloadAsync(
            "https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf", 2 * 1024 * 1024, CancellationToken.None);

        ReceiptFiles.DetectType(file.Content).Should().Be(".pdf");
        file.FileName.Should().Be("dummy.pdf");
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Download_AHostThatResolvesToLoopback_IsRefusedAtConnect()
    {
        using var downloader = new ReceiptDownloader(Microsoft.Extensions.Logging.Abstractions.NullLogger<ReceiptDownloader>.Instance);

        // localtest.me is public DNS that answers 127.0.0.1: only the connect-time check can catch it.
        var act = () => downloader.DownloadAsync("https://localtest.me/a.pdf", 1024, CancellationToken.None);

        (await act.Should().ThrowAsync<ReceiptDownloadException>()).Which.Message.Should().Contain("private or local");
    }

    // ---------------------------------------------------------------- tickets

    [Fact]
    public void Ticket_RoundTrips_ToItsConnection()
    {
        var tickets = Tickets(out _);
        var offer = tickets.Issue("conn-1");

        offer.Url.Should().Be("https://mcp.example/receipts/upload");
        offer.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(1));
        tickets.Redeem(offer.Ticket).Should().Be(("conn-1", (string?)null));
    }

    [Fact]
    public void Ticket_Tampered_IsRefused()
    {
        var tickets = Tickets(out _);
        var ticket = tickets.Issue("conn-1").Ticket;
        var other = tickets.Issue("conn-2").Ticket;

        // conn-2's payload with conn-1's signature.
        var forged = other[..other.LastIndexOf('.')] + ticket[ticket.LastIndexOf('.')..];
        tickets.Redeem(forged).ConnectionId.Should().BeNull();
        tickets.Redeem(ticket + "x").ConnectionId.Should().BeNull();
        tickets.Redeem("rut1.abc").ConnectionId.Should().BeNull();
        tickets.Redeem(null).Error.Should().Contain("list_expenses");
    }

    [Fact]
    public void Ticket_FromAnotherKey_IsRefused()
    {
        var ticket = Tickets(out _).Issue("conn-1").Ticket;
        Tickets(out _, key: new string('u', 48)).Redeem(ticket).ConnectionId.Should().BeNull();
    }

    [Fact]
    public void Ticket_Expires_After30Minutes()
    {
        var tickets = Tickets(out var time);
        var ticket = tickets.Issue("conn-1").Ticket;

        time.Now = time.Now.AddMinutes(31);
        tickets.Redeem(ticket).Error.Should().Contain("expired");
    }

    [Fact]
    public void Ticket_Allows50Uploads()
    {
        var tickets = Tickets(out _);
        var ticket = tickets.Issue("conn-1").Ticket;

        for (var i = 0; i < ReceiptUploadTickets.MaxUploadsPerTicket; i++)
        {
            tickets.Redeem(ticket).ConnectionId.Should().Be("conn-1");
        }

        tickets.Redeem(ticket).Error.Should().Contain("50 uploads");
    }

    [Fact]
    public void Ticket_AndAccessToken_AreNotInterchangeable()
    {
        var options = Options.Create(new ProjectorOptions
        {
            JwtSigningKey = TestKey,
            PublicBaseUrl = "https://mcp.example",
            McpAudience = "https://mcp.example/mcp"
        });
        var issuer = new McpJwtIssuer(options);
        var tickets = new ReceiptUploadTickets(options, new MemoryCache(new MemoryCacheOptions()));

        tickets.Redeem(issuer.CreateAccessToken("conn-1")).ConnectionId.Should().BeNull();
        var act = () => new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ValidateToken(tickets.Issue("conn-1").Ticket, issuer.CreateValidationParameters(), out _);
        act.Should().Throw<Exception>();
    }

    // ---------------------------------------------------------------- endpoint

    [Fact]
    public async Task Endpoint_WithoutTicket_Returns401()
    {
        var response = await _factory.CreateClient().PostAsync("/receipts/upload", Form(null, Pdf, "a.pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Error(response)).Should().Be("invalid_ticket");
    }

    [Fact]
    public async Task Endpoint_NotAForm_Returns400()
    {
        var response = await _factory.CreateClient().PostAsync("/receipts/upload", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Error(response)).Should().Be("invalid_request");
    }

    [Fact]
    public async Task Endpoint_TicketWithoutAFile_Returns400()
    {
        var ticket = _factory.Services.GetRequiredService<ReceiptUploadTickets>().Issue("no-such-connection").Ticket;
        using var form = new MultipartFormDataContent { { new StringContent(ticket), "ticket" } };

        var response = await _factory.CreateClient().PostAsync("/receipts/upload", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Endpoint_TicketForAnUnknownConnection_AsksToReconnect()
    {
        var ticket = _factory.Services.GetRequiredService<ReceiptUploadTickets>().Issue("no-such-connection").Ticket;

        var response = await _factory.CreateClient().PostAsync("/receipts/upload", Form(ticket, Pdf, "a.pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Error(response)).Should().Be("reconnect");
    }

    [Fact]
    public async Task Endpoint_TooLarge_Returns413()
    {
        var ticket = _factory.Services.GetRequiredService<ReceiptUploadTickets>().Issue("no-such-connection").Ticket;

        var response = await _factory.CreateClient().PostAsync("/receipts/upload",
            Form(ticket, new byte[ReceiptUploadEndpoint.MaxRequestBytes + 1], "big.pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    // ---------------------------------------------------------------- helpers

    private static MultipartFormDataContent Form(string? ticket, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent();
        if (ticket is not null)
        {
            form.Add(new StringContent(ticket), "ticket");
        }

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        return form;
    }

    private static async Task<string?> Error(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static ReceiptUploadTickets Tickets(out ManualTime time, string? key = null)
    {
        time = new ManualTime();
        return new ReceiptUploadTickets(
            Options.Create(new ProjectorOptions { JwtSigningKey = key ?? TestKey, PublicBaseUrl = "https://mcp.example/" }),
            new MemoryCache(new MemoryCacheOptions()),
            time);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
