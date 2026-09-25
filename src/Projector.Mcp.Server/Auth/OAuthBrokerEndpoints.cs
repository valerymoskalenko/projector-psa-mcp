using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Projector.ApiClient;
using Projector.Application.Auth;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.Mcp.Server.Auth;

public static class OAuthBrokerEndpoints
{
    public static IEndpointRouteBuilder MapOAuthBroker(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", (IOptions<ProjectorOptions> options) =>
        {
            var opts = options.Value;
            var baseUrl = opts.PublicBaseUrl.TrimEnd('/');
            return Results.Json(new
            {
                resource = opts.McpAudience.Contains("://", StringComparison.Ordinal)
                    ? opts.McpAudience
                    : $"{baseUrl}/mcp",
                authorization_servers = new[] { baseUrl },
                scopes_supported = new[] { opts.McpScope, "mcp:tools" },
                bearer_methods_supported = new[] { "header" },
                resource_name = "Projector PSA MCP Server"
            });
        });

        app.MapGet("/.well-known/oauth-authorization-server", (IOptions<ProjectorOptions> options) =>
        {
            var baseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
            return Results.Json(new
            {
                issuer = baseUrl,
                authorization_endpoint = $"{baseUrl}/oauth/authorize",
                token_endpoint = $"{baseUrl}/oauth/token",
                revocation_endpoint = $"{baseUrl}/oauth/revoke",
                registration_endpoint = $"{baseUrl}/oauth/register",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post", "none" },
                code_challenge_methods_supported = new[] { "S256" },
                scopes_supported = new[] { options.Value.McpScope, "mcp:tools" }
            });
        });

        app.MapGet("/oauth/authorize", (
            HttpRequest request,
            IOptions<ProjectorOptions> options,
            IPendingAuthorizationStore pendingStore) =>
        {
            var opts = options.Value;
            var clientRedirect = request.Query["redirect_uri"].ToString();
            var clientState = request.Query["state"].ToString();
            var clientChallenge = request.Query["code_challenge"].ToString();
            var challengeMethod = request.Query["code_challenge_method"].ToString();
            var responseType = request.Query["response_type"].ToString();
            var clientId = request.Query["client_id"].ToString();

            if (!string.Equals(responseType, "code", StringComparison.Ordinal))
            {
                return Results.BadRequest("response_type must be code");
            }

            if (string.IsNullOrWhiteSpace(clientRedirect))
            {
                return Results.BadRequest("redirect_uri required");
            }

            if (!IsAllowedRedirect(opts, clientRedirect))
            {
                return Results.BadRequest("redirect_uri is not allowlisted for this MCP server.");
            }

            if (!string.IsNullOrEmpty(challengeMethod)
                && !string.Equals(challengeMethod, "S256", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("code_challenge_method must be S256");
            }

            if (string.IsNullOrWhiteSpace(opts.EntraClientId) || string.IsNullOrWhiteSpace(opts.EntraTenantId))
            {
                // Local/dev without Entra: skip hop and go straight to Projector (CLI Inspector path).
                return StartProjectorAuthorize(
                    opts,
                    pendingStore,
                    clientRedirect,
                    clientState,
                    clientChallenge,
                    clientId,
                    tenantId: null,
                    entraObjectId: null);
            }

            var flowId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var connectionId = Guid.NewGuid().ToString("N");
            var entraNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pending = new PendingAuthorization
            {
                Code = $"flow:{flowId}",
                ConnectionId = connectionId,
                ClientRedirectUri = clientRedirect,
                ClientState = string.IsNullOrEmpty(clientState) ? null : clientState,
                ClientCodeChallenge = string.IsNullOrEmpty(clientChallenge) ? null : clientChallenge,
                ClientId = string.IsNullOrEmpty(clientId) ? null : clientId,
                EntraNonce = entraNonce,
                ProjectorAccountCode = opts.AccountCode,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                Kind = PendingKind.CopilotAuthorize
            };
            pendingStore.SaveFlow(flowId, pending);

            var entraRedirect = $"{opts.PublicBaseUrl.TrimEnd('/')}/oauth/entra/callback";
            var entraAuthorize =
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(opts.EntraTenantId)}/oauth2/v2.0/authorize" +
                $"?client_id={Uri.EscapeDataString(opts.EntraClientId)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(entraRedirect)}" +
                $"&response_mode=query" +
                $"&scope={Uri.EscapeDataString("openid profile")}" +
                $"&state={Uri.EscapeDataString(flowId)}" +
                $"&nonce={Uri.EscapeDataString(entraNonce)}";

            return Results.Redirect(entraAuthorize);
        });

        app.MapGet("/oauth/entra/callback", async (
            HttpRequest request,
            IOptions<ProjectorOptions> options,
            IPendingAuthorizationStore pendingStore,
            IHttpClientFactory httpClientFactory) =>
        {
            var opts = options.Value;
            var code = request.Query["code"].ToString();
            var flowId = request.Query["state"].ToString();
            var error = request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                return Results.BadRequest($"Entra authorization error: {error}");
            }

            var pending = pendingStore.GetFlow(flowId);
            if (pending is null || pending.IsExpired)
            {
                return Results.BadRequest("Invalid or expired Entra OAuth state.");
            }

            try
            {
                var (tid, oid) = await ExchangeEntraCodeAsync(opts, code, httpClientFactory, request.HttpContext.RequestAborted);
                if (string.IsNullOrWhiteSpace(tid) || string.IsNullOrWhiteSpace(oid))
                {
                    return Results.BadRequest("Entra token did not include tid/oid.");
                }

                if (!string.IsNullOrWhiteSpace(opts.EntraTenantId)
                    && !string.Equals(tid, opts.EntraTenantId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest("Entra tenant is not allowed for this MCP server.");
                }

                pending.TenantId = tid;
                pending.EntraObjectId = oid;
                pending.ProjectorAccountCode = opts.AccountCode;
                pendingStore.UpdateFlow(flowId, pending);

                return StartProjectorAuthorize(
                    opts,
                    pendingStore,
                    pending.ClientRedirectUri,
                    pending.ClientState,
                    pending.ClientCodeChallenge,
                    pending.ClientId,
                    tid,
                    oid,
                    pending.ConnectionId,
                    flowId);
            }
            catch (Exception ex)
            {
                return Results.BadRequest($"Entra token exchange failed: {ex.Message}");
            }
        });

        app.MapGet("/oauth/projector/callback", async (
            HttpRequest request,
            IOptions<ProjectorOptions> options,
            IPendingAuthorizationStore pendingStore,
            IProjectorTokenClient tokenClient,
            ProjectorConnectionService connections,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Projector.Mcp.OAuth.ProjectorCallback");
            var code = request.Query["code"].ToString();
            var state = request.Query["state"].ToString();
            var error = request.Query["error"].ToString();
            var corr = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state ?? "")))[..8];
            var stage = "start";

            if (!string.IsNullOrEmpty(error))
            {
                logger.LogWarning(
                    "Projector callback rejected: projector_authorize_error. corr={Corr} error={Error}",
                    corr,
                    error);
                return Results.BadRequest($"Projector authorization error: {error}");
            }

            try
            {
                stage = "pending_lookup";
                var pending = pendingStore.TakeByState(state);
                if (pending is null || pending.IsExpired || pending.ProjectorCodeVerifier is null)
                {
                    logger.LogWarning(
                        "Projector callback rejected: pending_state_invalid. corr={Corr} stage={Stage}",
                        corr,
                        stage);
                    return Results.BadRequest("Invalid or expired OAuth state. Start Connect again (do not refresh this page).");
                }

                stage = "projector_token_exchange";
                var token = await tokenClient.ExchangeAuthorizationCodeAsync(
                    code,
                    options.Value.RedirectUri,
                    pending.ProjectorCodeVerifier,
                    request.HttpContext.RequestAborted);

                var account = pending.ProjectorAccountCode ?? options.Value.AccountCode;
                var entraConfigured = !string.IsNullOrWhiteSpace(options.Value.EntraClientId);
                if (entraConfigured
                    && (string.IsNullOrWhiteSpace(pending.TenantId)
                        || string.IsNullOrWhiteSpace(pending.EntraObjectId)))
                {
                    logger.LogWarning(
                        "Projector callback rejected: owner_identity_missing. corr={Corr} stage={Stage} hasTid={HasTid} hasOid={HasOid}",
                        corr,
                        stage,
                        !string.IsNullOrWhiteSpace(pending.TenantId),
                        !string.IsNullOrWhiteSpace(pending.EntraObjectId));
                    return Results.BadRequest("Microsoft Entra identity missing from the auth flow. Start Connect again.");
                }

                stage = "connection_persist";
                var connection = connections.UpsertFromToken(
                    pending.ConnectionId,
                    token,
                    pending.TenantId,
                    pending.EntraObjectId,
                    account);

                stage = "mcp_code_issue";
                var mcpCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                pendingStore.Save(new PendingAuthorization
                {
                    Code = mcpCode,
                    ConnectionId = connection.ConnectionId,
                    ClientRedirectUri = pending.ClientRedirectUri,
                    ClientState = pending.ClientState,
                    ClientCodeChallenge = pending.ClientCodeChallenge,
                    ClientId = pending.ClientId,
                    TenantId = pending.TenantId,
                    EntraObjectId = pending.EntraObjectId,
                    ProjectorAccountCode = connection.ProjectorAccountCode,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                    Kind = PendingKind.McpCode
                });

                stage = "redirect_to_client";
                var separator = pending.ClientRedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
                var location =
                    $"{pending.ClientRedirectUri}{separator}code={Uri.EscapeDataString(mcpCode)}" +
                    (pending.ClientState is null ? string.Empty : $"&state={Uri.EscapeDataString(pending.ClientState)}");

                logger.LogInformation(
                    "Projector callback succeeded. corr={Corr} connectionId={ConnectionId} hasTid={HasTid} stage={Stage}",
                    corr,
                    connection.ConnectionId,
                    !string.IsNullOrEmpty(pending.TenantId),
                    stage);

                return Results.Redirect(location);
            }
            catch (ProjectorApiException ex)
            {
                logger.LogWarning(
                    "Projector callback rejected: projector_token_exchange_failed. corr={Corr} stage={Stage} detail={Detail}",
                    corr,
                    stage,
                    ex.Message);
                return Results.Content(
                    $"Projector token exchange failed ({stage}). Start Connect again (do not refresh). Details: {ex.Message}",
                    "text/plain",
                    statusCode: 400);
            }
            catch (Exception ex)
            {
                // Prefer 400 with stage over opaque 500 so Copilot/browser show actionable text.
                logger.LogError(
                    ex,
                    "Projector callback rejected: {FailureCategory}. corr={Corr} stage={Stage}",
                    ClassifyPersistFailure(ex),
                    corr,
                    stage);
                return Results.Content(
                    $"Could not complete Projector connection at stage '{stage}'. Start Connect again (do not refresh this page).",
                    "text/plain",
                    statusCode: 400);
            }
        });

        static string ClassifyPersistFailure(Exception ex)
        {
            var msg = ex.ToString();
            if (msg.Contains("UX_ProjectorConnections_Owner", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            {
                return "connection_owner_conflict";
            }

            if (msg.Contains("SqlException", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("DbUpdate", StringComparison.OrdinalIgnoreCase))
            {
                return "connection_persist_sql_failed";
            }

            return "connection_persist_failed";
        }

        app.MapPost("/oauth/token", async (
            HttpRequest request,
            IOptions<ProjectorOptions> options,
            IPendingAuthorizationStore pendingStore,
            IProjectorConnectionStore connectionStore,
            McpJwtIssuer jwtIssuer,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Projector.Mcp.OAuth.Token");
            var form = await request.ReadFormAsync();
            var grantType = form["grant_type"].ToString();

            if (string.Equals(grantType, "authorization_code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(grantType, "code", StringComparison.OrdinalIgnoreCase))
            {
                var code = form["code"].ToString();
                var codeVerifier = form["code_verifier"].ToString();
                var redirectUri = form["redirect_uri"].ToString();
                var clientId = form["client_id"].ToString();
                if (string.IsNullOrWhiteSpace(clientId)
                    && AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var authHeader)
                    && string.Equals(authHeader.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(authHeader.Parameter))
                {
                    try
                    {
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Parameter));
                        var sep = decoded.IndexOf(':');
                        if (sep > 0)
                        {
                            clientId = decoded[..sep];
                        }
                    }
                    catch
                    {
                        // ignore malformed Basic auth; PKCE path may still succeed
                    }
                }

                var pending = pendingStore.Peek(code);
                if (pending is null || pending.IsExpired || pending.Kind != PendingKind.McpCode)
                {
                    logger.LogWarning(
                        "Token exchange rejected: invalid_grant (code_missing_or_expired). code_len={CodeLen} client_id={ClientId}",
                        code?.Length ?? 0,
                        string.IsNullOrEmpty(clientId) ? "(none)" : clientId);
                    return Results.Json(new { error = "invalid_grant", error_description = "code_missing_or_expired" }, statusCode: 400);
                }

                if (!string.IsNullOrEmpty(redirectUri)
                    && !string.Equals(redirectUri, pending.ClientRedirectUri, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Token exchange rejected: redirect_uri_mismatch. expected={Expected} actual={Actual}",
                        pending.ClientRedirectUri,
                        redirectUri);
                    return Results.Json(new { error = "invalid_grant", error_description = "redirect_uri_mismatch" }, statusCode: 400);
                }

                if (!string.IsNullOrEmpty(pending.ClientId)
                    && !string.IsNullOrEmpty(clientId)
                    && !string.Equals(pending.ClientId, clientId, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Token exchange rejected: client_id_mismatch. expected={Expected} actual={Actual}",
                        pending.ClientId,
                        clientId);
                    return Results.Json(new { error = "invalid_grant", error_description = "client_id_mismatch" }, statusCode: 400);
                }

                if (!string.IsNullOrEmpty(pending.ClientCodeChallenge))
                {
                    if (string.IsNullOrEmpty(codeVerifier)
                        || !Pkce.VerifyS256(pending.ClientCodeChallenge, codeVerifier))
                    {
                        logger.LogWarning(
                            "Token exchange rejected: pkce_failed. challenge_len={ChallengeLen} verifier_len={VerifierLen}",
                            pending.ClientCodeChallenge.Length,
                            codeVerifier?.Length ?? 0);
                        return Results.Json(new { error = "invalid_grant", error_description = "pkce_failed" }, statusCode: 400);
                    }
                }

                var connection = connectionStore.Get(pending.ConnectionId);
                if (connection is null)
                {
                    logger.LogWarning(
                        "Token exchange rejected: connection_missing. connectionId={ConnectionId}",
                        pending.ConnectionId);
                    return Results.Json(new { error = "invalid_grant", error_description = "connection_missing" }, statusCode: 400);
                }

                // Consume only after all checks pass so Teams can retry a failed exchange.
                if (pendingStore.Take(code) is null)
                {
                    logger.LogWarning("Token exchange rejected: code_already_used.");
                    return Results.Json(new { error = "invalid_grant", error_description = "code_already_used" }, statusCode: 400);
                }

                var accessToken = jwtIssuer.CreateAccessToken(
                    pending.ConnectionId,
                    pending.TenantId ?? connection.TenantId,
                    pending.EntraObjectId ?? connection.EntraObjectId,
                    pending.ProjectorAccountCode ?? connection.ProjectorAccountCode ?? options.Value.AccountCode);
                var refreshToken = jwtIssuer.CreateRefreshToken(
                    pending.ConnectionId,
                    pending.TenantId ?? connection.TenantId,
                    pending.EntraObjectId ?? connection.EntraObjectId,
                    pending.ProjectorAccountCode ?? connection.ProjectorAccountCode ?? options.Value.AccountCode);

                logger.LogInformation(
                    "Token exchange succeeded for connection {ConnectionId} (tid={TenantId}).",
                    pending.ConnectionId,
                    pending.TenantId ?? connection.TenantId);

                return Results.Json(new
                {
                    access_token = accessToken,
                    refresh_token = refreshToken,
                    token_type = "Bearer",
                    expires_in = 3600,
                    scope = options.Value.McpScope
                });
            }

            if (string.Equals(grantType, "refresh_token", StringComparison.OrdinalIgnoreCase))
            {
                var refresh = form["refresh_token"].ToString();
                var principal = jwtIssuer.ValidateRefreshToken(refresh);
                if (principal is null)
                {
                    logger.LogWarning("Token exchange rejected: invalid_refresh_token.");
                    return Results.Json(new { error = "invalid_grant", error_description = "invalid_refresh_token" }, statusCode: 400);
                }

                var connectionId = principal.FindFirst("connection_id")?.Value
                    ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst("sub")?.Value;
                if (string.IsNullOrWhiteSpace(connectionId))
                {
                    logger.LogWarning("Token exchange rejected: refresh_missing_connection.");
                    return Results.Json(new { error = "invalid_grant", error_description = "refresh_missing_connection" }, statusCode: 400);
                }

                var connection = connectionStore.Get(connectionId);
                if (connection is null)
                {
                    logger.LogWarning("Token exchange rejected: refresh_connection_missing.");
                    return Results.Json(new { error = "invalid_grant", error_description = "refresh_connection_missing" }, statusCode: 400);
                }

                var tid = principal.FindFirst("tid")?.Value ?? connection.TenantId;
                var oid = principal.FindFirst("oid")?.Value ?? connection.EntraObjectId;
                var account = principal.FindFirst("projector_account")?.Value
                    ?? connection.ProjectorAccountCode
                    ?? options.Value.AccountCode;

                var accessToken = jwtIssuer.CreateAccessToken(connectionId, tid, oid, account);
                var newRefresh = jwtIssuer.CreateRefreshToken(connectionId, tid, oid, account);
                logger.LogInformation("Token refresh succeeded for connection {ConnectionId}.", connectionId);
                return Results.Json(new
                {
                    access_token = accessToken,
                    refresh_token = newRefresh,
                    token_type = "Bearer",
                    expires_in = 3600,
                    scope = options.Value.McpScope
                });
            }

            return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
        });

        // Dynamic Client Registration (RFC 7591) for MCP clients such as Claude, VS Code and Cursor.
        // Stateless: client_id is not a credential here (the broker does not keep a client registry);
        // the redirect URI allowlist and PKCE protect the flow. Registration only fails for redirect
        // URIs the authorize endpoint would reject anyway.
        app.MapPost("/oauth/register", async (HttpRequest request, IOptions<ProjectorOptions> options) =>
        {
            JsonElement body;
            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body);
                body = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = "Body must be a JSON object." },
                    statusCode: 400);
            }

            var redirectUris = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("redirect_uris", out var uris)
                && uris.ValueKind == JsonValueKind.Array
                    ? uris.EnumerateArray()
                        .Where(u => u.ValueKind == JsonValueKind.String)
                        .Select(u => u.GetString()!)
                        .ToList()
                    : [];

            if (redirectUris.Count == 0)
            {
                return Results.Json(
                    new { error = "invalid_redirect_uri", error_description = "redirect_uris is required." },
                    statusCode: 400);
            }

            var rejected = redirectUris.FirstOrDefault(u => !IsAllowedRedirect(options.Value, u));
            if (rejected is not null)
            {
                return Results.Json(
                    new
                    {
                        error = "invalid_redirect_uri",
                        error_description = $"redirect_uri is not allowlisted for this MCP server: {rejected}"
                    },
                    statusCode: 400);
            }

            var clientName = body.TryGetProperty("client_name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

            return Results.Json(
                new
                {
                    client_id = "mcp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                    client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    client_name = clientName,
                    redirect_uris = redirectUris,
                    grant_types = new[] { "authorization_code", "refresh_token" },
                    response_types = new[] { "code" },
                    token_endpoint_auth_method = "none"
                },
                statusCode: 201);
        });

        app.MapPost("/oauth/revoke", async (
            HttpRequest request,
            IProjectorConnectionStore store,
            IProjectorTokenClient tokenClient,
            ILocalOAuthSessionStore localStore,
            IOptions<ProjectorOptions> options) =>
        {
            var form = await request.ReadFormAsync();
            var connectionId = form["connection_id"].ToString();
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return Results.BadRequest("connection_id required");
            }

            var connection = store.Get(connectionId);
            if (connection is null)
            {
                return Results.Ok();
            }

            try
            {
                await tokenClient.RevokeAsync(connection, request.HttpContext.RequestAborted);
            }
            catch (ProjectorApiException)
            {
                // Best-effort revoke.
            }

            store.Remove(connectionId);
            localStore.Clear(
                options.Value.AccountCode,
                ProjectorScopes.Normalize(options.Value.RequestedScopes));
            return Results.Ok();
        });

        var env = app.ServiceProvider.GetService(typeof(IHostEnvironment)) as IHostEnvironment;
        if (env?.IsDevelopment() == true)
        {
            MapDevEndpoints(app);
        }

        return app;
    }

    private static void MapDevEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/oauth/dev/login", (IOptions<ProjectorOptions> options) =>
        {
            var opts = options.Value;
            var redirect = $"{opts.PublicBaseUrl.TrimEnd('/')}/oauth/dev/callback";
            var verifier = Pkce.GenerateCodeVerifier();
            var challenge = Pkce.GetS256CodeChallenge(verifier);
            var url =
                $"/oauth/authorize?response_type=code&client_id=mcp-inspector" +
                $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                $"&state=dev&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";
            return Results.Redirect(url);
        });

        app.MapGet("/oauth/dev/callback", (
            HttpRequest request,
            IPendingAuthorizationStore pendingStore,
            McpJwtIssuer jwtIssuer) =>
        {
            var code = request.Query["code"].ToString();
            var pending = pendingStore.Take(code);
            if (pending is null)
            {
                return Results.Content("Missing code. Start at /oauth/dev/login", "text/plain");
            }

            var token = jwtIssuer.CreateAccessToken(
                pending.ConnectionId,
                pending.TenantId,
                pending.EntraObjectId,
                pending.ProjectorAccountCode);
            pendingStore.Save(new PendingAuthorization
            {
                Code = code,
                ConnectionId = pending.ConnectionId,
                ClientRedirectUri = pending.ClientRedirectUri,
                ClientState = pending.ClientState,
                ClientCodeChallenge = null,
                TenantId = pending.TenantId,
                EntraObjectId = pending.EntraObjectId,
                ProjectorAccountCode = pending.ProjectorAccountCode,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                Kind = PendingKind.McpCode
            });

            var html = $"""
                <!DOCTYPE html>
                <html><body style="font-family:Segoe UI,sans-serif;max-width:720px;margin:40px auto">
                <h1>MCP Bearer token ready</h1>
                <p>Connection id: <code>{pending.ConnectionId}</code></p>
                <p>Use this Bearer token against <code>/mcp</code>:</p>
                <textarea style="width:100%;height:120px">{token}</textarea>
                </body></html>
                """;
            return Results.Content(html, "text/html");
        });
    }

    private static IResult StartProjectorAuthorize(
        ProjectorOptions opts,
        IPendingAuthorizationStore pendingStore,
        string clientRedirect,
        string? clientState,
        string? clientChallenge,
        string? clientId,
        string? tenantId,
        string? entraObjectId,
        string? connectionId = null,
        string? flowId = null)
    {
        connectionId ??= Guid.NewGuid().ToString("N");
        var projectorVerifier = Pkce.GenerateCodeVerifier();
        var projectorChallenge = Pkce.GetS256CodeChallenge(projectorVerifier);
        var projectorState = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var projectorScope = ProjectorScopes.Normalize(opts.RequestedScopes);

        pendingStore.SaveByState(projectorState, new PendingProjectorLogin
        {
            ConnectionId = connectionId,
            ClientRedirectUri = clientRedirect,
            ClientState = string.IsNullOrEmpty(clientState) ? null : clientState,
            ClientCodeChallenge = string.IsNullOrEmpty(clientChallenge) ? null : clientChallenge,
            ClientId = clientId,
            ProjectorCodeVerifier = projectorVerifier,
            TenantId = tenantId,
            EntraObjectId = entraObjectId,
            ProjectorAccountCode = opts.AccountCode,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });

        if (!string.IsNullOrEmpty(flowId))
        {
            pendingStore.TakeFlow(flowId);
        }

        var authorizeBase =
            $"{opts.AuthorizeBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(opts.AccountCode)}";

        var url =
            $"{authorizeBase}" +
            $"?response_type=code" +
            $"&client_id={Uri.EscapeDataString(opts.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(opts.RedirectUri)}" +
            $"&state={Uri.EscapeDataString(projectorState)}" +
            $"&scope={Uri.EscapeDataString(projectorScope)}" +
            $"&code_challenge={Uri.EscapeDataString(projectorChallenge)}" +
            $"&code_challenge_method=S256";

        return Results.Redirect(url);
    }

    private static async Task<(string tid, string oid)> ExchangeEntraCodeAsync(
        ProjectorOptions opts,
        string code,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var redirect = $"{opts.PublicBaseUrl.TrimEnd('/')}/oauth/entra/callback";
        var client = httpClientFactory.CreateClient("EntraToken");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = opts.EntraClientId,
            ["client_secret"] = opts.EntraClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid profile"
        });

        var tokenUrl = $"https://login.microsoftonline.com/{opts.EntraTenantId}/oauth2/v2.0/token";
        using var response = await client.PostAsync(tokenUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Entra token endpoint returned {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenEl))
        {
            throw new InvalidOperationException("Entra response missing id_token");
        }

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(idTokenEl.GetString());
        var tid = jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value
            ?? throw new InvalidOperationException("id_token missing tid");
        var oid = jwt.Claims.FirstOrDefault(c => c.Type == "oid")?.Value
            ?? throw new InvalidOperationException("id_token missing oid");
        return (tid, oid);
    }

    private static bool IsAllowedRedirect(ProjectorOptions opts, string redirectUri)
    {
        foreach (var allowed in opts.GetAllowedMcpRedirectUris())
        {
            if (string.Equals(allowed.TrimEnd('/'), redirectUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }
}
