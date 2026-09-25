using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Projector.ApiClient;

namespace Projector.Mcp.Server.Auth;

public sealed class McpJwtIssuer
{
    private readonly ProjectorOptions _options;
    private readonly SymmetricSecurityKey _key;

    public McpJwtIssuer(IOptions<ProjectorOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.JwtSigningKey))
        {
            throw new InvalidOperationException(
                "Projector:JwtSigningKey is missing. In Production load ProjectorMcpJwtSigningKey from Key Vault.");
        }

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSigningKey));
    }

    public SymmetricSecurityKey SecurityKey => _key;

    public string Issuer => _options.PublicBaseUrl.TrimEnd('/');

    public string Audience => _options.McpAudience;

    public string CreateAccessToken(
        string connectionId,
        string? tenantId = null,
        string? entraObjectId = null,
        string? projectorAccount = null,
        TimeSpan? lifetime = null)
    {
        return CreateToken(
            connectionId,
            tokenUse: "access",
            tenantId,
            entraObjectId,
            projectorAccount,
            lifetime ?? TimeSpan.FromHours(1));
    }

    public string CreateRefreshToken(
        string connectionId,
        string? tenantId = null,
        string? entraObjectId = null,
        string? projectorAccount = null,
        TimeSpan? lifetime = null)
    {
        return CreateToken(
            connectionId,
            tokenUse: "refresh",
            tenantId,
            entraObjectId,
            projectorAccount,
            lifetime ?? TimeSpan.FromDays(30));
    }

    public ClaimsPrincipal? ValidateRefreshToken(string refreshToken)
    {
        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(refreshToken, CreateValidationParameters(), out var validated);
            if (validated is not JwtSecurityToken)
            {
                return null;
            }

            var use = principal.FindFirst("token_use")?.Value;
            if (!string.Equals(use, "refresh", StringComparison.Ordinal))
            {
                return null;
            }

            return principal;
        }
        catch
        {
            return null;
        }
    }

    private string CreateToken(
        string connectionId,
        string tokenUse,
        string? tenantId,
        string? entraObjectId,
        string? projectorAccount,
        TimeSpan lifetime)
    {
        var expires = DateTime.UtcNow.Add(lifetime);
        var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, connectionId),
            new("connection_id", connectionId),
            new("scope", _options.McpScope),
            new("token_use", tokenUse),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tid", tenantId));
        }

        if (!string.IsNullOrWhiteSpace(entraObjectId))
        {
            claims.Add(new Claim("oid", entraObjectId));
        }

        if (!string.IsNullOrWhiteSpace(projectorAccount))
        {
            claims.Add(new Claim("projector_account", projectorAccount));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters CreateValidationParameters()
    {
        var audiences = new List<string> { Audience };
        var mcpUrl = $"{Issuer}/mcp";
        if (!audiences.Contains(mcpUrl, StringComparer.OrdinalIgnoreCase))
        {
            audiences.Add(mcpUrl);
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    }
}
