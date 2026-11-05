using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Projector.ApiClient.Xml;
using Projector.Application.Resources;
using Projector.Contracts.Resources;
using Projector.Domain.Holidays;

namespace Projector.UnitTests;

/// <summary>list_resources by cost center or department (Projector's resource export) and list_groups.</summary>
public class ResourceExportFilterTests
{
    private static ExportedResourceRow Row(
        string id, string name, string? costCenter, string? department, bool inactive = false) => new()
    {
        ResourceReferenceSystemId = id,
        DisplayName = name,
        EmailAddress = $"{name.Replace(' ', '.').ToLowerInvariant()}@example.com",
        CostCenterName = costCenter,
        DepartmentName = department,
        TitleName = "Consultant",
        Inactive = inactive
    };

    private static readonly IReadOnlyList<ExportedResourceRow> People =
    [
        Row("1", "Alice Example", "Professional Services", "Delivery"),
        Row("2", "Bob Example", "Professional Services", "Development"),
        Row("3", "Carol Example", "Sales", "Sales"),
        Row("4", "Dan Example", "Human Resources", "HR"),
        Row("5", "Eve Example", "Professional Services", "Delivery", inactive: true),
        Row("6", "Frank Example", null, null),
        Row("7", "Grace Example", "Professional Services EU", "Delivery")
    ];

    [Fact]
    public void CostCenter_MatchesContainedText_IgnoresCase_LeavesInactiveOut()
    {
        var result = ResourceExportFilter.Apply(People, null, "professional services", null, includeInactive: false);

        result.People.Select(p => p.DisplayName).Should().Equal("Alice Example", "Bob Example", "Grace Example");
        result.MatchedCostCenters.Should().Equal("Professional Services", "Professional Services EU");
    }

    [Fact]
    public void CostCenterAndDepartment_BothMustMatch()
    {
        var result = ResourceExportFilter.Apply(People, null, "Professional", "Delivery", includeInactive: true);

        result.People.Select(p => p.DisplayName).Should().Equal("Alice Example", "Eve Example", "Grace Example");
    }

    [Fact]
    public void Groups_CountEveryoneVisible_BeforeTheFilter()
    {
        var result = ResourceExportFilter.Apply(People, null, "Sales", null, includeInactive: false);

        result.People.Should().ContainSingle(p => p.DisplayName == "Carol Example");
        result.Groups.CostCenters.Should().ContainEquivalentOf(new ResourceGroupCountDto("Professional Services", 2));
        result.Groups.CostCenters.Should().ContainEquivalentOf(new ResourceGroupCountDto("(none)", 1));
        result.Groups.CostCenters.Sum(g => g.People).Should().Be(6, "the inactive person is not counted");
        result.Groups.CostCenters[0].Name.Should().Be("Professional Services", "the largest group comes first");
    }

    [Fact]
    public void Query_NarrowsByNameEmailOrId()
    {
        ResourceExportFilter.Apply(People, "bob.example@", "Professional", null, includeInactive: false)
            .People.Should().ContainSingle(p => p.DisplayName == "Bob Example");
        ResourceExportFilter.Apply(People, "3", null, "Sales", includeInactive: false)
            .People.Should().ContainSingle(p => p.DisplayName == "Carol Example");
    }

    [Fact]
    public void Note_NobodyMatched_TellsTheAgentToAsk()
    {
        var result = ResourceExportFilter.Apply(People, null, "Marketing", null, includeInactive: false);

        result.People.Should().BeEmpty();
        ResourceExportFilter.Note(result, "Marketing", null, truncated: false)
            .Should().Contain("Nobody matched").And.Contain("ask the user");
    }

    [Fact]
    public void Note_SeveralCostCentersMatched_NamesThem()
    {
        var result = ResourceExportFilter.Apply(People, null, "Professional", null, includeInactive: false);

        ResourceExportFilter.Note(result, "Professional", null, truncated: false)
            .Should().Contain("matched 2 cost centers").And.Contain("Professional Services EU");
    }

    [Fact]
    public void Note_OneGroupMatched_NoNote()
    {
        var result = ResourceExportFilter.Apply(People, null, "Sales", null, includeInactive: false);

        ResourceExportFilter.Note(result, "Sales", null, truncated: false).Should().BeNull();
    }

    [Fact]
    public void Validator_AllowsUpTo500Rows_OnlyWithAFilter()
    {
        var validator = new ListResourcesValidator();

        validator.Validate(new ListResourcesRequest(MaxRows: 300)).IsValid.Should().BeFalse();
        validator.Validate(new ListResourcesRequest(MaxRows: 300, CostCenter: "Sales")).IsValid.Should().BeTrue();
        validator.Validate(new ListResourcesRequest(MaxRows: 501, Department: "HR")).IsValid.Should().BeFalse();
        validator.Validate(new ListResourcesRequest(ListGroups: true)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void SummaryRow_WithoutExportFields_KeepsTheOldShape()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var plain = JsonSerializer.Serialize(new ResourceSummaryDto("u", "1", "Alice Example", "a@example.com", false), options);
        var exported = JsonSerializer.Serialize(
            new ResourceSummaryDto(null, "1", "Alice Example", "a@example.com", false, "Sales", "Sales", "Consultant"), options);

        plain.Should().NotContain("cost_center").And.NotContain("department").And.NotContain("title");
        exported.Should().Contain("\"cost_center\":\"Sales\"").And.Contain("\"title\":\"Consultant\"");
    }

    [Fact]
    public void Parser_ReadsEmailCostCenterDepartmentAndTitle()
    {
        var doc = XDocument.Parse("""
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:data="http://www.opsplanning.com/webservices/public/data">
              <soapenv:Body><data:ExportResourcesResponse><data:ExportResourcesResult><data:Data><data:Resources>
                <data:Resource>
                  <data:ResourceId>1</data:ResourceId>
                  <data:DisplayName>Alice Example</data:DisplayName>
                  <data:EmployeeId>10101</data:EmployeeId>
                  <data:EmailAddress>alice@example.com</data:EmailAddress>
                  <data:CostCenterName>Professional Services</data:CostCenterName>
                  <data:DepartmentName>Delivery</data:DepartmentName>
                  <data:TitleName>Consultant</data:TitleName>
                  <data:ResourceInactiveFlag>false</data:ResourceInactiveFlag>
                </data:Resource>
              </data:Resources></data:Data></data:ExportResourcesResult></data:ExportResourcesResponse></soapenv:Body>
            </soapenv:Envelope>
            """);

        var row = ProjectorResponseParsers.ParseExportedResourcesAsmx(doc).Single();

        row.EmailAddress.Should().Be("alice@example.com");
        row.CostCenterName.Should().Be("Professional Services");
        row.DepartmentName.Should().Be("Delivery");
        row.TitleName.Should().Be("Consultant");
    }
}
