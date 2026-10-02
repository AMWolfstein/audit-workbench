using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Finalization;
using AuditWorkbench.Domain.FinancialData;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping onto the SQL-first schema in /db/migrations. EF never owns
/// the schema (ADR-017): it maps to tables created by the migration runner, so
/// the constraints and triggers proven by the verification harness are exactly
/// the ones the application runs against.
/// </summary>
public class AuditWorkbenchDbContext : DbContext
{
    public AuditWorkbenchDbContext(DbContextOptions<AuditWorkbenchDbContext> options)
        : base(options)
    {
    }

    public DbSet<LocalUser> Users => Set<LocalUser>();

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<FinancialYear> FinancialYears => Set<FinancialYear>();

    public DbSet<Engagement> Engagements => Set<Engagement>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<FinancialDataEntry> FinancialData => Set<FinancialDataEntry>();

    public DbSet<PriorYearRelationship> PriorYearRelationships => Set<PriorYearRelationship>();

    public DbSet<FinalizationManifest> FinalizationManifests => Set<FinalizationManifest>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LocalUser>(entity =>
        {
            entity.ToTable("app_user");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.UserId).HasColumnName("user_id").ValueGeneratedNever();
            entity.Property(e => e.Username).HasColumnName("username");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.IsLocalDemo).HasColumnName("is_local_demo");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        modelBuilder.Entity<Company>(entity =>
        {
            entity.ToTable("company");
            entity.HasKey(e => e.CompanyId);
            entity.Property(e => e.CompanyId).HasColumnName("company_id").ValueGeneratedNever();
            entity.Property(e => e.LegalName).HasColumnName("legal_name");
            entity.Property(e => e.ShortName).HasColumnName("short_name");
            entity.Property(e => e.Industry).HasColumnName("industry");
            entity.Property(e => e.CountryCode).HasColumnName("country_code");
            entity.Property(e => e.TaxReference).HasColumnName("tax_reference");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ArchivedAtUtc).HasColumnName("archived_at_utc");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

        modelBuilder.Entity<FinancialYear>(entity =>
        {
            entity.ToTable("financial_year");
            entity.HasKey(e => e.FinancialYearId);
            entity.Property(e => e.FinancialYearId).HasColumnName("financial_year_id").ValueGeneratedNever();
            entity.Property(e => e.Label).HasColumnName("label");
            entity.Property(e => e.PeriodStart).HasColumnName("period_start");
            entity.Property(e => e.PeriodEnd).HasColumnName("period_end");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        modelBuilder.Entity<Engagement>(entity =>
        {
            entity.ToTable("engagement");
            entity.HasKey(e => e.EngagementId);
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id").ValueGeneratedNever();
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.FinancialYearId).HasColumnName("financial_year_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CurrencyCode).HasColumnName("currency_code");
            entity.Property(e => e.MinorUnitScale).HasColumnName("minor_unit_scale");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.FinalizedAtUtc).HasColumnName("finalized_at_utc");
            entity.Property(e => e.FinalizedBy).HasColumnName("finalized_by");
            entity.Property(e => e.FinalizationDigest).HasColumnName("finalization_digest");
            entity.Property(e => e.FinalizationManifestVersion).HasColumnName("finalization_manifest_version");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.CompanyId, e.FinancialYearId }).IsUnique();
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("account");
            entity.HasKey(e => e.AccountId);
            entity.Property(e => e.AccountId).HasColumnName("account_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.AccountName).HasColumnName("account_name");
            entity.Property(e => e.AccountTypeCode).HasColumnName("account_type");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.EngagementId, e.AccountCode }).IsUnique();
        });

        modelBuilder.Entity<FinancialDataEntry>(entity =>
        {
            entity.ToTable("financial_data");
            entity.HasKey(e => e.FinancialDataId);
            entity.Property(e => e.FinancialDataId).HasColumnName("financial_data_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.RevisionNo).HasColumnName("revision_no");
            entity.Property(e => e.AmountMinor).HasColumnName("amount_minor");
            entity.Property(e => e.CurrencyCode).HasColumnName("currency_code");
            entity.Property(e => e.SupersedesId).HasColumnName("supersedes_id");
            entity.Property(e => e.CorrectionReason).HasColumnName("correction_reason");
            entity.Property(e => e.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entity.Property(e => e.RecordedBy).HasColumnName("recorded_by");
            entity.HasIndex(e => new { e.EngagementId, e.AccountId, e.RevisionNo }).IsUnique();
        });

        modelBuilder.Entity<PriorYearRelationship>(entity =>
        {
            entity.ToTable("prior_year_relationship");
            entity.HasKey(e => e.RelationshipId);
            entity.Property(e => e.RelationshipId).HasColumnName("relationship_id").ValueGeneratedNever();
            entity.Property(e => e.CurrentEngagementId).HasColumnName("current_engagement_id");
            entity.Property(e => e.PriorEngagementId).HasColumnName("prior_engagement_id");
            entity.Property(e => e.LinkedAtUtc).HasColumnName("linked_at_utc");
            entity.Property(e => e.LinkedBy).HasColumnName("linked_by");
            entity.HasIndex(e => e.CurrentEngagementId).IsUnique();
        });

        modelBuilder.Entity<FinalizationManifest>(entity =>
        {
            entity.ToTable("finalization_manifest");
            entity.HasKey(e => e.ManifestId);
            entity.Property(e => e.ManifestId).HasColumnName("manifest_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.ManifestVersion).HasColumnName("manifest_version");
            entity.Property(e => e.RootDigest).HasColumnName("root_digest");
            entity.Property(e => e.CanonicalContent).HasColumnName("canonical_content");
            entity.Property(e => e.RecordCount).HasColumnName("record_count");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => e.EngagementId).IsUnique();
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_event");
            entity.HasKey(e => e.AuditEventId);
            entity.Property(e => e.AuditEventId).HasColumnName("audit_event_id").ValueGeneratedNever();
            entity.Property(e => e.SequenceNo).HasColumnName("sequence_no");
            entity.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc");
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(e => e.ActorDisplayName).HasColumnName("actor_display_name");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.EntityType).HasColumnName("entity_type");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DetailsJson).HasColumnName("details_json");
            entity.Property(e => e.PreviousEventHash).HasColumnName("previous_event_hash");
            entity.Property(e => e.EventHash).HasColumnName("event_hash");
            entity.HasIndex(e => e.SequenceNo).IsUnique();
        });

        // Identifiers are stored as canonical lowercase UUID text so a workspace
        // stays readable with any SQLite tool (ADR-017).
        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(type => type.GetProperties())
                     .Where(property => property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?)))
        {
            property.SetValueConverter(GuidTextConverter.Instance);
        }
    }
}
