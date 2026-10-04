using System.Globalization;
using System.Xml.Linq;
using Projector.Domain.Expenses;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Request bodies for the expense methods. Element order follows the WCF contracts (base type first, then the
/// WSDL sequence): an element out of order is dropped by Projector without an error.
/// </summary>
public static class ProjectorExpenseEnvelopes
{
    private static readonly XNamespace Pws = SoapNamespaces.Pws;
    private static readonly XNamespace Req = SoapNamespaces.Req;
    private static readonly XNamespace Com = SoapNamespaces.Com;
    private static readonly XNamespace Tim = SoapNamespaces.Tim;
    private static readonly XNamespace Doc = SoapNamespaces.Doc;

    /// <summary>A null resource sends no ResourceIdentity: Projector lists the caller's reports.</summary>
    public static XElement GetExpenseReports(string ticket, string? resourceId, int months, bool unreceivedOnly) =>
        Body("PwsGetExpenseReports", ticket,
            new XElement(Tim + "IncludeUnreceivedOnlyFlag", Bool(unreceivedOnly)),
            new XElement(Tim + "NumberMonths", months.ToString(CultureInfo.InvariantCulture)),
            Resource(Tim + "ResourceIdentity", resourceId));

    /// <summary>One existing report with its cards and receipts (credit cards left out).</summary>
    public static XElement GetExpenseDocument(string ticket, string reportNumber) =>
        Body("PwsGetExpenseDocument", ticket,
            new XElement(Tim + "ExcludeCreditCostCardsFlag", "true"),
            new XElement(Tim + "ExpenseDocumentIdentity", ReportRef(reportNumber)),
            new XElement(Tim + "RetrieveCostCardsFlag", "true"),
            new XElement(Tim + "RetrieveReceiptsFlag", "true"));

    /// <summary>An empty new expense report for the resource (nothing is created); shows its disbursed currency.</summary>
    public static XElement GetNewExpenseReport(string ticket, string resourceId) =>
        Body("PwsGetExpenseDocument", ticket,
            new XElement(Tim + "NewDocumentType", "E"),
            Resource(Tim + "ResourceIdentity", resourceId));

    public static XElement GetResourceExpenseEntryInfo(string ticket, string resourceId, string startDate, string endDate) =>
        Body("PwsGetResourceExpenseEntryInfo", ticket,
            new XElement(Tim + "EndDate", ProjectorDateHelpers.ToSoapDate(endDate)),
            Resource(Tim + "ResourceIdentity", resourceId),
            new XElement(Tim + "StartDate", ProjectorDateHelpers.ToSoapDate(startDate)));

    public static XElement GetExpenseEntryParameters(string ticket) =>
        Body("PwsGetExpenseEntryParameters", ticket);

    /// <summary>A null resource sends no ResourceIdentity: the caller's open days.</summary>
    public static XElement GetResourceExpenseSchedule(string ticket, string? resourceId, string startDate, string endDate) =>
        Body("PwsGetResourceExpenseSchedule", ticket,
            new XElement(Tim + "EndDate", ProjectorDateHelpers.ToSoapDate(endDate)),
            Resource(Tim + "ResourceIdentity", resourceId),
            new XElement(Tim + "StartDate", ProjectorDateHelpers.ToSoapDate(startDate)));

    /// <summary>Currencies with Projector's rate into <paramref name="disbursedCurrency"/> on <paramref name="date"/>.</summary>
    public static XElement GetCurrencies(string ticket, string resourceId, string disbursedCurrency, string date) =>
        Body("PwsGetCurrencies", ticket,
            new XElement(Tim + "DisbursedCurrencyIdentity", new XElement(Com + "CurrencyCode", disbursedCurrency)),
            new XElement(Tim + "EffectiveDate", ProjectorDateHelpers.ToSoapDate(date)),
            Resource(Tim + "ResourceIdentity", resourceId));

    public static XElement GetDocumentManagementParameters(string ticket) =>
        Body("PwsGetDocumentManagementParameters", ticket);

    public static XElement GetReceiptPoolFolder(string ticket, string userUid) =>
        Body("PwsGetFolder", ticket,
            new XElement(Doc + "FolderTypeCode", "UserReceiptPoolFolder"),
            new XElement(Doc + "UserIdentity", new XElement(Com + "UserUid", userUid)));

    public static XElement GetFolderContents(string ticket, string folderUid) =>
        Body("PwsGetFolderContents", ticket,
            new XElement(Doc + "FolderIdentity", new XElement(Com + "FolderUid", folderUid)));

    /// <summary>
    /// Creates a report (no <see cref="ExpenseSaveRequest.ReportUid"/>) or updates one. An update sends the
    /// ExpenseDocument block with the UID and Timestamp: with only ExpenseDocumentIdentity Projector answers Ok but
    /// ignores the cards and receipts (tested 2026-10-04). Never submits, never e-mails approvers.
    /// </summary>
    public static XElement SaveExpenseDocument(string ticket, ExpenseSaveRequest request)
    {
        var isUpdate = request.ReportUid is not null;
        if (isUpdate && string.IsNullOrWhiteSpace(request.ReportTimestamp))
        {
            throw new ArgumentException("An update needs the report's Timestamp.", nameof(request));
        }

        var document = new XElement(Tim + "ExpenseDocument",
            isUpdate ? new XElement(Com + "ExpenseDocumentUid", request.ReportUid) : null,
            new XElement(Tim + "DocumentName", request.ReportName),
            new XElement(Tim + "DocumentType", "E"),
            Resource(Tim + "ResourceIdentity", request.ResourceId),
            isUpdate ? new XElement(Tim + "Timestamp", request.ReportTimestamp) : null);

        return Body("PwsSaveExpenseDocument", ticket,
            new XElement(Tim + "ExcludeCreditCostCardsFlag", "true"),
            document,
            new XElement(Tim + "FullDetailFlag", "true"),
            // Needed on a create too: without it Projector reads ReceiptUid 0 as "update receipt 0" and refuses
            // the whole save with EntityNotFound (tested 2026-10-04).
            new XElement(Tim + "InsertReceiptsIfNotFoundOnUpdateFlag", Bool(request.Receipts.Count > 0)),
            new XElement(Tim + "RetrieveCostCardsFlag", "true"),
            new XElement(Tim + "RetrieveReceiptsFlag", "true"),
            request.Cards.Count == 0 ? null : new XElement(Tim + "SaveCostCards", request.Cards.Select(Card)),
            request.Receipts.Count == 0
                ? null
                : new XElement(Tim + "SaveReceipts", request.Receipts.Select(r => Receipt(r, isUpdate ? request.ReportUid : null))),
            new XElement(Tim + "SendNotificationEmailFlag", "false"),
            new XElement(Tim + "SubmitFlag", "false"));
    }

    private static XElement Card(ExpenseCardWrite card) =>
        new(Tim + "PwsCostCardDetail",
            card.CardUid is null ? null : new XElement(Com + "CostCardUid", card.CardUid),
            new XElement(Com + "ReferenceId", card.ReferenceId),
            string.IsNullOrWhiteSpace(card.Description) ? null : new XElement(Tim + "Description", card.Description),
            new XElement(Tim + "ExpenseTypeIdentity", new XElement(Com + "ExpenseTypeName", card.ExpenseType)),
            new XElement(Tim + "IncurredAmount", Number(card.Amount)),
            new XElement(Tim + "IncurredDate", ProjectorDateHelpers.ToSoapDate(card.Date)),
            new XElement(Tim + "IncurredOpsCurrencyIdentity", new XElement(Com + "OpsCurrencyCode", card.Currency)),
            string.IsNullOrWhiteSpace(card.Location)
                ? null
                : new XElement(Tim + "LocationIdentity", new XElement(Com + "LocationName", card.Location)),
            new XElement(Tim + "ProjectIdentity", new XElement(Com + "ProjectCode", card.ProjectCode)),
            card.Timestamp is null ? null : new XElement(Tim + "Timestamp", card.Timestamp),
            new XElement(Tim + "TotalAmountDisbursedCurrency", Number(card.DisbursedAmount)));

    /// <summary>ReceiptUid 0 = a new link; the card is named by UID (existing) or by the ReferenceId of this save.</summary>
    private static XElement Receipt(ExpenseReceiptLink receipt, string? reportUid) =>
        new(Tim + "PwsReceiptDetail",
            new XElement(Com + "ReceiptUid", "0"),
            receipt.ReferenceId is null ? null : new XElement(Com + "ReferenceId", receipt.ReferenceId),
            new XElement(Tim + "DocumentIdentity", new XElement(Com + "DocumentRefUid", receipt.DocumentUid)),
            new XElement(Tim + "EntireExpenseReportFlag", "false"),
            reportUid is null
                ? null
                : new XElement(Tim + "ExpenseDocumentIdentity", new XElement(Com + "ExpenseDocumentUid", reportUid)),
            new XElement(Tim + "ReceiptCostCards",
                new XElement(Tim + "PwsReceiptCostCard",
                    new XElement(Tim + "CostCardIdentity",
                        receipt.CardUid is not null
                            ? new XElement(Com + "CostCardUid", receipt.CardUid)
                            : new XElement(Com + "ReferenceId", receipt.CardReferenceId)))),
            new XElement(Tim + "DocumentName", receipt.DocumentName));

    private static XElement Body(string method, string ticket, params object?[] content) =>
        new(Pws + method,
            new XElement(Pws + "serviceRequest",
                new XElement(Req + "SessionTicket", ticket),
                content));

    private static XElement? Resource(XName name, string? resourceId) =>
        string.IsNullOrWhiteSpace(resourceId)
            ? null
            : new XElement(name, ProjectorIdentityRefs.BuildResourceRef(resourceId).Elements());

    private static XElement ReportRef(string reportNumber) =>
        long.TryParse(reportNumber, out _) && reportNumber.Length >= 15
            ? new XElement(Com + "ExpenseDocumentUid", reportNumber)
            : new XElement(Com + "DocumentNumber", reportNumber);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Number(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);
}
