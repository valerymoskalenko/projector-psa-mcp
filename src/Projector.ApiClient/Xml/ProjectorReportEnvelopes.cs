using System.Globalization;
using System.Xml.Linq;
using Projector.Domain.Reports;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Request builders for get_report: PwsGetReportOutput (WCF) and the legacy (ASMX) report and export methods.
/// Parameter elements follow the order of the service description.
/// </summary>
public static class ProjectorReportEnvelopes
{
    /// <summary>The stored output of a report as CSV with a header row (Projector sends none unless asked).</summary>
    public static XElement BuildGetReportOutput(string sessionTicket, string? webServiceCode, string? outputUid)
    {
        var identity = new XElement(SoapNamespaces.Rep + "ReportIdentity");
        if (!string.IsNullOrWhiteSpace(outputUid))
        {
            identity.Add(new XElement(SoapNamespaces.Com + "ReportUid", outputUid.Trim()));
        }
        else
        {
            identity.Add(new XElement(SoapNamespaces.Com + "WebServiceCode", webServiceCode?.Trim()));
        }

        return new XElement(SoapNamespaces.Pws + "PwsGetReportOutput",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Rep + "ColumnHeaders", "firstrow"),
                new XElement(SoapNamespaces.Rep + "Format", "CSV"),
                identity));
    }

    public static XDocument BuildSubmitReportSpec(string sessionTicket, string specUid) =>
        Asmx(sessionTicket, "SubmitReportSpec", P("ReportSpecUid", specUid.Trim()));

    public static XDocument BuildGetReportStatus(string sessionTicket, string? outputUid) =>
        Asmx(sessionTicket, "GetReportStatus",
            string.IsNullOrWhiteSpace(outputUid) ? null : P("ReportOutputUid", outputUid.Trim()));

    public static XDocument BuildSubmitOlapGinsuExport(string sessionTicket, GinsuExportRequest request) =>
        Asmx(sessionTicket, "SubmitOlapGinsuExport",
            string.IsNullOrWhiteSpace(request.CostCenterNumber) ? null : P("CostCenterReferenceSystemId", request.CostCenterNumber.Trim()),
            P("ResourceFilteringFlag", request.FilterByResources),
            P("BeginDate", request.BeginDate),
            P("EndDate", request.EndDate),
            P("CutoffDate", request.CutoffDate),
            P("BillableOnlyFlag", request.BillableOnly),
            P("TimeoffFlag", request.IncludeTimeOff),
            string.IsNullOrWhiteSpace(request.BucketWidth) ? null : P("BucketWidth", request.BucketWidth),
            P("ReportingCurrencyCode", request.CurrencyCode),
            P("IncludeRequestsFlag", false),
            P("IncludeUnapprovedFlag", request.IncludeUnapproved),
            P("IncludeUnapprovedExpensesFlag", false));

    public static XDocument BuildExportOlapGinsuRecords(
        string sessionTicket, string requestId, long startAfterRowIndex, int maxRows, bool onlyCount) =>
        Asmx(sessionTicket, "ExportOlapGinsuRecords",
            P("RequestId", requestId.Trim()),
            P("StartAfterRowIndex", startAfterRowIndex.ToString(CultureInfo.InvariantCulture)),
            P("MaxRowsToReturn", maxRows),
            P("OnlyCountRows", onlyCount));

    public static XDocument BuildExportProjectList(
        string sessionTicket, bool openForTimeOnly, string? projectCodesAfter, int maxRows, bool onlyCount) =>
        Asmx(sessionTicket, "ExportProjectList",
            P("LimitToOpenForTimeOnly", openForTimeOnly),
            P("LimitToOpenForCostOnly", false),
            P("MaxRowsToReturn", maxRows),
            string.IsNullOrWhiteSpace(projectCodesAfter) ? null : P("ProjectCodesAfter", projectCodesAfter),
            P("OnlyCountRows", onlyCount));

    public static XDocument BuildExportTimeCards(
        string sessionTicket,
        string minWorkDate,
        string maxWorkDate,
        string? approvedMinTimestamp,
        string? approvedIdsAfter,
        int maxRows,
        bool onlyCount) =>
        Asmx(sessionTicket, "ExportTimeCards",
            P("MinWorkDate", minWorkDate),
            P("MaxWorkDate", maxWorkDate),
            string.IsNullOrWhiteSpace(approvedMinTimestamp) ? null : P("ApprovedMinTimestamp", approvedMinTimestamp),
            string.IsNullOrWhiteSpace(approvedIdsAfter) ? null : P("ApprovedIdsAfter", approvedIdsAfter),
            P("MaxRowsToReturn", maxRows),
            P("IncludeBillableOnly", false),
            P("OnlyCountRows", onlyCount));

    /// <summary>A legacy request: session ticket in the header, <c>method/request/Parameters</c> in the body.</summary>
    public static XDocument Asmx(string sessionTicket, string method, params XElement?[] parameters) =>
        new(
            new XElement(SoapNamespaces.SoapEnv + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", SoapNamespaces.SoapEnv),
                new XAttribute(XNamespace.Xmlns + "data", SoapNamespaces.Data),
                new XElement(SoapNamespaces.SoapEnv + "Header",
                    new XElement(SoapNamespaces.Data + "OpsAuthenticationHeader",
                        new XElement(SoapNamespaces.Data + "SessionTicket", sessionTicket))),
                new XElement(SoapNamespaces.SoapEnv + "Body",
                    new XElement(SoapNamespaces.Data + method,
                        new XElement(SoapNamespaces.Data + "request",
                            new XElement(SoapNamespaces.Data + "Parameters", parameters.Where(p => p is not null)))))));

    private static XElement P(string name, string value) => new(SoapNamespaces.Data + name, value);

    private static XElement P(string name, bool value) => new(SoapNamespaces.Data + name, value ? "true" : "false");

    private static XElement P(string name, int value) =>
        new(SoapNamespaces.Data + name, value.ToString(CultureInfo.InvariantCulture));
}
