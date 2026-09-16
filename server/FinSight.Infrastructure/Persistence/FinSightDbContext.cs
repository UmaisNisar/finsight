using FinSight.Core.Domain;
using FinSight.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FinSight.Infrastructure.Persistence;

public sealed class FinSightDbContext(
    DbContextOptions<FinSightDbContext> options,
    IUserContext userContext,
    IFieldProtector fieldProtector) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<GmailConnection> GmailConnections => Set<GmailConnection>();
    public DbSet<Statement> Statements => Set<Statement>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<MerchantRule> MerchantRules => Set<MerchantRule>();
    public DbSet<CustomCategory> CustomCategories => Set<CustomCategory>();
    public DbSet<FinancialAnalysisRecord> FinancialAnalyses => Set<FinancialAnalysisRecord>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();

    // Referenced by query filters; EF evaluates these per context instance.
    private Guid CurrentUserId => userContext.UserId ?? Guid.Empty;
    private bool IsSystem => userContext.IsSystem;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no decimal or DateTimeOffset type that sorts and compares correctly.
        // Money is stored as integer cents; timestamps as UTC ticks.
        configurationBuilder.Properties<decimal>().HaveConversion<CentsConverter>();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

        foreach (var enumType in typeof(StatementStatus).Assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == typeof(StatementStatus).Namespace))
        {
            configurationBuilder.Properties(enumType).HaveConversion<string>().HaveMaxLength(32);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var encrypted = new ValueConverter<string, string>(v => fieldProtector.Protect(v), v => fieldProtector.Unprotect(v));

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.GoogleSubject).IsUnique().HasFilter("GoogleSubject IS NOT NULL");
            e.Property(u => u.Email).HasMaxLength(320);
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.ComplexProperty(u => u.Settings, s =>
            {
                s.Property(p => p.Currency).HasMaxLength(3);
                s.Property(p => p.DateFormat).HasMaxLength(32);
            });
            e.HasQueryFilter(u => IsSystem || u.Id == CurrentUserId);
        });

        modelBuilder.Entity<GmailConnection>(e =>
        {
            e.HasIndex(c => c.UserId).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(c => IsSystem || c.UserId == CurrentUserId);
        });

        modelBuilder.Entity<Statement>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.SourceKey }).IsUnique();
            e.HasIndex(s => new { s.UserId, s.ContentHash });
            e.Property(s => s.SourceKey).HasMaxLength(300);
            e.Property(s => s.Filename).HasMaxLength(260);
            e.Property(s => s.Subject).HasMaxLength(500);
            e.Property(s => s.Sender).HasMaxLength(500);
            e.Property(s => s.AccountMask).HasMaxLength(4);
            e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.Transactions).WithOne(t => t.Statement).HasForeignKey(t => t.StatementId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => IsSystem || s.UserId == CurrentUserId);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.HasIndex(t => new { t.UserId, t.Fingerprint }).IsUnique();
            e.HasIndex(t => new { t.UserId, t.Date });
            e.HasIndex(t => new { t.UserId, t.MerchantKey });
            e.Property(t => t.Description).HasConversion(encrypted);
            e.Property(t => t.Fingerprint).HasMaxLength(64);
            e.Property(t => t.Merchant).HasMaxLength(120);
            e.Property(t => t.MerchantKey).HasMaxLength(120);
            e.Property(t => t.CategoryId).HasMaxLength(64);
            e.Property(t => t.UserCategoryId).HasMaxLength(64);
            e.Property(t => t.UserMerchant).HasMaxLength(120);
            e.Property(t => t.Currency).HasMaxLength(3);
            e.Ignore(t => t.EffectiveCategoryId);
            e.Ignore(t => t.EffectiveMerchant);
            e.Ignore(t => t.EffectiveType);
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasQueryFilter(t => IsSystem || t.UserId == CurrentUserId);
        });

        modelBuilder.Entity<MerchantRule>(e =>
        {
            e.HasIndex(r => new { r.UserId, r.MerchantKey }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(r => IsSystem || r.UserId == CurrentUserId);
        });

        modelBuilder.Entity<CustomCategory>(e =>
        {
            e.HasIndex(c => new { c.UserId, c.CategoryId }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(c => IsSystem || c.UserId == CurrentUserId);
        });

        modelBuilder.Entity<FinancialAnalysisRecord>(e =>
        {
            e.HasIndex(a => new { a.UserId, a.PeriodStart, a.PeriodEnd });
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(a => IsSystem || a.UserId == CurrentUserId);
        });

        modelBuilder.Entity<ProcessingJob>(e =>
        {
            e.HasIndex(j => new { j.UserId, j.CreatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(j => j.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(j => IsSystem || j.UserId == CurrentUserId);
        });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>Second line of defence: a write can never touch another user's rows.</summary>
    private void EnforceOwnership()
    {
        if (IsSystem)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            var owner = entry.Entity switch
            {
                IUserOwned owned => owned.UserId,
                User user => user.Id,
                _ => (Guid?)null,
            };

            if (owner is not null && (userContext.UserId is null || owner != userContext.UserId))
            {
                throw new UnauthorizedAccessException("Attempted to write data belonging to another user.");
            }
        }
    }

    private sealed class CentsConverter() : ValueConverter<decimal, long>(
        v => (long)decimal.Round(v * 100m, MidpointRounding.AwayFromZero),
        v => v / 100m);
}
