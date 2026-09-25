using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Projector.Domain.Auth;

namespace Projector.Application.Persistence;

public sealed class ProjectorTokenDbContext : DbContext
{
    public ProjectorTokenDbContext(DbContextOptions<ProjectorTokenDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProjectorConnectionEntity> Connections => Set<ProjectorConnectionEntity>();

    public DbSet<PendingAuthorizationEntity> PendingAuthorizations => Set<PendingAuthorizationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectorConnectionEntity>(e =>
        {
            e.ToTable("ProjectorConnections");
            e.HasKey(x => x.ConnectionId);
            e.HasIndex(x => new { x.TenantId, x.EntraObjectId, x.ProjectorAccountCode })
                .IsUnique()
                .HasDatabaseName("UX_ProjectorConnections_Owner");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<PendingAuthorizationEntity>(e =>
        {
            e.ToTable("PendingAuthorizations");
            e.HasKey(x => x.Key);
            e.HasIndex(x => x.ExpiresAt);
        });
    }
}

public sealed class ProjectorConnectionEntity
{
    [MaxLength(64)]
    public string ConnectionId { get; set; } = "";

    [MaxLength(64)]
    public string TenantId { get; set; } = "";

    [MaxLength(64)]
    public string EntraObjectId { get; set; } = "";

    [MaxLength(64)]
    public string ProjectorAccountCode { get; set; } = "";

    public string EncryptedAccessToken { get; set; } = "";

    public string EncryptedRefreshToken { get; set; } = "";

    public DateTimeOffset AccessTokenExpiresAt { get; set; }

    public string GrantedScopes { get; set; } = "";

    public string RestServiceAuthority { get; set; } = "";

    public string SoapServiceAuthority { get; set; } = "";

    [MaxLength(32)]
    public string ConnectionStatus { get; set; } = "Active";

    [MaxLength(16)]
    public string EncryptionKeyVersion { get; set; } = "1";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastRefreshAt { get; set; }

    public DateTimeOffset? RefreshInProgressUntil { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    [MaxLength(256)]
    public string? DisplayName { get; set; }
}

public sealed class PendingAuthorizationEntity
{
    [MaxLength(128)]
    public string Key { get; set; } = "";

    public string PayloadJson { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}
