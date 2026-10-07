using BonBon.Entities;
using Microsoft.EntityFrameworkCore;

namespace BonBon.DataAccess;

public class BonBonDbContext(DbContextOptions<BonBonDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AccountRole> AccountRoles => Set<AccountRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("bonbon");

        ConfigureAccount(modelBuilder);
        ConfigureRole(modelBuilder);
        ConfigureAccountRole(modelBuilder);
        ConfigureRefreshToken(modelBuilder);
    }

    // -------------------------------------------------------------------------
    private static void ConfigureAccount(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.ToTable("account");
            e.HasKey(x => x.AccountId);

            e.Property(x => x.AccountId)
             .HasColumnName("account_id")
             .HasColumnType("uuid")
             .HasDefaultValueSql("gen_random_uuid()");

            e.Property(x => x.Email)
             .HasColumnName("email")
             .HasColumnType("varchar(254)")
             .IsRequired();

            e.Property(x => x.PasswordHash)
             .HasColumnName("password_hash")
             .HasColumnType("varchar(512)")
             .IsRequired(false);

            e.Property(x => x.FullName)
             .HasColumnName("full_name")
             .HasColumnType("varchar(150)")
             .IsRequired();

            e.Property(x => x.DisplayName)
             .HasColumnName("display_name")
             .HasColumnType("varchar(150)")
             .IsRequired(false);

            e.Property(x => x.Phone)
             .HasColumnName("phone")
             .HasColumnType("varchar(30)")
             .IsRequired(false);

            e.Property(x => x.Status)
             .HasColumnName("status")
             .HasColumnType("varchar(20)")
             .HasDefaultValue("Active")
             .IsRequired();

            e.Property(x => x.CreatedAt)
             .HasColumnName("created_at")
             .HasColumnType("timestamptz")
             .HasDefaultValueSql("now()");

            // Case-insensitive unique index — mirrors DB: lower(btrim(email))
            e.HasIndex(x => x.Email)
             .IsUnique()
             .HasDatabaseName("uq_account_email");

            e.HasMany(x => x.AccountRoles)
             .WithOne(x => x.Account)
             .HasForeignKey(x => x.AccountId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(x => x.RefreshTokens)
             .WithOne(x => x.Account)
             .HasForeignKey(x => x.AccountId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureRole(ModelBuilder b)
    {
        b.Entity<Role>(e =>
        {
            e.ToTable("role");
            e.HasKey(x => x.RoleId);

            e.Property(x => x.RoleId)
             .HasColumnName("role_id")
             .HasColumnType("smallint");

            e.Property(x => x.Code)
             .HasColumnName("code")
             .HasColumnType("varchar(30)")
             .IsRequired();

            // Seed — mirrors INSERT trong SQL migration
            e.HasData(
                new Role { RoleId = 1, Code = "Admin" },
                new Role { RoleId = 2, Code = "Customer" },
                new Role { RoleId = 3, Code = "VehicleOwner" }
            );
        });
    }

    private static void ConfigureAccountRole(ModelBuilder b)
    {
        b.Entity<AccountRole>(e =>
        {
            e.ToTable("account_role");
            e.HasKey(x => new { x.AccountId, x.RoleId });

            e.Property(x => x.AccountId)
             .HasColumnName("account_id")
             .HasColumnType("uuid");

            e.Property(x => x.RoleId)
             .HasColumnName("role_id")
             .HasColumnType("smallint");

            e.Property(x => x.Status)
             .HasColumnName("status")
             .HasColumnType("varchar(20)")
             .HasDefaultValue("Pending")
             .IsRequired();

            e.Property(x => x.AssignedAt)
             .HasColumnName("assigned_at")
             .HasColumnType("timestamptz")
             .HasDefaultValueSql("now()");

            e.HasOne(x => x.Role)
             .WithMany(x => x.AccountRoles)
             .HasForeignKey(x => x.RoleId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.RoleId, x.Status })
             .HasDatabaseName("ix_account_role_role_status");
        });
    }

    private static void ConfigureRefreshToken(ModelBuilder b)
    {
        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_token");
            e.HasKey(x => x.RefreshTokenId);

            e.Property(x => x.RefreshTokenId)
             .HasColumnName("refresh_token_id")
             .HasColumnType("uuid")
             .HasDefaultValueSql("gen_random_uuid()");

            e.Property(x => x.AccountId)
             .HasColumnName("account_id")
             .HasColumnType("uuid");

            e.Property(x => x.TokenHash)
             .HasColumnName("token_hash")
             .HasColumnType("varchar(512)")
             .IsRequired();

            e.Property(x => x.ExpiresAt)
             .HasColumnName("expires_at")
             .HasColumnType("timestamptz");

            e.Property(x => x.CreatedAt)
             .HasColumnName("created_at")
             .HasColumnType("timestamptz")
             .HasDefaultValueSql("now()");

            e.Property(x => x.RevokedAt)
             .HasColumnName("revoked_at")
             .HasColumnType("timestamptz")
             .IsRequired(false);

            e.Property(x => x.DeviceInfo)
             .HasColumnName("device_info")
             .HasColumnType("text")
             .IsRequired(false);

            // Không map computed property IsActive/IsExpired/IsRevoked lên DB
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.IsExpired);
            e.Ignore(x => x.IsRevoked);

            e.HasIndex(x => x.TokenHash)
             .IsUnique()
             .HasDatabaseName("uq_refresh_token_hash");

            e.HasIndex(x => new { x.AccountId, x.RevokedAt })
             .HasDatabaseName("ix_refresh_token_account_active");
        });
    }
}
