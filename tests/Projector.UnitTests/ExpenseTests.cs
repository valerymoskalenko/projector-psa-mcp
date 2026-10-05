using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Projector.ApiClient.Xml;
using Projector.Application.Auth;
using Projector.Application.Tools;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Expenses;
using Projector.Domain.Timecards;

namespace Projector.UnitTests;

/// <summary>
/// list_expenses and save_expenses: parsers (fixtures with the captured shape, invented values), request bodies and
/// the save rules against a fake client. Live writes are manual tests, never part of this suite.
/// </summary>
public class ExpenseTests
{
    private const string ConnectionId = "test";
    private static readonly string FixturesDir = ResolveDir("tests", "Projector.UnitTests", "fixtures");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Pdf = "%PDF-1.7\n1 0 obj\n"u8.ToArray();
    private static readonly string PngBase64 = Convert.ToBase64String(Png);

    // ---------------------------------------------------------------- parsers

    [Fact]
    public void ParseReportList_ReadsReportsAndPermission()
    {
        var list = ProjectorExpenseParsers.ParseReportList(Fixture("expense_reports.xml"));

        list.CanCreate.Should().BeTrue();
        list.Reports.Should().HaveCount(2);
        var paid = list.Reports[0];
        paid.Number.Should().Be("ER00100");
        paid.Status.Should().Be("P");
        paid.CardCount.Should().Be(3);
        paid.Total.Should().Be(150.5);
        paid.Currency.Should().Be("USD");
        paid.EarliestDate.Should().Be("2026-07-04");
        paid.Locked.Should().BeTrue();
        paid.Projects.Should().ContainSingle(p => p.Code == "P000100-001");
        paid.ResourceId.Should().Be("10001");
        paid.ResourceUid.Should().Be("2000000000000000001");
        list.Reports[1].Locked.Should().BeFalse();
    }

    [Fact]
    public void ParseReportDetail_ReadsCardsReceiptsAndTimestamp()
    {
        var report = ProjectorExpenseParsers.ParseReportDetail(Fixture("expense_document.xml"))!;

        report.Number.Should().Be("ER00200");
        report.Uid.Should().Be("1000000000000000200");
        report.Timestamp.Should().Be("AAAAAAAAAAk=");
        report.Currency.Should().Be("USD");
        report.Total.Should().Be(62.26);
        report.Editable.Should().BeTrue();
        report.ResourceUid.Should().Be("2000000000000000001");

        var taxi = report.Cards[0];
        taxi.Uid.Should().Be("4000000000000000001");
        taxi.Date.Should().Be("2026-07-12");
        taxi.ExpenseType.Should().Be("Travel - Internal");
        taxi.Amount.Should().Be(38.04);
        taxi.Currency.Should().Be("CAD");
        taxi.FxRate.Should().Be(1.4157729924);
        taxi.DisbursedAmount.Should().Be(26.87);
        taxi.Location.Should().Be("CA - Canada");
        taxi.ProjectCode.Should().Be("C000001-003");
        taxi.Timestamp.Should().Be("AAAAAAAAAAE=");
        taxi.Editable.Should().BeTrue();
        report.Cards[1].Editable.Should().BeFalse("an approved, locked card can't be changed");

        report.Receipts.Should().ContainSingle();
        report.Receipts[0].DocumentUid.Should().Be("6000000000000000001");
        report.Receipts[0].CardUids.Should().Equal("4000000000000000001");
    }

    [Fact]
    public void ParseEntryInfo_ReadsProjectsTypesAndLocations()
    {
        var info = ProjectorExpenseParsers.ParseEntryInfo(Fixture("expense_entry_info.xml"));

        info.Projects.Should().HaveCount(2);
        var internalProject = info.Projects.Single(p => p.Code == "C000001-003");
        internalProject.ClientName.Should().Be("Internal");
        internalProject.EngagementName.Should().Be("Operations");
        internalProject.AnyExpenseType.Should().BeFalse();
        internalProject.ExpenseTypes.Should().Contain(["Office Fee", "Travel - Internal"]);
        internalProject.OpenDate.Should().Be("2021-01-01");
        internalProject.CloseDate.Should().BeNull();
        info.Projects.Single(p => p.Code == "P000100-001").AnyExpenseType.Should().BeTrue();

        info.ExpenseTypes.Should().HaveCount(5);
        info.ExpenseTypes.Single(t => t.Name == "Mileage - Internal").Mileage.Should().BeTrue();
        info.ExpenseTypes.Single(t => t.Name == "Sales - Meals/Entertainment").DescriptionRequired.Should().BeFalse();
        info.ExpenseTypes.Single(t => t.Name == "Office Fee").Group.Should().Be("Internal");
        info.Locations.Should().Equal("US - United States", "CA - Canada");
    }

    [Fact]
    public void ParseSaveResult_MapsCardAndReceiptErrorsByReferenceId()
    {
        var result = ProjectorExpenseParsers.ParseSaveResult(Fixture("expense_save_receipt_error.xml"));

        result.Succeeded.Should().BeFalse();
        result.Report.Should().BeNull();
        result.CardIssues.Should().ContainSingle(i => i.ReferenceId == "card1"
            && i.Code == "TotalAmountDisbursedCurrencyIsRequiredForNewCostCards");
        result.ReceiptIssues.Should().ContainSingle(i => i.ReferenceId == "receipt0" && i.Code == "EntityNotFound");
        result.Messages.Should().ContainSingle(m => m.Code == "ReceiptErrors");
    }

    [Fact]
    public void ParseCurrencies_KeepsActiveCurrenciesWithRates()
    {
        var doc = XDocument.Parse(
            """
            <r xmlns:a="t" xmlns:b="c">
              <a:PwsCurrencyRate><a:Currency><b:OpsCurrencyCode>CAD</b:OpsCurrencyCode><b:CurrencyName>Canadian Dollars</b:CurrencyName><b:DecimalDigits>2</b:DecimalDigits><b:InactiveFlag>false</b:InactiveFlag></a:Currency><a:FxRate>0.70632792500499175</a:FxRate></a:PwsCurrencyRate>
              <a:PwsCurrencyRate><a:Currency><b:OpsCurrencyCode>XXX</b:OpsCurrencyCode><b:InactiveFlag>true</b:InactiveFlag></a:Currency><a:FxRate>2</a:FxRate></a:PwsCurrencyRate>
              <a:PwsCurrencyRate><a:Currency><b:OpsCurrencyCode>USD</b:OpsCurrencyCode><b:DecimalDigits>2</b:DecimalDigits></a:Currency><a:FxRate>1</a:FxRate></a:PwsCurrencyRate>
            </r>
            """);

        var rates = ProjectorExpenseParsers.ParseCurrencies(doc);

        rates.Select(r => r.Code).Should().Equal("CAD", "USD");
        rates[0].Rate.Should().BeApproximately(0.706327925, 1e-9);
    }

    [Fact]
    public void ParseUploadResult_ReturnsTheDocumentOrThrowsTheReason()
    {
        var ok = ProjectorExpenseParsers.ParseUploadResult(
            """{"DocumentRefId":null,"DocumentRefUid":"6000000000000000009","DocumentName":"r.jpeg","DocumentSize":3676,"MimeType":"image/jpeg","Status":0,"Messages":[]}""");
        ok.DocumentUid.Should().Be("6000000000000000009");
        ok.Name.Should().Be("r.jpeg");
        ok.Size.Should().Be(3676);

        var act = () => ProjectorExpenseParsers.ParseUploadResult(
            """{"refid":null,"Status":2,"Messages":[{"Code":75012,"Mnemonic":"DocumentStorageCapacityExceeded","Text":"Storage is full."}]}""");
        act.Should().Throw<ProjectorApiException>()
            .Where(e => e.ErrorCode == "DocumentStorageCapacityExceeded" && e.Message.Contains("Storage is full."));
    }

    // ---------------------------------------------------------------- request bodies

    [Fact]
    public void SaveEnvelope_NewReport_HasNoUidAndNeverSubmits()
    {
        var body = ProjectorExpenseEnvelopes.SaveExpenseDocument("ticket", new ExpenseSaveRequest
        {
            ReportName = "Trip",
            ResourceId = "10001",
            Cards = [Card("card0")],
            Receipts = [new ExpenseReceiptLink { DocumentUid = "600", DocumentName = "r.pdf", ReferenceId = "receipt0", CardReferenceId = "card0" }]
        });

        var document = Single(body, "ExpenseDocument");
        Single(document, "ExpenseDocumentUid", allowMissing: true).Should().BeNull();
        Single(document, "Timestamp", allowMissing: true).Should().BeNull();
        Single(body, "SubmitFlag").Value.Should().Be("false");
        Single(body, "SendNotificationEmailFlag").Value.Should().Be("false");
        Single(body, "InsertReceiptsIfNotFoundOnUpdateFlag").Value.Should().Be("true",
            "without it Projector reads ReceiptUid 0 as an update of receipt 0 and refuses the save");
        body.Descendants().Select(e => e.Name.LocalName).Should().NotContain(
            ["ApproverIdentities", "DeleteCostCards", "DeleteReceipts", "SubmissionComments", "AdministratorComments"]);

        var receipt = Single(body, "PwsReceiptDetail");
        Single(receipt, "ReceiptUid").Value.Should().Be("0");
        receipt.Elements().Single(e => e.Name.LocalName == "ReferenceId").Value.Should().Be("receipt0");
        Single(receipt, "ExpenseDocumentIdentity", allowMissing: true).Should().BeNull();
        Single(Single(receipt, "CostCardIdentity"), "ReferenceId").Value.Should().Be("card0");

        // WCF drops elements out of contract order without an error.
        Single(body, "PwsCostCardDetail").Elements().Select(e => e.Name.LocalName).Should().Equal(
            "ReferenceId", "Description", "ExpenseTypeIdentity", "IncurredAmount", "IncurredDate",
            "IncurredOpsCurrencyIdentity", "LocationIdentity", "ProjectIdentity", "TotalAmountDisbursedCurrency");
        Single(body, "serviceRequest").Elements().Select(e => e.Name.LocalName).Should().Equal(
            "SessionTicket", "ExcludeCreditCostCardsFlag", "ExpenseDocument", "FullDetailFlag",
            "InsertReceiptsIfNotFoundOnUpdateFlag", "RetrieveCostCardsFlag", "RetrieveReceiptsFlag", "SaveCostCards",
            "SaveReceipts", "SendNotificationEmailFlag", "SubmitFlag");
    }

    [Fact]
    public void SaveEnvelope_Update_SendsTheDocumentBlockWithUidAndTimestamp()
    {
        var body = ProjectorExpenseEnvelopes.SaveExpenseDocument("ticket", new ExpenseSaveRequest
        {
            ReportUid = "1000000000000000200",
            ReportTimestamp = "AAAAAAAAAAk=",
            ReportName = "Office supplies",
            ResourceId = "10001",
            Cards = [Card("card0") with { CardUid = "400", Timestamp = "AAAAAAAAAAE=" }],
            Receipts = [new ExpenseReceiptLink { DocumentUid = "600", DocumentName = "r.pdf", CardUid = "400" }]
        });

        var document = Single(body, "ExpenseDocument");
        document.Elements().Select(e => e.Name.LocalName).Should().Equal(
            "ExpenseDocumentUid", "DocumentName", "DocumentType", "ResourceIdentity", "Timestamp");
        Single(document, "Timestamp").Value.Should().Be("AAAAAAAAAAk=");
        var card = Single(body, "PwsCostCardDetail");
        card.Elements().First().Name.LocalName.Should().Be("CostCardUid");
        Single(card, "Timestamp").Value.Should().Be("AAAAAAAAAAE=");
        Single(Single(body, "PwsReceiptDetail"), "ExpenseDocumentIdentity").Value.Should().Be("1000000000000000200");
        Single(Single(body, "CostCardIdentity"), "CostCardUid").Value.Should().Be("400");
    }

    [Fact]
    public void SaveEnvelope_UpdateWithoutTimestamp_IsRefused()
    {
        var act = () => ProjectorExpenseEnvelopes.SaveExpenseDocument("ticket", new ExpenseSaveRequest
        {
            ReportUid = "1", ReportName = "x", ResourceId = "10001", Cards = [Card("card0")]
        });

        act.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------- save rules

    [Fact]
    public async Task DryRun_ConvertsWithProjectorsRate_AndWritesNothing()
    {
        var (svc, fake) = CreateService();

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(amount: 38.04, currency: "CAD", type: "Travel - Internal", date: "2026-07-12")], dryRun: true, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("dry_run");
        var card = result.GetProperty("results")[0];
        card.GetProperty("status").GetString().Should().Be("valid");
        card.GetProperty("card").GetProperty("amount_report_currency").GetDouble().Should().Be(26.87);
        card.GetProperty("warnings")[0].GetString().Should().Be(ExpenseToolService.ReceiptRequiredWarning);
        fake.Saves.Should().BeEmpty();
        fake.Uploads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Conference", "not allowed on C000001-003")]
    [InlineData("Mileage - Internal", "per mile")]
    [InlineData("Unknown Type", "Unknown expense type")]
    public async Task InvalidExpenseType_RefusesTheWholeCall(string type, string message)
    {
        var (svc, fake) = CreateService();

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(), Input(type: type)], dryRun: false, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("refused");
        result.GetProperty("results")[1].GetProperty("errors")[0].GetString().Should().Contain(message);
        result.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("valid");
        fake.Saves.Should().BeEmpty();
        fake.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task CardChecks_CoverDescriptionDatesDaysLocationAndReceipts()
    {
        var (svc, fake) = CreateService();
        fake.Schedule = [new ExpenseDay("2026-09-29", CanEnter: false, PeriodClosed: true)];

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
        [
            Input(description: " "),
            Input(project: "P000100-001", date: "2026-10-01"),
            Input(date: "2026-09-29"),
            Input(location: "Mars"),
            Input(receiptName: "r.exe", receiptBase64: PngBase64),
            Input(receiptName: "r.png", receiptBase64: "not base64!"),
            Input(receiptName: "r.png", receiptBase64: Convert.ToBase64String([.. Png, .. new byte[ExpenseToolService.DefaultReceiptMaxBytes]])),
            Input(amount: 0),
            Input(currency: "ZZZ")
        ], dryRun: false, CancellationToken.None));

        string Error(int i) => result.GetProperty("results")[i].GetProperty("errors")[0].GetString()!;
        Error(0).Should().Contain("needs a description");
        Error(1).Should().Contain("outside the dates of project P000100-001");
        Error(2).Should().Contain("closed for expenses");
        Error(3).Should().Contain("Unknown location 'Mars'");
        Error(4).Should().Contain("file_name must end in");
        Error(5).Should().Contain("not valid base64");
        Error(6).Should().Contain("Projector accepts up to 2.00 MB");
        Error(7).Should().Contain("amount must be more than 0");
        Error(8).Should().Contain("Unknown currency 'ZZZ'");
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_UploadsOnce_SavesOnce_AndConfirmsByReadingBack()
    {
        var (svc, fake) = CreateService();

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
        [
            Input(receiptName: "taxi.png", receiptBase64: PngBase64),
            Input(amount: 10, currency: "CAD", type: "Travel - Internal", location: "ca - canada")
        ], dryRun: false, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("saved");
        result.GetProperty("saved_count").GetInt32().Should().Be(2);
        result.GetProperty("submitted").GetBoolean().Should().BeFalse();
        result.GetProperty("results")[0].GetProperty("receipt").GetProperty("linked").GetBoolean().Should().BeTrue();
        fake.Uploads.Should().ContainSingle().Which.Should().Be("taxi.png");
        var request = fake.Saves.Should().ContainSingle().Subject;
        request.ReportUid.Should().BeNull();
        request.ResourceId.Should().Be("10001");
        request.Cards[1].Location.Should().Be("CA - Canada");
        request.Cards[1].DisbursedAmount.Should().Be(10 * 0.70632792500499175,
            "the unrounded amount keeps Projector's own rate; Projector rounds the total itself");
        result.GetProperty("results")[1].GetProperty("card").GetProperty("amount_report_currency").GetDouble().Should().Be(7.06);
        request.Receipts.Should().ContainSingle(r => r.CardReferenceId == "card0" && r.ReferenceId == "receipt0" && r.DocumentUid == "upload0");
    }

    [Fact]
    public async Task Save_CardMissingAfterReadBack_IsNotApplied()
    {
        var (svc, fake) = CreateService();
        fake.DropCards = true;

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip", [Input()], dryRun: false, CancellationToken.None));

        result.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("not_applied");
        result.GetProperty("not_applied_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Save_RefusedByProjector_ShowsEachCardsError()
    {
        var (svc, fake) = CreateService();
        fake.SaveResult = _ => ProjectorExpenseParsers.ParseSaveResult(Fixture("expense_save_receipt_error.xml"));

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(receiptName: "r.png", receiptBase64: PngBase64), Input()], dryRun: false, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("failed");
        result.GetProperty("results")[0].GetProperty("errors")[0].GetString().Should().StartWith("receipt: Receipt was not found");
        result.GetProperty("results")[1].GetProperty("errors")[0].GetString().Should().Contain("Total amount in disbursed currency");
        result.GetProperty("receipts_in_pool").GetArrayLength().Should().Be(1, "the uploaded receipt waits in the pool");
    }

    [Fact]
    public async Task UploadFailure_StopsBeforeTheSave()
    {
        var (svc, fake) = CreateService();
        fake.UploadError = new ProjectorApiException("Storage is full.", "DocumentStorageCapacityExceeded");

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(receiptName: "r.png", receiptBase64: PngBase64), Input()], dryRun: false, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("failed");
        result.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("failed");
        result.GetProperty("results")[1].GetProperty("status").GetString().Should().Be("not_attempted");
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_SendsTheReportTimestamp_AndOnlyEditableCards()
    {
        var (svc, fake) = CreateService();

        var locked = Json(await svc.SaveExpensesAsync(ConnectionId, "ER00200", null,
            [Input(cardUid: "4000000000000000002", type: "Sales - Meals/Entertainment")], dryRun: false, CancellationToken.None));
        locked.GetProperty("results")[0].GetProperty("errors")[0].GetString().Should().Contain("can't be changed");

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, "ER00200", null,
            [Input(cardUid: "4000000000000000001", type: "Travel - Internal", amount: 40, currency: "CAD", date: "2026-07-12")],
            dryRun: false, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("saved");
        var request = fake.Saves.Should().ContainSingle().Subject;
        request.ReportUid.Should().Be("1000000000000000200");
        request.ReportTimestamp.Should().Be("AAAAAAAAAAk=");
        request.ReportName.Should().Be("Office supplies");
        request.Cards[0].CardUid.Should().Be("4000000000000000001");
        request.Cards[0].Timestamp.Should().Be("AAAAAAAAAAE=");
    }

    [Fact]
    public async Task Update_OfAnotherPersonsReport_IsRefused()
    {
        var (svc, fake) = CreateService();
        fake.Self = new ExpenseIdentity("10002", "2000000000000000002", "Someone Else", "3000000000000000002");

        var act = () => svc.SaveExpensesAsync(ConnectionId, "ER00200", null, [Input()], dryRun: false, CancellationToken.None);

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("NotOwnExpenseReport");
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAnyExpenseReport_TheUserIsTold()
    {
        var (svc, fake) = CreateService();
        fake.Self = null;

        var act = () => svc.SaveExpensesAsync(ConnectionId, null, "Trip", [Input()], dryRun: true, CancellationToken.None);

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("NoExpenseIdentity");
    }

    [Fact]
    public async Task ListExpenses_ShowsReportCardsWithProjectorsRate()
    {
        var (svc, _) = CreateService();

        var result = Json(await svc.ListExpensesAsync(ConnectionId, null, "ER00200", 12, false, null, false, null, null, 50, 0,
            CancellationToken.None));

        var cards = result.GetProperty("report").GetProperty("cards");
        cards.GetArrayLength().Should().Be(2);
        var taxi = cards.EnumerateArray().Single(c => c.GetProperty("card_uid").GetString() == "4000000000000000001");
        taxi.GetProperty("rate").GetDouble().Should().BeApproximately(0.70632793, 1e-8);
        taxi.GetProperty("receipts")[0].GetString().Should().Be("taxi.pdf");
        taxi.GetProperty("editable").GetBoolean().Should().BeTrue();
        taxi.TryGetProperty("missing_receipt", out var none).Should().BeTrue();
        none.ValueKind.Should().Be(JsonValueKind.Null, "the taxi has its receipt");
        var dinner = cards.EnumerateArray().Single(c => c.GetProperty("card_uid").GetString() == "4000000000000000002");
        dinner.GetProperty("missing_receipt").GetBoolean().Should().BeTrue("35.39 is above the type's 25 threshold and has no receipt");
    }

    [Fact]
    public async Task ReceiptRule_ThresholdDecidesTheWarning()
    {
        var (svc, _) = CreateService();

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
        [
            Input(type: "Sales - Meals/Entertainment", amount: 10),
            Input(type: "Sales - Meals/Entertainment", amount: 30)
        ], dryRun: true, CancellationToken.None));

        result.GetProperty("results")[0].GetProperty("warnings")[0].GetString().Should().Be("no receipt (not required for this type)");
        result.GetProperty("results")[1].GetProperty("warnings")[0].GetString().Should().Be(ExpenseToolService.ReceiptRequiredWarning);
    }

    [Fact]
    public async Task ListExpenses_Options_ListWhatASaveNeeds()
    {
        var (svc, _) = CreateService();

        var result = Json(await svc.ListExpensesAsync(ConnectionId, null, null, 12, false, null, true, "2026-07-12", null, 50, 0,
            CancellationToken.None));

        result.GetProperty("reports").GetProperty("count").GetInt32().Should().Be(2);
        var options = result.GetProperty("options");
        options.GetProperty("projects_total").GetInt32().Should().Be(2);
        options.GetProperty("report_currency").GetString().Should().Be("USD");
        options.GetProperty("currencies").EnumerateArray().Select(c => c.GetString()).Should().Contain("CAD");
        options.GetProperty("expense_types").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Mileage - Internal")
            .GetProperty("supported").GetBoolean().Should().BeFalse();
        options.GetProperty("rules").GetProperty("receipt_max_kb").GetDouble().Should().Be(2048);
        var types = options.GetProperty("expense_types").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        types["Office Fee"].GetProperty("receipt_required").GetBoolean().Should().BeTrue();
        types["Sales - Meals/Entertainment"].GetProperty("receipt_required").GetString().Should().Be("from 25 USD");
        types["Conference"].GetProperty("receipt_required").GetBoolean().Should().BeFalse();
        options.GetProperty("receipt_pool")[0].GetProperty("receipt_uid").GetString().Should().Be("pool1");
    }

    [Fact]
    public async Task ReadBack_RateDifferentFromProjectorsSystemRate_IsWarned()
    {
        var (svc, fake) = CreateService();
        fake.StoredFxRate = (1.4235, 1.4157729924);

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(amount: 12, currency: "CAD", type: "Travel - Internal")], dryRun: false, CancellationToken.None));

        result.GetProperty("results")[0].GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(w => w!.StartsWith("Projector stored rate"));
    }

    [Fact]
    public void SaveEnvelope_SendsTheConvertedAmountUnrounded()
    {
        var body = ProjectorExpenseEnvelopes.SaveExpenseDocument("ticket", new ExpenseSaveRequest
        {
            ReportName = "Trip", ResourceId = "10001", Cards = [Card("card0") with { DisbursedAmount = 12 * 0.7026233528078851 }]
        });

        Single(body, "TotalAmountDisbursedCurrency")!.Value.Should().Be((12 * 0.7026233528078851).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(38.04, 0.70632792500499175, 26.87)]
    [InlineData(10, 0.70262335, 7.03)]
    [InlineData(0.005, 1, 0.01)]
    public void ConvertAmount_RoundsToTheCurrencyDigits(double amount, double rate, double expected) =>
        ExpenseToolService.ConvertAmount(amount, rate, 2).Should().Be(expected);

    // ---------------------------------------------------------------- receipt sources (v0.10.0)

    [Fact]
    public async Task SourceUrl_IsDownloadedChecked_AndUploadedUnderItsName()
    {
        var downloader = new FakeDownloader { File = new DownloadedReceipt(Pdf, "invoice.pdf") };
        var (svc, fake) = CreateService(downloader);

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(sourceUrl: "https://files.example.com/s/abc?download=1", sha256: ReceiptFiles.Sha256(Pdf))],
            dryRun: false, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("saved");
        downloader.Urls.Should().Equal("https://files.example.com/s/abc?download=1");
        fake.Uploads.Should().Equal("invoice.pdf");
        fake.UploadedBytes.Single().Should().Equal(Pdf);
        result.GetProperty("results")[0].GetProperty("receipt").GetProperty("linked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SourceUrl_DryRunDownloadsButUploadsNothing_AndNamesAFileWithoutExtension()
    {
        var downloader = new FakeDownloader { File = new DownloadedReceipt(Png, null) };
        var (svc, fake) = CreateService(downloader);

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(sourceUrl: "https://files.example.com/download")], dryRun: true, CancellationToken.None));

        result.GetProperty("results")[0].GetProperty("status").GetString().Should().Be("valid");
        result.GetProperty("results")[0].GetProperty("receipt").GetProperty("name").GetString().Should().Be("receipt.png");
        downloader.Urls.Should().ContainSingle();
        fake.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task SourceUrl_DownloadError_RefusesTheCall()
    {
        var downloader = new FakeDownloader { Error = "The receipt link answered 404 Not Found; it must be a public link that downloads the file." };
        var (svc, fake) = CreateService(downloader);

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(sourceUrl: "https://files.example.com/x.pdf"), Input()], dryRun: false, CancellationToken.None));

        result.GetProperty("action").GetString().Should().Be("refused");
        result.GetProperty("results")[0].GetProperty("errors")[0].GetString().Should().Contain("404");
        fake.Uploads.Should().BeEmpty();
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task SourceUrl_WebPageInsteadOfAFile_IsRefused()
    {
        var downloader = new FakeDownloader { File = new DownloadedReceipt("<!DOCTYPE html><html>"u8.ToArray(), "x.pdf") };
        var (svc, fake) = CreateService(downloader);

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(sourceUrl: "https://files.example.com/x.pdf")], dryRun: false, CancellationToken.None));

        result.GetProperty("results")[0].GetProperty("errors")[0].GetString().Should().Contain("not a PDF, PNG, JPEG or GIF");
        fake.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Receipt_TwoSourcesOrAWrongSha256_AreRefusedBeforeAnyUpload()
    {
        var (svc, fake) = CreateService(new FakeDownloader { File = new DownloadedReceipt(Pdf, "a.pdf") });

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
        [
            Input(receiptName: "r.png", receiptBase64: PngBase64, sourceUrl: "https://files.example.com/a.pdf"),
            Input(receiptName: "r.png", receiptBase64: PngBase64, sha256: new string('0', 64)),
            Input(receiptName: "r.png", receiptBase64: PngBase64, sha256: ReceiptFiles.Sha256(Png).ToUpperInvariant())
        ], dryRun: false, CancellationToken.None));

        string? Error(int i) => result.GetProperty("results")[i].GetProperty("errors") is { ValueKind: JsonValueKind.Array } e ? e[0].GetString() : null;
        Error(0).Should().Contain("Give one of");
        Error(1).Should().Contain("SHA-256").And.Contain("changed on the way");
        Error(2).Should().BeNull("the right hash in upper case is fine");
        fake.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_PdfStoredWithAnotherSize_Warns()
    {
        var (svc, fake) = CreateService();
        fake.StoredSizeDelta = -100;

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(receiptName: "taxi.pdf", receiptBase64: Convert.ToBase64String(Pdf))], dryRun: false, CancellationToken.None));

        result.GetProperty("results")[0].GetProperty("warnings").EnumerateArray()
            .Should().Contain(w => w.GetString()!.StartsWith("receipt size differs"));
    }

    [Fact]
    public async Task Upload_PhotoStoredSmaller_GetsANoteNotAWarning()
    {
        var (svc, fake) = CreateService();
        fake.StoredSizeDelta = -5;

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(receiptName: "taxi.png", receiptBase64: PngBase64)], dryRun: false, CancellationToken.None));

        var card = result.GetProperty("results")[0];
        card.GetProperty("receipt").GetProperty("note").GetString().Should().Be(ExpenseToolService.ReencodedNote);
        card.GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Null, "a re-encoded photo is not a warning");
    }

    [Theory]
    [InlineData("a.jpeg", 100L, 100L, false, false)]
    [InlineData("a.jpeg", 100L, 90L, false, true)]
    [InlineData("a.png", 100L, 90L, false, true)]
    [InlineData("a.jpeg", 100L, 110L, true, false)]
    [InlineData("a.pdf", 100L, 90L, true, false)]
    [InlineData("a.pdf", 100L, null, false, false)]
    public void SizeCheck_PhotosMayShrink_PdfsMustMatch(string name, long sentBytes, long? storedBytes, bool warns, bool notes)
    {
        var (warning, note) = ExpenseToolService.SizeCheck(sentBytes, new UploadedReceipt("1", name, storedBytes, null), name);
        (warning is not null).Should().Be(warns);
        (note is not null).Should().Be(notes);
    }

    [Fact]
    public async Task Update_WithOnlyCardUidAndDescription_KeepsTheCardsValuesAndReceipt()
    {
        var (svc, fake) = CreateService();
        var taxi = fake.Report.Cards.Single(c => c.Uid == "4000000000000000001");

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, "ER00200", null,
            [new SaveExpenseInput(null, null, null, "Taxi to the hotel", null, CardUid: "4000000000000000001")],
            dryRun: true, CancellationToken.None));
        result.GetProperty("results")[0].GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Null, "the taxi already has a receipt");

        await svc.SaveExpensesAsync(ConnectionId, "ER00200", null,
            [new SaveExpenseInput(null, null, null, "Taxi to the hotel", null, CardUid: "4000000000000000001")],
            dryRun: false, CancellationToken.None);

        var request = fake.Saves.Should().ContainSingle().Subject;
        var sent = request.Cards.Single();
        sent.CardUid.Should().Be(taxi.Uid);
        sent.Description.Should().Be("Taxi to the hotel");
        sent.Date.Should().Be(taxi.Date);
        sent.ExpenseType.Should().Be(taxi.ExpenseType);
        sent.ProjectCode.Should().Be(taxi.ProjectCode);
        sent.Amount.Should().Be(taxi.Amount!.Value);
        sent.Currency.Should().Be(taxi.Currency);
        request.Receipts.Should().BeEmpty("the card keeps its receipt; nothing is relinked");
    }

    [Fact]
    public async Task NewCard_WithoutTheRequiredFields_IsRefused()
    {
        var (svc, fake) = CreateService();

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [new SaveExpenseInput(null, null, null, "Taxi", null)], dryRun: true, CancellationToken.None));

        var errors = result.GetProperty("results")[0].GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        errors.Should().Contain("date is required for a new card.");
        errors.Should().Contain("project_code is required for a new card.");
        errors.Should().Contain("expense_type is required for a new card.");
        errors.Should().Contain(e => e!.StartsWith("amount is required for a new card"));
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Brief_ListsOnlyTheCardsThatNeedAttention()
    {
        var (svc, _) = CreateService();

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
        [
            Input(amount: 1, receiptName: "a.png", receiptBase64: PngBase64),
            Input(amount: 2)
        ], dryRun: false, CancellationToken.None, brief: true));

        result.GetProperty("action").GetString().Should().Be("saved");
        result.GetProperty("results").GetArrayLength().Should().Be(1);
        result.GetProperty("results")[0].GetProperty("index").GetInt32().Should().Be(1);
        result.GetProperty("results_omitted").GetInt32().Should().Be(1);
        result.GetProperty("saved_count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Brief_IsIgnoredOnADryRun()
    {
        var (svc, _) = CreateService();

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(amount: 1, receiptName: "a.png", receiptBase64: PngBase64), Input(amount: 2)],
            dryRun: true, CancellationToken.None, brief: true));

        result.GetProperty("results").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ProjectNotOpen_NamesTheProjectsOfTheUsersTimeCards()
    {
        var soap = ResourceArgumentTests.RecordingSoap.Create();
        soap.Timecards.Add(new Timecard { ProjectCode = "C000009-001", ProjectName = "Internal Time", WorkHours = 30, WorkDate = "2026-07-10" });
        soap.Timecards.Add(new Timecard { ProjectCode = "C000001-003", ProjectName = "Client Work", WorkHours = 8, WorkDate = "2026-07-11" });
        var (svc, _) = CreateService(soap: soap);

        var result = Json(await svc.SaveExpensesAsync(ConnectionId, null, "Trip",
            [Input(project: "C000009-001")], dryRun: true, CancellationToken.None));

        var error = result.GetProperty("results")[0].GetProperty("errors")[0].GetString();
        error.Should().StartWith("Project C000009-001 is not open for your expenses")
            .And.Contain("Your time cards from 2026-07-05 to 2026-07-19 are on: C000009-001 Internal Time (30 h, not open for expenses); C000001-003 Client Work (8 h, open for expenses)");
        soap.Calls.Should().Equal("ListTimecardsAsync(<none>)");
    }

    [Fact]
    public async Task ValidCards_ReadNoTimeCards()
    {
        var soap = ResourceArgumentTests.RecordingSoap.Create();
        var (svc, _) = CreateService(soap: soap);

        await svc.SaveExpensesAsync(ConnectionId, null, "Trip", [Input()], dryRun: true, CancellationToken.None);

        soap.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Options_ProjectFilterWithoutMatch_GivesTheTimeCardHint()
    {
        var soap = ResourceArgumentTests.RecordingSoap.Create();
        soap.Timecards.Add(new Timecard { ProjectCode = "C000009-001", ProjectName = "Internal Time", WorkHours = 30, WorkDate = "2026-07-10" });
        var (svc, _) = CreateService(soap: soap);

        var found = Json(await svc.ListExpensesAsync(ConnectionId, null, null, 12, false, null, true, "2026-07-12", "C000001-003", 50, 0,
            CancellationToken.None));
        found.GetProperty("options").GetProperty("project_hint").ValueKind.Should().Be(JsonValueKind.Null);
        soap.Calls.Should().BeEmpty();

        var missing = Json(await svc.ListExpensesAsync(ConnectionId, null, null, 12, false, null, true, "2026-07-12", "C000009-001", 50, 0,
            CancellationToken.None));
        missing.GetProperty("options").GetProperty("project_hint").GetString()
            .Should().Contain("C000009-001 Internal Time (30 h, not open for expenses)");
        soap.Calls.Should().Equal("ListTimecardsAsync(<none>)");
    }

    [Fact]
    public async Task UploadToPool_StoresTheFile_AndAnswersItsReceiptUid()
    {
        var (svc, fake) = CreateService();

        var result = Json(await svc.UploadReceiptToPoolAsync(ConnectionId, "Sep12 Uber.pdf", Pdf, ReceiptFiles.Sha256(Pdf), CancellationToken.None));

        result.GetProperty("receipt_uid").GetString().Should().Be("upload0");
        result.GetProperty("size_bytes").GetInt64().Should().Be(Pdf.Length);
        result.GetProperty("sha256").GetString().Should().Be(ReceiptFiles.Sha256(Pdf));
        fake.Uploads.Should().Equal("Sep12 Uber.pdf");
    }

    [Theory]
    [InlineData("r.exe", "must end in")]
    [InlineData("r.png", "is a PDF")]
    public async Task UploadToPool_RefusesABadFile_WithoutUploading(string name, string message)
    {
        var (svc, fake) = CreateService();

        var act = () => svc.UploadReceiptToPoolAsync(ConnectionId, name, Pdf, null, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(message);
        fake.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Options_OfferTheReceiptUpload_OnlyForTheSignedInUser()
    {
        var (svc, _) = CreateService(tickets: new FakeTickets());

        var mine = Json(await svc.ListExpensesAsync(ConnectionId, null, null, 12, false, null, true, "2026-07-12", null, 50, 0, CancellationToken.None));

        var upload = mine.GetProperty("options").GetProperty("receipt_upload");
        upload.GetProperty("url").GetString().Should().Be("https://mcp.example/receipts/upload");
        upload.GetProperty("ticket").GetString().Should().Be("ticket-for-test");
        upload.GetProperty("max_kb").GetDouble().Should().Be(2048);
        upload.GetProperty("how").GetString().Should().Contain("curl");
    }

    // ---------------------------------------------------------------- helpers

    private sealed class FakeDownloader : IReceiptDownloader
    {
        public DownloadedReceipt? File { get; set; }
        public string? Error { get; set; }
        public List<string> Urls { get; } = [];

        public Task<DownloadedReceipt> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken)
        {
            lock (Urls)
            {
                Urls.Add(url);
            }

            return Error is not null ? throw new ReceiptDownloadException(Error) : Task.FromResult(File!);
        }
    }

    private sealed class FakeTickets : IReceiptUploadTickets
    {
        public ReceiptUploadOffer Issue(string connectionId) =>
            new("https://mcp.example/receipts/upload", "ticket-for-" + connectionId, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private static SaveExpenseInput Input(
        string date = "2026-07-12",
        string project = "C000001-003",
        string type = "Office Fee",
        string? description = "Test",
        double amount = 1,
        string? currency = null,
        string? location = null,
        string? cardUid = null,
        string? receiptName = null,
        string? receiptBase64 = null,
        string? receiptUid = null,
        string? sourceUrl = null,
        string? sha256 = null) =>
        new(date, project, type, description, amount, currency, location, cardUid, receiptName, receiptBase64, receiptUid, sourceUrl, sha256);

    private static ExpenseCardWrite Card(string referenceId) => new()
    {
        ReferenceId = referenceId,
        Date = "2026-07-12",
        ExpenseType = "Travel - Internal",
        Description = "Taxi",
        Amount = 38.04,
        Currency = "CAD",
        DisbursedAmount = 26.87,
        ProjectCode = "C000001-003",
        Location = "CA - Canada"
    };

    private static XElement? Single(XElement parent, string localName, bool allowMissing = false)
    {
        var found = parent.Descendants().Where(e => e.Name.LocalName == localName).ToList();
        if (allowMissing && found.Count == 0)
        {
            return null;
        }

        return found.Should().ContainSingle().Subject;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static (ExpenseToolService Service, FakeExpenseClient Fake) CreateService(
        IReceiptDownloader? downloader = null,
        IReceiptUploadTickets? tickets = null,
        ResourceArgumentTests.RecordingSoap? soap = null)
    {
        var store = new InMemoryProjectorConnectionStore();
        store.Save(new ProjectorConnection
        {
            ConnectionId = ConnectionId,
            SessionTicket = "ticket",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            GrantedScope = "allowFullPermissions",
            SoapServiceAuthority = "https://example.invalid",
            RestServiceAuthority = "https://example.invalid"
        });
        var connections = new ProjectorConnectionService(store, new NoRefreshTokenClient());
        var fake = new FakeExpenseClient
        {
            Reports = ProjectorExpenseParsers.ParseReportList(Fixture("expense_reports.xml")),
            Report = ProjectorExpenseParsers.ParseReportDetail(Fixture("expense_document.xml"))!,
            EntryInfo = ProjectorExpenseParsers.ParseEntryInfo(Fixture("expense_entry_info.xml"))
        };
        var tools = new ProjectorToolService(connections, soap is null ? null! : (IProjectorSoapClient)(object)soap);
        return (new ExpenseToolService(connections, fake, new TimeEntryCache(), tools, NullLogger<ExpenseToolService>.Instance,
            downloader, tickets), fake);
    }

    private static XDocument Fixture(string name) => XDocument.Load(Path.Combine(FixturesDir, name));

    private static string ResolveDir(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(string.Join('/', relative));
    }

    /// <summary>
    /// A Projector stand-in. A save answers with the report as Projector would return it: the sent cards with new
    /// UIDs and the receipts linked to them (unless <see cref="DropCards"/>).
    /// </summary>
    private sealed class FakeExpenseClient : IProjectorExpenseClient
    {
        public ExpenseReportList Reports { get; set; } = new([], true);
        public ExpenseReportDetail Report { get; set; } = new();
        public ExpenseEntryInfo EntryInfo { get; set; } = new([], [], []);
        public ExpenseEntryRules Rules { get; set; } = new() { ReceiptsOnCards = true, ReceiptMaxBytes = 2 * 1024 * 1024 };
        public IReadOnlyList<ExpenseDay> Schedule { get; set; } = [];
        public ExpenseIdentity? Self { get; set; } = new("10001", "2000000000000000001", "Jane Doe", "3000000000000000001");
        public bool DropCards { get; set; }
        /// <summary>(stored, system) FxRate on every saved card; null = none.</summary>
        public (double Stored, double System)? StoredFxRate { get; set; }
        public ProjectorApiException? UploadError { get; set; }
        public Func<ExpenseSaveRequest, ExpenseSaveResult>? SaveResult { get; set; }
        public List<ExpenseSaveRequest> Saves { get; } = [];
        public List<string> Uploads { get; } = [];
        public List<byte[]> UploadedBytes { get; } = [];
        /// <summary>Bytes Projector reports beyond what was sent.</summary>
        public long StoredSizeDelta { get; set; }

        public Task<ExpenseReportList> ListReportsAsync(ProjectorConnection c, string? r, int m, bool u, CancellationToken ct = default) =>
            Task.FromResult(Reports);

        public Task<ExpenseReportDetail?> GetReportAsync(ProjectorConnection c, string number, CancellationToken ct = default) =>
            Task.FromResult(number == Report.Number ? Report : null);

        public Task<ExpenseReportDetail?> GetNewReportAsync(ProjectorConnection c, string r, CancellationToken ct = default) =>
            Task.FromResult<ExpenseReportDetail?>(new ExpenseReportDetail { Currency = "USD" });

        public Task<ExpenseEntryInfo> GetEntryInfoAsync(ProjectorConnection c, string r, string s, string e, CancellationToken ct = default) =>
            Task.FromResult(EntryInfo);

        public Task<ExpenseEntryRules> GetEntryRulesAsync(ProjectorConnection c, CancellationToken ct = default) =>
            Task.FromResult(Rules);

        public Task<IReadOnlyList<ExpenseDay>> GetScheduleAsync(ProjectorConnection c, string? r, string s, string e, CancellationToken ct = default) =>
            Task.FromResult(Schedule);

        public Task<IReadOnlyList<CurrencyRate>> GetCurrenciesAsync(ProjectorConnection c, string r, string d, string date, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CurrencyRate>>(
                [new CurrencyRate("CAD", "Canadian Dollars", 2, 0.70632792500499175), new CurrencyRate("USD", "US Dollars", 2, 1)]);

        public Task<IReadOnlyList<ExpenseReceiptRule>> GetReceiptRulesAsync(
            ProjectorConnection c, string r, string d, string date, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExpenseReceiptRule>>(
            [
                new ExpenseReceiptRule("Office Fee", true, 0),
                new ExpenseReceiptRule("Travel - Internal", true, 0),
                new ExpenseReceiptRule("Sales - Meals/Entertainment", true, 25),
                new ExpenseReceiptRule("Conference", false, null)
            ]);

        public Task<ExpenseIdentity?> FindSelfAsync(ProjectorConnection c, CancellationToken ct = default) => Task.FromResult(Self);

        public Task<ReceiptPool> GetReceiptPoolAsync(ProjectorConnection c, string userUid, CancellationToken ct = default) =>
            Task.FromResult(new ReceiptPool("folder1", "https://doc.example.invalid/1"));

        public Task<IReadOnlyList<PoolReceipt>> ListPoolAsync(ProjectorConnection c, string folderUid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PoolReceipt>>([new PoolReceipt("pool1", "old.pdf", 2048, "2026-10-01", "application/pdf")]);

        public Task<UploadedReceipt> UploadReceiptAsync(ProjectorConnection c, ReceiptPool pool, string fileName, byte[] content, CancellationToken ct = default)
        {
            if (UploadError is not null)
            {
                throw UploadError;
            }

            Uploads.Add(fileName);
            UploadedBytes.Add(content);
            var name = Path.ChangeExtension(fileName, ".jpeg");
            return Task.FromResult(new UploadedReceipt("upload" + (Uploads.Count - 1), name, content.Length + StoredSizeDelta, "image/jpeg"));
        }

        public Task<ExpenseSaveResult> SaveAsync(ProjectorConnection c, ExpenseSaveRequest request, CancellationToken ct = default)
        {
            Saves.Add(request);
            if (SaveResult is not null)
            {
                return Task.FromResult(SaveResult(request));
            }

            var before = request.ReportUid is null ? [] : Report.Cards.Where(k => request.Cards.All(w => w.CardUid != k.Uid));
            var cards = DropCards
                ? []
                : request.Cards.Select(w => new ExpenseCard
                {
                    Uid = w.CardUid ?? "new-" + w.ReferenceId,
                    Date = w.Date,
                    ExpenseType = w.ExpenseType,
                    Description = w.Description,
                    Amount = w.Amount,
                    Currency = w.Currency,
                    DisbursedAmount = w.DisbursedAmount,
                    ProjectCode = w.ProjectCode,
                    ApprovalStatus = "D",
                    FxRate = StoredFxRate?.Stored,
                    SystemFxRate = StoredFxRate?.System
                }).ToList();
            var receipts = request.Receipts.Select(r => new ExpenseReceipt
            {
                DocumentUid = r.DocumentUid,
                Name = r.DocumentName,
                CardUids = [r.CardUid ?? "new-" + r.CardReferenceId]
            }).ToList();
            var report = new ExpenseReportDetail
            {
                Number = request.ReportUid is null ? "ER09999" : Report.Number,
                Uid = request.ReportUid ?? "1000000000000009999",
                Name = request.ReportName,
                Timestamp = "AAAAAAAAAAz=",
                Currency = "USD",
                Cards = [.. before, .. cards],
                Receipts = receipts
            };
            return Task.FromResult(new ExpenseSaveResult { Succeeded = true, Number = report.Number, Uid = report.Uid, Report = report });
        }
    }

    private sealed class NoRefreshTokenClient : IProjectorTokenClient
    {
        public Task<ProjectorTokenResponse> ExchangeAuthorizationCodeAsync(
            string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectorTokenResponse> RefreshAsync(ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAsync(ProjectorConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
