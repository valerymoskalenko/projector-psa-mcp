using Microsoft.AspNetCore.Http.Features;
using Projector.Application.Tools;
using Projector.Domain.Exceptions;

namespace Projector.Mcp.Server.Hosting;

/// <summary>
/// POST /receipts/upload: the MCP server's counterpart of Projector's AjxAddDocument. A multipart form with
/// <c>ticket</c> (from list_expenses options.receipt_upload), <c>file</c> and an optional <c>sha256</c> stores the file in
/// the ticket owner's receipt pool and answers with its receipt_uid, which save_expenses then links to a card. The
/// file goes as binary, so an AI client sends it with curl and never has to write it out as base64.
/// </summary>
public static class ReceiptUploadEndpoint
{
    /// <summary>Projector's usual receipt quota (2 MB) plus room for the multipart framing.</summary>
    public const long MaxRequestBytes = 3 * 1024 * 1024;

    public static void MapReceiptUpload(this WebApplication app)
    {
        app.MapPost(ReceiptUploadTickets.Path, async (
            HttpContext http,
            ReceiptUploadTickets tickets,
            ExpenseToolService expenses,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(typeof(ReceiptUploadEndpoint));
            var ct = http.RequestAborted;
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = MaxRequestBytes;
            }

            if (!http.Request.HasFormContentType)
            {
                return Refuse(logger, StatusCodes.Status400BadRequest, "invalid_request",
                    "Send a multipart form: ticket, file and optionally sha256 (curl -F ticket=... -F file=@receipt.pdf <url>).");
            }

            IFormCollection form;
            try
            {
                form = await http.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxRequestBytes }, ct);
            }
            catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
            {
                return Refuse(logger, StatusCodes.Status413PayloadTooLarge, "too_large",
                    $"The upload is larger than {MaxRequestBytes / 1024 / 1024} MB; Projector accepts receipts up to 2 MB.");
            }

            var (connectionId, ticketError) = tickets.Redeem(form["ticket"].ToString());
            if (connectionId is null)
            {
                return Refuse(logger, StatusCodes.Status401Unauthorized, "invalid_ticket", ticketError!);
            }

            if (form.Files.Count != 1 || form.Files[0] is not { Length: > 0 } file)
            {
                return Refuse(logger, StatusCodes.Status400BadRequest, "invalid_request",
                    "Send exactly one non-empty file in the form field file.");
            }

            byte[] content;
            using (var buffer = new MemoryStream((int)file.Length))
            {
                await file.CopyToAsync(buffer, ct);
                content = buffer.ToArray();
            }

            var fileName = form["file_name"].ToString() is { Length: > 0 } given ? given : file.FileName;
            try
            {
                var result = await expenses.UploadReceiptToPoolAsync(connectionId, fileName, content, form["sha256"].ToString(), ct);
                return Results.Json(result);
            }
            catch (ArgumentException ex)
            {
                return Refuse(logger, StatusCodes.Status400BadRequest, "invalid_receipt", ex.Message);
            }
            catch (ProjectorAuthorizationException ex)
            {
                return Refuse(logger, StatusCodes.Status401Unauthorized, "reconnect", ex.Message);
            }
            catch (ProjectorApiException ex)
            {
                logger.LogWarning(ex, "Receipt upload failed in Projector ({ErrorCode})", ex.ErrorCode);
                return Results.Json(
                    new { error = ex.ErrorCode ?? "projector_error", message = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    private static IResult Refuse(ILogger logger, int status, string error, string message)
    {
        logger.LogWarning("Receipt upload refused: {Status} {Error}", status, error);
        return Results.Json(new { error, message }, statusCode: status);
    }
}
