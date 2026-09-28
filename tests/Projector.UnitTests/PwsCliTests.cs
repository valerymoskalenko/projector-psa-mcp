using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Projector.Mcp.Server.Cli;

namespace Projector.UnitTests;

/// <summary>The dev-only pws command must never change Projector data and must not run outside Development.</summary>
public class PwsCliTests
{
    [Theory]
    [InlineData("PwsGetTimeCards", true)]
    [InlineData("PwsSearchProjects", true)]
    [InlineData("PwsSaveTimeCards", false)]
    [InlineData("PwsDeleteTimeCards", false)]
    [InlineData("PwsSubmitTimeCards", false)]
    [InlineData("PwsSetTimeCardApprovalWorkflowStatus", false)]
    [InlineData("pwsgetTimeCards", false)]
    [InlineData("PwsGet", false)]
    [InlineData("Pwsgetaway", false)]
    [InlineData("", false)]
    public void OnlyReadMethodsAreAllowed(string method, bool allowed) =>
        PwsCliRunner.IsReadMethod(method).Should().Be(allowed);

    [Fact]
    public async Task WriteMethod_IsRefusedBeforeAnyCall()
    {
        var body = WriteBody("<pws:PwsSaveTimeCards><pws:serviceRequest/></pws:PwsSaveTimeCards>");
        var runner = new PwsCliRunner(null!, null!, new Env("Development"));

        (await runner.RunAsync("PwsSaveTimeCards", body, CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task ReadMethodName_WithAWriteBody_IsRefused()
    {
        var body = WriteBody("<pws:PwsSaveTimeCards><pws:serviceRequest/></pws:PwsSaveTimeCards>");
        var runner = new PwsCliRunner(null!, null!, new Env("Development"));

        (await runner.RunAsync("PwsGetTimeCards", body, CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task OutsideDevelopment_NothingRuns()
    {
        var body = WriteBody("<pws:PwsGetTimeCards><pws:serviceRequest/></pws:PwsGetTimeCards>");
        var runner = new PwsCliRunner(null!, null!, new Env("Production"));

        (await runner.RunAsync("PwsGetTimeCards", body, CancellationToken.None)).Should().Be(2);
    }

    private static string WriteBody(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pws-test-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
