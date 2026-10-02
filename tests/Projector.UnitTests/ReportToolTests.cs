using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Projector.ApiClient.Xml;
using Projector.Application.Auth;
using Projector.Application.Tools;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Reports;
using Projector.Mcp.Server.Cli;
using Projector.Mcp.Server.Tools;

namespace Projector.UnitTests;

/// <summary>
/// get_report: envelopes and parsers (fixtures with the captured shape, invented values), the Ginsu clean-up, the
/// cursor and the cache, the service against a fake client, and who sees the tool.
/// </summary>
public class ReportToolTests
{
    private const string ConnectionId = "report-test-connection";
    private static readonly string FixturesDir = ResolveFixturesDir();

    public ReportToolTests()
    {
        // No real waiting in tests: a run that is not ready answers "running" at once.
        ReportToolService.PollDelay = TimeSpan.FromMilliseconds(1);
        ReportToolService.WaitBudget = TimeSpan.FromMilliseconds(20);
    }

    // ---- envelopes ----

    [Fact]
    public void GetReportOutput_AsksForCsvWithAHeaderRow_ByCodeOrByUid()
    {
        var byCode = ProjectorReportEnvelopes.BuildGetReportOutput("ticket", "MY_CODE", null);
        byCode.Descendants(SoapNamespaces.Rep + "Format").Single().Value.Should().Be("CSV");
        byCode.Descendants(SoapNamespaces.Rep + "ColumnHeaders").Single().Value.Should().Be("firstrow");
        byCode.Descendants(SoapNamespaces.Com + "WebServiceCode").Single().Value.Should().Be("MY_CODE");
        byCode.Descendants(SoapNamespaces.Com + "ReportUid").Should().BeEmpty();

        var byUid = ProjectorReportEnvelopes.BuildGetReportOutput("ticket", null, "1000000000000000001");
        byUid.Descendants(SoapNamespaces.Com + "ReportUid").Single().Value.Should().Be("1000000000000000001");
        byUid.Descendants(SoapNamespaces.Com + "WebServiceCode").Should().BeEmpty();
    }

    [Fact]
    public void LegacyEnvelope_CarriesTheTicketInTheHeader_AndOnlyTheGivenParameters()
    {
        var export = new GinsuExportRequest("2026-01-01", "2026-01-31", "2026-01-15", "", null, false, false, false, true);
        var doc = ProjectorReportEnvelopes.BuildSubmitOlapGinsuExport("ticket", export);

        doc.Descendants(SoapNamespaces.Data + "SessionTicket").Single().Value.Should().Be("ticket");
        doc.Descendants(SoapNamespaces.Data + "BeginDate").Single().Value.Should().Be("2026-01-01");
        doc.Descendants(SoapNamespaces.Data + "IncludeUnapprovedFlag").Single().Value.Should().Be("true");
        doc.Descendants(SoapNamespaces.Data + "BucketWidth").Should().BeEmpty("no bucket = one row for the whole range");
        doc.Descendants(SoapNamespaces.Data + "CostCenterReferenceSystemId").Should().BeEmpty();

        var status = ProjectorReportEnvelopes.BuildGetReportStatus("ticket", null);
        status.Descendants(SoapNamespaces.Data + "Parameters").Single().HasElements.Should().BeFalse();
    }

    // ---- parsers ----

    [Fact]
    public void Csv_KeepsQuotedCommasQuotesAndLineBreaks_AndTurnsEmptyCellsIntoNull()
    {
        var table = ProjectorReportParsers.ParseReportOutput(Fixture("report_output_csv.xml"));

        table.Columns.Should().Equal("Resource Display Name", "Customer", "Project Name", "Description", "Work Date", "Person Hours");
        table.Rows.Should().HaveCount(4);
        table.Rows[0][3].Should().Be("Reviewed the setup, sent notes");
        table.Rows[1][3].Should().Be("Workshop: agenda\nand \"follow-up\" list");
        table.Rows[2][3].Should().BeNull();
        table.Rows[3][5].Should().Be("3");
    }

    [Fact]
    public void Csv_WithoutContent_IsAnEmptyTable()
    {
        ProjectorReportParsers.ParseCsv(null).Rows.Should().BeEmpty();
        ProjectorReportParsers.ParseCsv("A,B\r\n").Should().BeEquivalentTo(new ReportTable(["A", "B"], []));
    }

    [Fact]
    public void ReportRuns_FromTheList_HaveNoUid_ButASingleRunHas()
    {
        var list = ProjectorReportParsers.ParseReportRuns(Fixture("report_status_list.xml"));
        list.Should().HaveCount(3);
        list.Should().OnlyContain(r => r.OutputUid == null, "the list carries a placeholder instead of the output UID");
        list[1].Status.Should().Be("Empty");
        list[0].Completed.Should().Be(DateTimeOffset.Parse("2026-01-15T06:05:17.317-05:00"));

        var one = ProjectorReportParsers.ParseReportRuns(Fixture("report_status_one.xml")).Single();
        one.OutputUid.Should().Be("1000000000000000001");
        one.Name.Should().Be("Weekly time cards");
    }

    [Fact]
    public void LegacyFailure_InANormalResponse_Throws()
    {
        var act = () => ProjectorReportParsers.ThrowIfOpsError(Fixture("submit_report_spec_error.xml"), "SubmitReportSpec");
        act.Should().Throw<ProjectorApiException>().Which.ErrorCode.Should().Be("CouldNotFindReportSpec");

        var ok = () => ProjectorReportParsers.ThrowIfOpsError(Fixture("submit_report_spec.xml"), "SubmitReportSpec");
        ok.Should().NotThrow();
        ProjectorReportParsers.SubmittedOutputUid(Fixture("submit_report_spec.xml")).Should().Be("1000000000000000001");
    }

    [Fact]
    public void ExportRows_AreFlat_NestedListsAndEmptyElementsAreLeftOut()
    {
        var rows = ProjectorReportParsers.ParseRows(Fixture("export_project_list.xml"), "Project");

        rows.Should().HaveCount(2);
        rows[0]["ProjectCode"].Should().Be("P000101-001");
        rows[0].Should().NotContainKey("ProjectUdf");
        rows[0].Should().NotContainKey("BookedResources");
        rows[1].Should().NotContainKey("EndDate");
        ProjectorReportParsers.RowCount(Fixture("export_project_list.xml")).Should().Be(2);
    }

    [Fact]
    public void BatchPage_SaysWhenTheExportIsStillWaiting()
    {
        var running = ProjectorReportParsers.ParseBatchPage(Fixture("olap_ginsu_running.xml"), "OlapGinsuRecord");
        running.IsWaiting.Should().BeTrue();
        running.RowCount.Should().Be(-1);

        var done = ProjectorReportParsers.ParseBatchPage(Fixture("olap_ginsu_page.xml"), "OlapGinsuRecord");
        done.IsWaiting.Should().BeFalse();
        done.Rows.Should().HaveCount(6);
    }

    // ---- Ginsu clean-up ----

    [Fact]
    public void Ginsu_DropsAllZeroRows_AndAddsUpTheHoursRowAndTheRevenueRowOfOneItem()
    {
        var clean = GinsuCleaner.Clean(GinsuRows());

        // 6 raw rows: Jane approved (hours row + revenue row), Jane unapproved, an all-zero row, John actual, John planned.
        clean.Rows.Should().HaveCount(4);
        var hours = clean.Columns.ToList().IndexOf("hours");
        var revenue = clean.Columns.ToList().IndexOf("system_revenue");
        var status = clean.Columns.ToList().IndexOf("status");
        var person = clean.Columns.ToList().IndexOf("person");
        var janeApproved = clean.Rows.Single(r => r[person] == "Jane Doe" && r[status] == "approved");
        janeApproved[hours].Should().Be("1.5");
        janeApproved[revenue].Should().Be("150");
    }

    [Fact]
    public void Ginsu_GroupsByTheChosenColumns_AndWritesCodesAsWords()
    {
        var clean = GinsuCleaner.Clean(GinsuRows());

        var byPerson = GinsuCleaner.Group(clean, ["person", "hours", "system_revenue"]);
        byPerson.Columns.Should().Equal("person", "hours", "system_revenue");
        byPerson.Rows.Should().BeEquivalentTo(new[]
        {
            new[] { "Jane Doe", "1.83", "150" },
            new[] { "John Roe", "4.33", "0" }
        });

        // Text columns only: hours is added. A group whose hours are zero is left out.
        var byKind = GinsuCleaner.Group(clean, ["person", "kind", "status"]);
        byKind.Columns.Should().Equal("person", "kind", "status", "hours");
        byKind.Rows.Should().BeEquivalentTo(new[]
        {
            new[] { "Jane Doe", "actual", "approved", "1.5" },
            new[] { "Jane Doe", "actual", "unapproved", "0.33" },
            new[] { "John Roe", "actual", "approved", "0.33" },
            new[] { "John Roe", "planned", "booked", "4" }
        });
    }

    // ---- cursor and cache ----

    [Fact]
    public void Cursor_RoundTrips_AndIsRefusedForAnotherUserOrWhenItIsNotACursor()
    {
        var request = new ReportRequest
        {
            Dataset = "projects", IncludeUnapproved = false, Columns = ["project_code"], MaxRows = 25, Query = "contoso"
        };
        var position = new ReportPosition { After = "P000101-001", Total = 314 };
        var cursor = ReportCursor.Encode("user-a", request, position);

        cursor.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        var (readRequest, readPosition) = ReportCursor.Decode(cursor, "user-a");
        readRequest.Should().BeEquivalentTo(request);
        readPosition.Should().Be(position);

        ((Action)(() => ReportCursor.Decode(cursor, "user-b"))).Should().Throw<ArgumentException>();
        ((Action)(() => ReportCursor.Decode("not-a-cursor", "user-a"))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cache_IsPerUser_AndDoesNotKeepATableThatIsTooLarge()
    {
        using var cache = new ReportCache();
        var alice = Connection("a", "tenant", "alice");
        var bob = Connection("b", "tenant", "bob");
        var report = new CachedReport(new ReportTable(["A"], [["1"]]), null);

        cache.Set(alice, "report::code::x", report, 100).Should().BeTrue();
        cache.TryGet<CachedReport>(alice, "report::code::x", out var found).Should().BeTrue();
        found.Should().BeSameAs(report);
        cache.TryGet<CachedReport>(bob, "report::code::x", out _).Should().BeFalse();

        cache.Set(alice, "big", report, ReportCache.MaxTableBytes + 1).Should().BeFalse();
        cache.TryGet<CachedReport>(alice, "big", out _).Should().BeFalse();
    }

    // ---- service: saved report ----

    [Fact]
    public async Task Report_ByCode_IsCut_AndTheNextPartComesFromTheCacheWithoutANewDownload()
    {
        var (service, fake) = CreateService();

        var first = await CallAsync(service, new ReportRequest { Dataset = "report", Code = "MY_CODE", MaxRows = 3 });
        first.GetProperty("count").GetInt32().Should().Be(3);
        first.GetProperty("total").GetInt32().Should().Be(4);
        first.GetProperty("has_more").GetBoolean().Should().BeTrue();
        first.GetProperty("columns")[0].GetString().Should().Be("Resource Display Name");
        first.GetProperty("recent_report_runs").GetArrayLength().Should().Be(3);
        first.TryGetProperty("data_as_of", out _).Should().BeFalse("CSV fetched by code has no run time");

        var second = await CallAsync(service, new ReportRequest { Cursor = first.GetProperty("next_cursor").GetString() });
        second.GetProperty("count").GetInt32().Should().Be(1);
        second.GetProperty("has_more").GetBoolean().Should().BeFalse();
        second.GetProperty("rows")[0][0].GetString().Should().Be("John Roe");
        second.TryGetProperty("available_columns", out _).Should().BeFalse("only the first part lists them");
        fake.OutputCalls.Should().Be(1);
    }

    [Fact]
    public async Task Report_ColumnsAndQuery_ShapeTheAnswer()
    {
        var (service, _) = CreateService();

        var answer = await CallAsync(service, new ReportRequest
        {
            Dataset = "report", Code = "MY_CODE", Columns = ["person hours", "ProjectName"], Query = "fabrikam"
        });

        answer.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).Should().Equal("Person Hours", "Project Name");
        answer.GetProperty("total").GetInt32().Should().Be(2);

        var act = () => service.GetReportAsync(ConnectionId, new ReportRequest { Dataset = "report", Code = "MY_CODE", Columns = ["nope"] }, default);
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("Resource Display Name");
    }

    [Fact]
    public async Task Report_NeedsExactlyOneAddress_AndAnUnknownCodeIsExplained()
    {
        var (service, fake) = CreateService();

        var none = () => service.GetReportAsync(ConnectionId, new ReportRequest { Dataset = "report" }, default);
        await none.Should().ThrowAsync<ArgumentException>();
        var two = () => service.GetReportAsync(ConnectionId, new ReportRequest { Dataset = "report", Code = "A", OutputUid = "1" }, default);
        await two.Should().ThrowAsync<ArgumentException>();
        var notDigits = () => service.GetReportAsync(ConnectionId, new ReportRequest { Dataset = "report", SpecUid = "12ab" }, default);
        await notDigits.Should().ThrowAsync<ArgumentException>();

        fake.OutputError = new ProjectorApiException("Report Request was not found.", "EntityNotFound");
        var missing = () => service.GetReportAsync(ConnectionId, new ReportRequest { Dataset = "report", Code = "GONE" }, default);
        (await missing.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("report_not_found");
    }

    [Fact]
    public async Task Report_BySpecUid_RunsOnce_AnswersRunning_ThenReturnsTheRunWithItsTime()
    {
        var (service, fake) = CreateService();
        fake.RunStatus = "Running";

        var running = await CallAsync(service, new ReportRequest { Dataset = "report", SpecUid = "555", MaxRows = 2 });
        running.GetProperty("status").GetString().Should().Be("running");
        running.GetProperty("has_more").GetBoolean().Should().BeTrue();

        fake.RunStatus = null;
        var done = await CallAsync(service, new ReportRequest { Cursor = running.GetProperty("next_cursor").GetString() });
        done.GetProperty("status").GetString().Should().Be("ok");
        done.GetProperty("data_as_of").GetString().Should().StartWith("2026-01-15T16:23:47");
        fake.SubmitReportCalls.Should().Be(1, "the cursor carries the run; asking again must not start another");

        // The next part addresses the finished run by its output UID: no new run, no new download.
        var next = await CallAsync(service, new ReportRequest { Cursor = done.GetProperty("next_cursor").GetString() });
        next.GetProperty("count").GetInt32().Should().Be(2);
        fake.SubmitReportCalls.Should().Be(1);
        fake.OutputCalls.Should().Be(1);
        fake.LastOutputUid.Should().Be("1000000000000000001");
    }

    // ---- service: Ginsu ----

    [Fact]
    public async Task Ginsu_ReadsTheWholeBatchOnce_GroupsIt_AndServesOtherColumnsFromTheCache()
    {
        var (service, fake) = CreateService();
        var request = new ReportRequest
        {
            Dataset = "ginsu", StartDate = "2026-01-11", EndDate = "2026-01-17", Columns = ["person", "hours"]
        };

        var byPerson = await CallAsync(service, request);
        byPerson.GetProperty("rows").GetRawText().Should().Be("""[["Jane Doe",1.83],["John Roe",4.33]]""");
        byPerson.GetProperty("total").GetInt32().Should().Be(2);
        byPerson.TryGetProperty("data_as_of", out _).Should().BeTrue();

        var byStatus = await CallAsync(service, request with { Columns = ["status", "hours"], Query = "booked" });
        byStatus.GetProperty("rows").GetRawText().Should().Be("""[["booked",4]]""");
        fake.SubmitGinsuCalls.Should().Be(1);
    }

    [Fact]
    public async Task Ginsu_StillQueued_AnswersRunning_AndADeletedBatchIsRunAgain()
    {
        var (service, fake) = CreateService();
        fake.BatchStatus = "Queued";
        var request = new ReportRequest { Dataset = "ginsu", StartDate = "2026-01-11", EndDate = "2026-01-17" };

        var running = await CallAsync(service, request);
        running.GetProperty("status").GetString().Should().Be("running");
        fake.SubmitGinsuCalls.Should().Be(1);

        // Projector deleted the batch before the agent came back: the cursor outlived it.
        fake.BatchStatus = "Deleted";
        var done = await CallAsync(service, new ReportRequest { Cursor = running.GetProperty("next_cursor").GetString() });
        done.GetProperty("status").GetString().Should().Be("ok");
        fake.SubmitGinsuCalls.Should().Be(2);
    }

    [Fact]
    public async Task Ginsu_NeedsDates_AndRefusesABatchThatIsTooLarge()
    {
        var (service, fake) = CreateService();

        var noDates = () => service.GetReportAsync(ConnectionId, new ReportRequest { Dataset = "ginsu" }, default);
        await noDates.Should().ThrowAsync<ArgumentException>();
        var tooLong = () => service.GetReportAsync(
            ConnectionId, new ReportRequest { Dataset = "ginsu", StartDate = "2024-01-01", EndDate = "2026-01-01" }, default);
        (await tooLong.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("date_window_exceeded");

        fake.BatchRowCount = ReportToolService.MaxGinsuRawRows + 1;
        var tooMany = () => service.GetReportAsync(
            ConnectionId, new ReportRequest { Dataset = "ginsu", StartDate = "2026-01-11", EndDate = "2026-01-17" }, default);
        (await tooMany.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("too_many_rows");
    }

    // ---- service: exports with Projector's own paging ----

    [Fact]
    public async Task Projects_ShowManagerNames_AndContinueAfterTheLastProjectCode()
    {
        var (service, fake) = CreateService();

        var first = await CallAsync(service, new ReportRequest
        {
            Dataset = "projects", MaxRows = 2, Columns = ["project_code", "project_manager", "engagement_manager", "end_date", "open_for_time"]
        });
        first.GetProperty("rows")[0].GetRawText().Should().Be("""["P000101-001","Jane Doe","10001","2026-12-31",true]""",
            "10002 is in the resource list, 10001 is not and stays an id");
        first.GetProperty("rows")[1][3].ValueKind.Should().Be(JsonValueKind.Null);
        first.GetProperty("total").GetInt32().Should().Be(314);
        first.GetProperty("has_more").GetBoolean().Should().BeTrue("the page came back full");
        fake.LastOpenForTimeOnly.Should().BeTrue("closed projects are left out unless asked for");

        await CallAsync(service, new ReportRequest { Cursor = first.GetProperty("next_cursor").GetString() });
        fake.LastProjectCodesAfter.Should().Be("P000102-001");
        fake.ProjectCountCalls.Should().Be(1, "the total travels in the cursor");
    }

    [Fact]
    public async Task TimeCards_AreApprovedOnly_InHours_AndContinueAfterTheLastApproval()
    {
        var (service, fake) = CreateService();
        fake.ResourceNamesError = new ProjectorApiException("No.", "AccessPermissionDenied");

        var first = await CallAsync(service, new ReportRequest
        {
            Dataset = "time_cards", StartDate = "2026-01-12", EndDate = "2026-01-18", MaxRows = 2
        });
        first.GetProperty("columns").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("work_date", "person", "project_code", "task", "role", "hours", "narrative");
        first.GetProperty("rows")[0].GetRawText().Should().Be(
            """["2026-01-12","10002","P000101-001","Workshops","Consultant",1.5,"Workshop preparation"]""");
        first.GetProperty("rows")[1][5].GetDouble().Should().Be(0.33);
        first.GetProperty("note").GetString().Should().Contain("Approved time cards only").And.Contain("resource id");

        await CallAsync(service, new ReportRequest { Cursor = first.GetProperty("next_cursor").GetString() });
        fake.LastApprovedMinTimestamp.Should().Be("2026-01-14T09:10:00-05:00");
        fake.LastApprovedIdsAfter.Should().Be("900002");
    }

    [Fact]
    public async Task TimeCards_OfAPersonWithoutAnEmployeeId_HaveNoPerson_AndTheAnswerSaysWhy()
    {
        var (service, fake) = CreateService();
        fake.TimeCardsFixture = "export_time_cards_no_id.xml";

        var answer = await CallAsync(service, new ReportRequest
        {
            Dataset = "time_cards", StartDate = "2026-01-12", EndDate = "2026-01-18", Columns = ["person", "role", "hours"]
        });

        answer.GetProperty("rows").GetRawText().Should().Be("""[["Jane Doe","Consultant",1.5],[null,"Alex Poe",2]]""");
        answer.GetProperty("note").GetString().Should().Contain("1 of these cards have no person").And.Contain("ginsu");
    }

    [Fact]
    public async Task APartialAnswer_SaysWhichArgumentTakesTheCursor()
    {
        var (service, _) = CreateService();

        var report = await CallAsync(service, new ReportRequest { Dataset = "report", Code = "MY_CODE", MaxRows = 1 });
        report.GetProperty("note").GetString().Should().Contain("argument cursor set to next_cursor");

        var projects = await CallAsync(service, new ReportRequest { Dataset = "projects", MaxRows = 2 });
        projects.GetProperty("note").GetString().Should().Contain("argument cursor set to next_cursor");
    }

    [Fact]
    public async Task WithoutADataset_TheCatalogAndTheRecentRunsComeBack_AndAnUnknownDatasetIsRefused()
    {
        var (service, _) = CreateService();

        var catalog = await CallAsync(service, new ReportRequest());
        catalog.GetProperty("datasets").EnumerateArray().Select(d => d.GetProperty("dataset").GetString())
            .Should().Equal(ReportDatasets.Names);
        catalog.GetProperty("recent_report_runs")[0].GetProperty("name").GetString().Should().Be("Weekly time cards");

        var unknown = () => service.GetReportAsync(ConnectionId, new ReportRequest { Dataset = "bookings" }, default);
        (await unknown.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("time_cards");
    }

    // ---- who sees the tool ----

    [Theory]
    [InlineData("", "oid-1", false)]
    [InlineData("oid-1; oid-2", "oid-2", true)]
    [InlineData("oid-1,oid-2", "oid-3", false)]
    [InlineData("*", "oid-3", true)]
    [InlineData("OID-1", "oid-1", true)]
    public void AccessSetting_DecidesForASignedInUser(string setting, string objectId, bool allowed)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", objectId)], "test"));
        ReportAccessFilter.IsAllowed(setting, user).Should().Be(allowed);
    }

    [Fact]
    public void AccessSetting_DoesNotApplyWithoutASignedInUser()
    {
        // Local stdio and the CLI have no HTTP user: the tool is always there.
        ReportAccessFilter.IsAllowed("", null).Should().BeTrue();
        ReportAccessFilter.IsAllowed("", new ClaimsPrincipal(new ClaimsIdentity())).Should().BeTrue();
    }

    // ---- dev command ----

    [Theory]
    [InlineData("GetReportStatus", false, true)]
    [InlineData("ExportProjectList", false, true)]
    [InlineData("SubmitReportSpec", false, false)]
    [InlineData("SubmitReportSpec", true, true)]
    [InlineData("SubmitOlapGinsuExport", true, true)]
    [InlineData("OpsUpdateProject", true, false)]
    [InlineData("OpsImportTimeCards", false, false)]
    [InlineData("getreportstatus", false, false)]
    [InlineData("", true, false)]
    public void AsmxCommand_SendsOnlyListedMethods_AndRunsOnlyWhenAsked(string method, bool allowRun, bool sent) =>
        (PwsCliRunner.AsmxRefusal(method, allowRun) is null).Should().Be(sent);

    // ---- helpers ----

    private static async Task<JsonElement> CallAsync(ReportToolService service, ReportRequest request)
    {
        var answer = await service.GetReportAsync(ConnectionId, request, CancellationToken.None);
        return JsonSerializer.SerializeToElement(answer);
    }

    private static (ReportToolService Service, FakeReportClient Fake) CreateService()
    {
        var store = new InMemoryProjectorConnectionStore();
        store.Save(Connection());
        var connections = new ProjectorConnectionService(store, new NoRefreshTokenClient());
        var fake = new FakeReportClient();
        return (new ReportToolService(connections, fake, new ReportCache(), NullLoggerFactory.Instance), fake);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> GinsuRows() =>
        ProjectorReportParsers.ParseRows(Fixture("olap_ginsu_page.xml"), "OlapGinsuRecord");

    private static ProjectorConnection Connection(string id = ConnectionId, string? tenant = null, string? oid = null) => new()
    {
        ConnectionId = id,
        TenantId = tenant,
        EntraObjectId = oid,
        SessionTicket = "ticket",
        RefreshToken = "refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        GrantedScope = "allowFullPermissions",
        SoapServiceAuthority = "https://example.invalid",
        RestServiceAuthority = "https://example.invalid"
    };

    private static XDocument Fixture(string name) => XDocument.Load(Path.Combine(FixturesDir, name));

    private static string ResolveFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "fixtures"),
                         Path.Combine(dir.FullName, "tests", "Projector.UnitTests", "fixtures")
                     })
            {
                if (File.Exists(Path.Combine(candidate, "olap_ginsu_page.xml")))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("tests/Projector.UnitTests/fixtures");
    }

    /// <summary>Answers from the fixtures; counts the calls and remembers the paging values it was asked with.</summary>
    private sealed class FakeReportClient : IProjectorReportClient
    {
        public int OutputCalls { get; private set; }
        public string? LastOutputUid { get; private set; }
        public ProjectorApiException? OutputError { get; set; }
        public int SubmitReportCalls { get; private set; }
        public int SubmitGinsuCalls { get; private set; }

        /// <summary>The status every poll returns while it is set; null = the run is completed.</summary>
        public string? RunStatus { get; set; }

        /// <summary>As <see cref="RunStatus"/> for the export batch; "Deleted" is answered once, the rerun then completes.</summary>
        public string? BatchStatus { get; set; }
        public int? BatchRowCount { get; set; }
        public int ProjectCountCalls { get; private set; }
        public bool? LastOpenForTimeOnly { get; private set; }
        public string? LastProjectCodesAfter { get; private set; }
        public string? LastApprovedMinTimestamp { get; private set; }
        public string? LastApprovedIdsAfter { get; private set; }
        public ProjectorApiException? ResourceNamesError { get; set; }
        public string TimeCardsFixture { get; set; } = "export_time_cards.xml";

        public Task<ReportTable> GetReportOutputAsync(
            ProjectorConnection connection, string? webServiceCode, string? outputUid, CancellationToken cancellationToken = default)
        {
            if (OutputError is not null)
            {
                throw OutputError;
            }

            OutputCalls++;
            LastOutputUid = outputUid;
            return Task.FromResult(ProjectorReportParsers.ParseReportOutput(Fixture("report_output_csv.xml")));
        }

        public Task<string> SubmitReportSpecAsync(
            ProjectorConnection connection, string specUid, CancellationToken cancellationToken = default)
        {
            SubmitReportCalls++;
            return Task.FromResult(ProjectorReportParsers.SubmittedOutputUid(Fixture("submit_report_spec.xml"))!);
        }

        public Task<IReadOnlyList<ReportRun>> GetReportStatusAsync(
            ProjectorConnection connection, string? outputUid, CancellationToken cancellationToken = default)
        {
            if (outputUid is null)
            {
                return Task.FromResult(ProjectorReportParsers.ParseReportRuns(Fixture("report_status_list.xml")));
            }

            var run = ProjectorReportParsers.ParseReportRuns(Fixture("report_status_one.xml")).Single();
            if (RunStatus is not null)
            {
                run = run with { Status = RunStatus, Completed = null };
            }

            return Task.FromResult<IReadOnlyList<ReportRun>>([run]);
        }

        public Task<string> SubmitGinsuExportAsync(
            ProjectorConnection connection, GinsuExportRequest request, CancellationToken cancellationToken = default)
        {
            SubmitGinsuCalls++;
            return Task.FromResult("7000" + SubmitGinsuCalls);
        }

        public Task<BatchPage> GetGinsuRecordsAsync(
            ProjectorConnection connection, string requestId, long startAfterRowIndex, int maxRows, bool onlyCount,
            CancellationToken cancellationToken = default)
        {
            var page = ProjectorReportParsers.ParseBatchPage(Fixture("olap_ginsu_page.xml"), "OlapGinsuRecord");
            if (!onlyCount)
            {
                return Task.FromResult(page);
            }

            if (BatchStatus is { } status)
            {
                if (status == "Deleted")
                {
                    BatchStatus = null;
                }

                return Task.FromResult(new BatchPage(status, -1, []));
            }

            return Task.FromResult(new BatchPage("Completed", BatchRowCount ?? page.RowCount, []));
        }

        public Task<ExportPage> ExportProjectListAsync(
            ProjectorConnection connection, bool openForTimeOnly, string? projectCodesAfter, int maxRows, bool onlyCount,
            CancellationToken cancellationToken = default)
        {
            LastOpenForTimeOnly = openForTimeOnly;
            if (onlyCount)
            {
                ProjectCountCalls++;
                return Task.FromResult(new ExportPage(314, []));
            }

            LastProjectCodesAfter = projectCodesAfter;
            var doc = Fixture("export_project_list.xml");
            return Task.FromResult(new ExportPage(ProjectorReportParsers.RowCount(doc), ProjectorReportParsers.ParseRows(doc, "Project")));
        }

        public Task<ExportPage> ExportTimeCardsAsync(
            ProjectorConnection connection, string minWorkDate, string maxWorkDate, string? approvedMinTimestamp,
            string? approvedIdsAfter, int maxRows, bool onlyCount, CancellationToken cancellationToken = default)
        {
            if (onlyCount)
            {
                return Task.FromResult(new ExportPage(2018, []));
            }

            LastApprovedMinTimestamp = approvedMinTimestamp;
            LastApprovedIdsAfter = approvedIdsAfter;
            var doc = Fixture(TimeCardsFixture);
            return Task.FromResult(new ExportPage(ProjectorReportParsers.RowCount(doc), ProjectorReportParsers.ParseRows(doc, "TimeCard")));
        }

        public Task<IReadOnlyDictionary<string, string>> ExportResourceNamesAsync(
            ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            ResourceNamesError is not null
                ? throw ResourceNamesError
                : Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["10002"] = "Jane Doe" });
    }

    private sealed class NoRefreshTokenClient : IProjectorTokenClient
    {
        public Task<ProjectorTokenResponse> ExchangeAuthorizationCodeAsync(
            string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectorTokenResponse> RefreshAsync(
            ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAsync(ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
