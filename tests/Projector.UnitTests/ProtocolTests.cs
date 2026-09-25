using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Projector.Domain.Exceptions;
using Projector.Mcp.Server;
using Projector.Mcp.Server.Cli;
using Projector.Mcp.Server.Tools;

namespace Projector.UnitTests;

public class ProtocolTests : IClassFixture<ProjectorWebApplicationFactory>
{
    private readonly ProjectorWebApplicationFactory _factory;

    public ProtocolTests(ProjectorWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void AgentTools_ToError_SetsIsErrorPayload()
    {
        var result = AgentTools.ToError(new ArgumentException("bad arg"));
        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        using var doc = JsonDocument.Parse(text!);
        doc.RootElement.GetProperty("error").GetString().Should().Be("invalid_argument");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("bad arg");
    }

    [Fact]
    public void AgentTools_ToError_ProjectorApiUsesErrorCode()
    {
        var result = AgentTools.ToError(new ProjectorApiException("missing", "AtLeastOneItemNotFound"));
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        using var doc = JsonDocument.Parse(text!);
        doc.RootElement.GetProperty("error").GetString().Should().Be("AtLeastOneItemNotFound");
    }

    [Fact]
    public void ToolCliRunner_ParseArgs_SupportsResourceId()
    {
        var map = ToolCliRunner.ParseArgs(["--resource-id", "10001", "--start-date", "2026-09-01"]);
        map["resource_id"].Should().Be("10001");
        map["start_date"].Should().Be("2026-09-01");
    }

    [Fact]
    public async Task Http_Mcp_WithoutAuth_Returns401WithWwwAuthenticate()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Http_Mcp_InvalidOrigin_Returns403()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid Origin");
    }

    [Fact]
    public async Task AuthorizationServerMetadata_AdvertisesRegistrationEndpoint()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));
        doc.RootElement.GetProperty("registration_endpoint").GetString().Should().EndWith("/oauth/register");
        doc.RootElement.GetProperty("code_challenge_methods_supported")[0].GetString().Should().Be("S256");
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("http://localhost:3118/callback")]
    [InlineData("http://127.0.0.1:33418")]
    public async Task Register_AllowlistedRedirect_Returns201WithClientId(string redirectUri)
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new { client_name = "Test", redirect_uris = new[] { redirectUri } });
        var response = await client.PostAsync("/oauth/register", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("client_id").GetString().Should().StartWith("mcp-");
        doc.RootElement.GetProperty("token_endpoint_auth_method").GetString().Should().Be("none");
        doc.RootElement.GetProperty("redirect_uris")[0].GetString().Should().Be(redirectUri);
    }

    [Fact]
    public async Task Register_UnknownRedirect_Returns400()
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new { redirect_uris = new[] { "https://evil.example/callback" } });
        var response = await client.PostAsync("/oauth/register", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Http_Initialize_AdvertisesTools_AndToolsListReturnsThem()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<Projector.Mcp.Server.Auth.McpJwtIssuer>()
            .CreateAccessToken("protocol-test-connection");

        var init = await PostMcpAsync(client, token,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"protocol-test","version":"1.0"}}}""");
        init.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _)
            .Should().BeTrue("clients such as VS Code only call tools/list when initialize advertises tools");

        var list = await PostMcpAsync(client, token, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        list.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(13);
    }

    private static async Task<JsonElement> PostMcpAsync(HttpClient client, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        // Streamable HTTP may answer as SSE ("data: {...}") or plain JSON.
        var json = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => l["data:".Length..].Trim()).LastOrDefault() ?? text;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>In-memory HTTP host for protocol checks (no live Projector / no Key Vault).</summary>
public sealed class ProjectorWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        builder.UseSetting("Projector:KeyVaultUri", "");
        builder.UseSetting("Projector:AccountCode", "Contoso");
        builder.UseSetting("Projector:ClientId", "protocol-test-client");
        builder.UseSetting("Projector:ClientSecret", "protocol-test-secret");
    }
}
