using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Finalization;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Domain.Teams;
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

    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<EngagementMember> EngagementMembers => Set<EngagementMember>();

    public DbSet<Assignment> Assignments => Set<Assignment>();

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
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("app_user");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.UserId).HasColumnName("user_id").ValueGeneratedNever();
            entity.Property(e => e.Username).HasColumnName("username");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.IsLocalDemo).HasColumnName("is_local_demo");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(e => e.IsExternalPrincipal).HasColumnName("is_external_principal");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("app_role"); entity.HasKey(e => e.RoleId);
            entity.Property(e => e.RoleId).HasColumnName("role_id").ValueGeneratedNever();
            entity.Property(e => e.RoleKey).HasColumnName("role_key");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.IsSystem).HasColumnName("is_system");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
        });
        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("role_permission"); entity.HasKey(e => new { e.RoleId, e.PermissionKey });
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.PermissionKey).HasColumnName("permission_key");
        });
        modelBuilder.Entity<EngagementMember>(entity =>
        {
            entity.ToTable("engagement_member"); entity.HasKey(e => e.EngagementMemberId);
            entity.Property(e => e.EngagementMemberId).HasColumnName("engagement_member_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.AddedAtUtc).HasColumnName("added_at_utc");
            entity.Property(e => e.AddedBy).HasColumnName("added_by");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });
        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.ToTable("assignment"); entity.HasKey(e => e.AssignmentId);
            entity.Property(e => e.AssignmentId).HasColumnName("assignment_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.AssigneeUserId).HasColumnName("assignee_user_id");
            entity.Property(e => e.ScopeType).HasColumnName("scope_type");
            entity.Property(e => e.ScopeId).HasColumnName("scope_id");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.AssignedAtUtc).HasColumnName("assigned_at_utc");
            entity.Property(e => e.AssignedBy).HasColumnName("assigned_by");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
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

        // The tables already declare these foreign keys (migrations are SQL). They are
        // mirrored here, without navigations, only so EF Core inserts parents before
        // children within one SaveChanges; it never creates or alters constraints.
        modelBuilder.Entity<Company>().HasOne<User>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Engagement>().HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Engagement>().HasOne<FinancialYear>().WithMany().HasForeignKey(e => e.FinancialYearId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Engagement>().HasOne<User>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Account>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Account>().HasOne<User>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialDataEntry>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialDataEntry>().HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialDataEntry>().HasOne<User>().WithMany().HasForeignKey(e => e.RecordedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<PriorYearRelationship>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.CurrentEngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<PriorYearRelationship>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.PriorEngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<PriorYearRelationship>().HasOne<User>().WithMany().HasForeignKey(e => e.LinkedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinalizationManifest>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinalizationManifest>().HasOne<User>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<AuditEvent>().HasOne<User>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<AuditEvent>().HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<AuditEvent>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<EngagementMember>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<EngagementMember>().HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<EngagementMember>().HasOne<Role>().WithMany().HasForeignKey(e => e.RoleId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Assignment>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);

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
