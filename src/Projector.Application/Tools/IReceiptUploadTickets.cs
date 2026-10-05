namespace Projector.Application.Tools;

/// <summary>Where and with which ticket a client uploads receipt files (list_expenses options.receipt_upload).</summary>
public sealed record ReceiptUploadOffer(string Url, string Ticket, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues receipt upload tickets: the MCP server's counterpart of Projector's sessionTicket + DocumentServerUrl for
/// AjxAddDocument. Registered only by the hosted HTTP server (the CLI has no upload endpoint).
/// </summary>
public interface IReceiptUploadTickets
{
    ReceiptUploadOffer Issue(string connectionId);
}
