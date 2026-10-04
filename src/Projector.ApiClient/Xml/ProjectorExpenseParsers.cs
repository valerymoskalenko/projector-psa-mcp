using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Projector.Domain.Exceptions;
using Projector.Domain.Expenses;

namespace Projector.ApiClient.Xml;

/// <summary>Parsers for the expense methods. Fields are read from direct children, so nested identities never leak in.</summary>
public static class ProjectorExpenseParsers
{
    public static ExpenseReportList ParseReportList(XDocument doc)
    {
        var result = XmlNodeHelpers.LocalNode(doc, "PwsGetExpenseReportsResult");
        var reports = XmlNodeHelpers.ChildLocalNodes(Child(result, "ExpenseReports"), "PwsExpenseDocument")
            .Select(ParseReportSummary)
            .ToList();
        return new ExpenseReportList(reports, Bool(Child(result, "CreateExpenseReportsPermissionFlag")));
    }

    private static ExpenseReportSummary ParseReportSummary(XElement e)
    {
        var resource = Child(e, "ResourceIdentity");
        return new ExpenseReportSummary
        {
            Number = Text(e, "DocumentNumber"),
            Uid = Text(e, "ExpenseDocumentUid"),
            Name = Text(e, "DocumentName"),
            Status = Text(e, "Status"),
            EarliestDate = XmlNodeHelpers.ShortDate(Text(e, "EarliestIncurredDate")),
            LatestDate = XmlNodeHelpers.ShortDate(Text(e, "LatestIncurredDate")),
            CardCount = (int)(Number(e, "CostCardCount") ?? 0),
            Reimbursement = Number(e, "ReimbursementAmount"),
            Total = Number(e, "TotalAmount"),
            Currency = Text(Child(e, "DisbursedCurrencyIdentity"), "CurrencyCode"),
            Projects = XmlNodeHelpers.ChildLocalNodes(Child(e, "Projects"), "PwsProjectSummary")
                .Select(p => new ExpenseProjectRef(Text(p, "ProjectCode"), Text(p, "ProjectName")))
                .ToList(),
            Locked = Bool(Child(e, "ExpenseDocumentLockedFlag")),
            ResourceId = Text(resource, "ResourceReferenceSystemId"),
            ResourceUid = Text(resource, "ResourceUid"),
            ResourceName = Text(resource, "ResourceDisplayName")
        };
    }

    /// <summary>The ExpenseDocument of PwsGetExpenseDocument or PwsSaveExpenseDocument; null when absent.</summary>
    public static ExpenseReportDetail? ParseReportDetail(XDocument doc)
    {
        var element = XmlNodeHelpers.LocalNodes(doc, "ExpenseDocument")
            .FirstOrDefault(e => Child(e, "ExpenseDocumentDetail") is not null);
        return element is null ? null : ParseReportDetail(element);
    }

    private static ExpenseReportDetail ParseReportDetail(XElement e)
    {
        var detail = Child(e, "ExpenseDocumentDetail");
        var resource = Child(detail, "ResourceIdentity");
        return new ExpenseReportDetail
        {
            Number = Text(detail, "DocumentNumber"),
            Uid = Text(detail, "ExpenseDocumentUid"),
            Name = Text(detail, "DocumentName"),
            Timestamp = Text(detail, "Timestamp"),
            Status = Text(e, "Status"),
            Currency = Text(Child(e, "DisbursedCurrencyIdentity"), "CurrencyCode"),
            CurrencyDigits = (int)(Number(e, "DisbursedCurrencyDecimalDigits") ?? 2),
            Locked = Bool(Child(e, "ExpenseDocumentLockedFlag")) || Bool(Child(e, "LockedFlag")),
            MaintainUnavailableReason = Text(e, "MaintainExpenseDocumentUnavailableReason"),
            ResourceId = Text(resource, "ResourceReferenceSystemId"),
            ResourceUid = Text(resource, "ResourceUid"),
            ResourceName = Text(resource, "ResourceDisplayName"),
            Total = Number(e, "CostCardTotalDisbursedAmount"),
            Cards = XmlNodeHelpers.ChildLocalNodes(Child(e, "CostCards"), "PwsCostCardElement").Select(ParseCard).ToList(),
            Receipts = XmlNodeHelpers.ChildLocalNodes(Child(e, "Receipts"), "PwsReceiptDetail").Select(ParseReceipt).ToList()
        };
    }

    private static ExpenseCard ParseCard(XElement e)
    {
        var d = Child(e, "CostCardDetail");
        return new ExpenseCard
        {
            Uid = Text(d, "CostCardUid"),
            Timestamp = Text(d, "Timestamp"),
            Date = XmlNodeHelpers.ShortDate(Text(d, "IncurredDate")),
            ExpenseType = Text(Child(d, "ExpenseTypeIdentity"), "ExpenseTypeName"),
            Description = Text(d, "Description"),
            Amount = Number(d, "IncurredAmount"),
            Currency = Text(Child(d, "IncurredOpsCurrencyIdentity"), "OpsCurrencyCode"),
            FxRate = Number(e, "FxRate"),
            DisbursedAmount = Number(e, "DisbursedAmountDisbursedCurrency") ?? Number(d, "TotalAmountDisbursedCurrency"),
            Units = Number(d, "Units"),
            Location = Text(Child(d, "LocationIdentity"), "LocationName"),
            ProjectCode = Text(Child(d, "ProjectIdentity"), "ProjectCode"),
            ProjectName = Text(e, "ProjectName"),
            ApprovalStatus = Text(e, "ApprovalWorkflowStatus"),
            Locked = Bool(Child(e, "LockedFlag")),
            RejectedReason = Text(e, "RejectedReason"),
            DocumentCount = (int)(Number(e, "DocumentCount") ?? 0)
        };
    }

    private static ExpenseReceipt ParseReceipt(XElement e) =>
        new()
        {
            ReceiptUid = Text(e, "ReceiptUid"),
            DocumentUid = Text(Child(e, "DocumentIdentity"), "DocumentRefUid"),
            Name = Text(e, "DocumentName"),
            MimeType = Text(e, "MimeType"),
            EntireReport = Bool(Child(e, "EntireExpenseReportFlag")),
            CardUids = XmlNodeHelpers.ChildLocalNodes(Child(e, "ReceiptCostCards"), "PwsReceiptCostCard")
                .Select(c => Text(Child(c, "CostCardIdentity"), "CostCardUid"))
                .OfType<string>()
                .ToList()
        };

    public static ExpenseEntryInfo ParseEntryInfo(XDocument doc)
    {
        var result = XmlNodeHelpers.LocalNode(doc, "PwsGetResourceExpenseEntryInfoResult");
        var projects = new List<ExpenseEntryProject>();
        foreach (var client in XmlNodeHelpers.ChildLocalNodes(Child(result, "Clients"), "PwsClientEngagementProjectInfoHierarchyForResourceExpenseEntry"))
        {
            var clientName = Text(client, "ClientName");
            foreach (var engagement in XmlNodeHelpers.ChildLocalNodes(Child(client, "Engagements"), "PwsEngagementProjectInfoHierarchyForResourceExpenseEntry"))
            {
                var engagementName = Text(engagement, "EngagementName");
                foreach (var p in XmlNodeHelpers.ChildLocalNodes(Child(engagement, "Projects"), "PwsProjectInfoForResourceExpenseEntry"))
                {
                    projects.Add(new ExpenseEntryProject
                    {
                        Code = Text(p, "ProjectCode"),
                        Name = Text(p, "ProjectName"),
                        ClientName = clientName,
                        EngagementName = engagementName,
                        OpenDate = XmlNodeHelpers.ShortDate(Text(p, "OpenDate")),
                        CloseDate = XmlNodeHelpers.ShortDate(Text(p, "CloseDate")),
                        AnyExpenseType = Bool(Child(p, "AllowExpenseOnAnyExpenseTypeFlag")),
                        ExpenseTypes = Child(p, "AvailableExpenseTypes")?.Elements()
                            .Select(t => Text(t, "ExpenseTypeName"))
                            .OfType<string>()
                            .ToList() ?? []
                    });
                }
            }
        }

        var types = XmlNodeHelpers.ChildLocalNodes(Child(result, "ExpenseTypes"), "PwsExpenseTypeInfoForResource")
            .Select(t => new ExpenseTypeInfo
            {
                Name = Text(t, "ExpenseTypeName"),
                Group = Text(Child(t, "ExpenseTypeGroupIdentity"), "ExpenseTypeGroupName"),
                DescriptionRequired = Bool(Child(t, "DescriptionRequiredFlag")),
                UnitDriven = Bool(Child(t, "UnitDrivenFlag")),
                Mileage = Bool(Child(t, "MileageFlag")),
                DefaultUnitCost = Number(t, "DefaultUnitCost"),
                Instructions = Text(t, "ExpenseEntryInstructions")
            })
            .ToList();

        var locations = Child(result, "CostCardLocations")?.Elements()
            .Select(l => Text(l, "LocationName"))
            .OfType<string>()
            .ToList() ?? [];

        return new ExpenseEntryInfo(projects, types, locations);
    }

    public static ExpenseEntryRules ParseEntryRules(XDocument doc)
    {
        var p = XmlNodeHelpers.LocalNode(doc, "Parameters");
        return new ExpenseEntryRules
        {
            ReceiptsOnCards = Bool(Child(p, "AllowAttachReceiptsToCcFlag")),
            ReceiptsOnReport = Bool(Child(p, "AllowAttachReceiptsToErFlag")),
            NonBillableAllowed = Bool(Child(p, "AllowNonbillableCostCardsFlag")),
            OutsideProjectDatesAllowed = Bool(Child(p, "AllowCostCardsOutsideProjectDatesFlag")),
            LocationRequired = Bool(Child(p, "RequireLocationFlag")),
            ReceiptMaxBytes = (long)(Number(p, "ReceiptFileSizeQuota") ?? 0),
            EntryForOthersAllowed = Bool(Child(p, "OboPermissionFlag"))
        };
    }

    public static IReadOnlyList<ExpenseDay> ParseSchedule(XDocument doc) =>
        XmlNodeHelpers.LocalNodes(doc, "PwsExpenseScheduleDay")
            .Select(d => new ExpenseDay(
                XmlNodeHelpers.ShortDate(Text(d, "Date")) ?? string.Empty,
                Bool(Child(d, "CanEnterExpenseFlag")),
                Bool(Child(d, "AccountingPeriodClosedFlag"))))
            .Where(d => d.Date.Length > 0)
            .ToList();

    public static IReadOnlyList<CurrencyRate> ParseCurrencies(XDocument doc) =>
        XmlNodeHelpers.LocalNodes(doc, "PwsCurrencyRate")
            .Select(r =>
            {
                var currency = Child(r, "Currency");
                return (Currency: currency, Code: Text(currency, "OpsCurrencyCode"), Rate: Number(r, "FxRate"));
            })
            .Where(x => x.Code is not null && !Bool(Child(x.Currency, "InactiveFlag")) && !Bool(Child(x.Currency, "InstallationCurrencyInactiveFlag")))
            .Select(x => new CurrencyRate(
                x.Code!,
                Text(x.Currency, "CurrencyName"),
                (int)(Number(x.Currency, "DecimalDigits") ?? 2),
                x.Rate))
            .ToList();

    public static IReadOnlyList<ExpenseReceiptRule> ParseReceiptRules(XDocument doc) =>
        XmlNodeHelpers.LocalNodes(doc, "PwsExpenseType")
            .Select(t => (Name: Text(t, "ExpenseTypeName"), Type: t))
            .Where(x => x.Name is not null)
            .Select(x => new ExpenseReceiptRule(
                x.Name!,
                Bool(Child(x.Type, "ReceiptRequiredFlag")),
                Number(x.Type, "ReceiptRequiredThresholdAmountDisbursedCurrency") ?? Number(x.Type, "ReceiptRequiredThresholdAmount")))
            .ToList();

    public static string? ParseDocumentServerUrl(XDocument doc) =>
        XmlNodeHelpers.Value(XmlNodeHelpers.LocalNode(doc, "Parameters"), "DocumentServerUrl");

    public static string? ParseFolderUid(XDocument doc) =>
        Text(XmlNodeHelpers.LocalNode(doc, "FolderIdentity"), "FolderUid");

    public static IReadOnlyList<PoolReceipt> ParseFolderContents(XDocument doc) =>
        XmlNodeHelpers.LocalNodes(doc, "PwsDocument")
            .Where(d => Text(d, "DeletedTimestamp") is null)
            .Select(d => (Uid: Text(d, "DocumentRefUid"), Doc: d))
            .Where(x => x.Uid is not null)
            .Select(x => new PoolReceipt(
                x.Uid!,
                Text(x.Doc, "DocumentName"),
                (long?)Number(x.Doc, "DocumentSize"),
                XmlNodeHelpers.ShortDate(Text(x.Doc, "CreatedTimestamp")),
                Text(x.Doc, "MimeType")))
            .ToList();

    /// <summary>The user linked to a resource (PwsGetResource → ResourceDetail.UserIdentity).</summary>
    public static string? ParseResourceUserUid(XDocument doc) =>
        Text(XmlNodeHelpers.LocalNodes(doc, "UserIdentity").FirstOrDefault(), "UserUid");

    public static ExpenseSaveResult ParseSaveResult(XDocument doc)
    {
        var result = XmlNodeHelpers.LocalNode(doc, "PwsSaveExpenseDocumentResult");
        var identity = Child(result, "ExpenseDocumentIdentity");
        return new ExpenseSaveResult
        {
            Succeeded = Bool(Child(result, "SaveSucceededFlag")),
            Number = Text(identity, "DocumentNumber"),
            Uid = Text(identity, "ExpenseDocumentUid"),
            CardIssues = Issues(Child(result, "CostCardResults")),
            ReceiptIssues = Issues(Child(result, "ReceiptResults")),
            Messages = XmlNodeHelpers.ChildLocalNodes(Child(result, "Messages"), "PwsMessage")
                .Select(m => new ExpenseSaveIssue(null, Text(m, "ErrorCode"), Text(m, "ErrorText") ?? Text(m, "MessageText")))
                .Where(m => m.Code is not null)
                .ToList(),
            Report = ParseReportDetail(doc)
        };
    }

    private static IReadOnlyList<ExpenseSaveIssue> Issues(XElement? results) =>
        results?.Elements()
            .Select(r =>
            {
                var error = Child(r, "ErrorDetail");
                return new ExpenseSaveIssue(Text(r, "ReferenceId"), Text(error, "ErrorCode"), Text(error, "ErrorText"));
            })
            .Where(i => i.Code is not null || i.Text is not null)
            .ToList() ?? [];

    /// <summary>
    /// The JSON answer of {DocumentServerUrl}/AjxAddDocument. Status 0 = stored; otherwise Messages explain why
    /// (e.g. DocumentStorageCapacityExceeded).
    /// </summary>
    public static UploadedReceipt ParseUploadResult(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var status = root.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
        var uid = root.TryGetProperty("DocumentRefUid", out var u) ? u.ToString() : null;
        if (status != 0 || string.IsNullOrWhiteSpace(uid) || uid == "null")
        {
            var message = root.TryGetProperty("Messages", out var messages) && messages.ValueKind == JsonValueKind.Array
                ? string.Join(" ", messages.EnumerateArray().Select(m => m.TryGetProperty("Text", out var t) ? t.GetString() : null).OfType<string>())
                : null;
            var code = root.TryGetProperty("Messages", out var ms) && ms.ValueKind == JsonValueKind.Array
                ? ms.EnumerateArray().Select(m => m.TryGetProperty("Mnemonic", out var c) ? c.GetString() : null).OfType<string>().FirstOrDefault()
                : null;
            throw new ProjectorApiException(
                "Projector did not store the receipt" + (string.IsNullOrWhiteSpace(message) ? "." : ": " + message),
                code ?? "ReceiptUploadFailed");
        }

        return new UploadedReceipt(
            uid,
            root.TryGetProperty("DocumentName", out var n) ? n.GetString() : null,
            root.TryGetProperty("DocumentSize", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetInt64() : null,
            root.TryGetProperty("MimeType", out var m) ? m.GetString() : null);
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Text(XElement? parent, string localName)
    {
        var value = Child(parent, localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static double? Number(XElement? parent, string localName) =>
        double.TryParse(Text(parent, localName), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool Bool(XElement? element) =>
        bool.TryParse(element?.Value, out var b) && b;
}
