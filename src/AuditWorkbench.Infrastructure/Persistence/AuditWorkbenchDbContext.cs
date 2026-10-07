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

    // TB/GL import and financial data foundation (migration 0007).
    public DbSet<FinancialPeriod> FinancialPeriods => Set<FinancialPeriod>();

    public DbSet<FinancialUpload> FinancialUploads => Set<FinancialUpload>();

    public DbSet<FinancialDatasetImport> DatasetImports => Set<FinancialDatasetImport>();

    public DbSet<TbLine> TbLines => Set<TbLine>();

    public DbSet<GlJournal> GlJournals => Set<GlJournal>();

    public DbSet<GlLine> GlLines => Set<GlLine>();

    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    public DbSet<MaterialityRecord> MaterialityRecords => Set<MaterialityRecord>();

    public DbSet<AuditArea> AuditAreas => Set<AuditArea>();

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
            entity.Property(e => e.NormalizedCode).HasColumnName("normalized_code");
            entity.Property(e => e.AccountGroup).HasColumnName("account_group");
            entity.Property(e => e.AccountOrigin).HasColumnName("account_origin");
            entity.Property(e => e.AuditAreaId).HasColumnName("audit_area_id");
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

        modelBuilder.Entity<FinancialPeriod>(entity =>
        {
            entity.ToTable("financial_period");
            entity.HasKey(e => e.FinancialPeriodId);
            entity.Property(e => e.FinancialPeriodId).HasColumnName("financial_period_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.FinancialYearId).HasColumnName("financial_year_id");
            entity.Property(e => e.ReportingDate).HasColumnName("reporting_date");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            entity.HasIndex(e => e.EngagementId).IsUnique();
        });

        modelBuilder.Entity<FinancialUpload>(entity =>
        {
            entity.ToTable("financial_upload");
            entity.HasKey(e => e.UploadId);
            entity.Property(e => e.UploadId).HasColumnName("upload_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.DatasetKind).HasColumnName("dataset_kind");
            entity.Property(e => e.FileName).HasColumnName("file_name");
            entity.Property(e => e.ContentType).HasColumnName("content_type");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.Sha256).HasColumnName("sha256");
            entity.Property(e => e.StorageLocation).HasColumnName("storage_location");
            entity.Property(e => e.DetectedFormat).HasColumnName("detected_format");
            entity.Property(e => e.DetectedStructure).HasColumnName("detected_structure");
            entity.Property(e => e.UploadedAtUtc).HasColumnName("uploaded_at_utc");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");
            entity.HasIndex(e => new { e.EngagementId, e.DatasetKind, e.Sha256 });
        });

        modelBuilder.Entity<FinancialDatasetImport>(entity =>
        {
            entity.ToTable("dataset_import");
            entity.HasKey(e => e.ImportId);
            entity.Property(e => e.ImportId).HasColumnName("import_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.FinancialPeriodId).HasColumnName("financial_period_id");
            entity.Property(e => e.DatasetKind).HasColumnName("dataset_kind");
            entity.Property(e => e.ImportNo).HasColumnName("import_no");
            entity.Property(e => e.SnapshotLabel).HasColumnName("snapshot_label");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.SupersededByImportId).HasColumnName("superseded_by_import_id");
            entity.Property(e => e.RepeatOfImportId).HasColumnName("repeat_of_import_id");
            entity.Property(e => e.SourceUploadId).HasColumnName("source_upload_id");
            entity.Property(e => e.SourceFileName).HasColumnName("source_file_name");
            entity.Property(e => e.SourceFileSha256).HasColumnName("source_file_sha256");
            entity.Property(e => e.SourceFileSizeBytes).HasColumnName("source_file_size_bytes");
            entity.Property(e => e.SourceHeaderRowNo).HasColumnName("source_header_row_no");
            entity.Property(e => e.SourceSheetName).HasColumnName("source_sheet_name");
            entity.Property(e => e.CoverageThroughDate).HasColumnName("coverage_through_date");
            entity.Property(e => e.ColumnMappingJson).HasColumnName("column_mapping_json");
            entity.Property(e => e.FingerprintHash).HasColumnName("fingerprint_hash");
            entity.Property(e => e.ValidationJson).HasColumnName("validation_json");
            entity.Property(e => e.ValidationStatus).HasColumnName("validation_status");
            entity.Property(e => e.RowCount).HasColumnName("row_count");
            entity.Property(e => e.ValidRowCount).HasColumnName("valid_row_count");
            entity.Property(e => e.WarningCount).HasColumnName("warning_count");
            entity.Property(e => e.ErrorCount).HasColumnName("error_count");
            entity.Property(e => e.OutOfPeriodCount).HasColumnName("out_of_period_count");
            entity.Property(e => e.TotalDebitMinor).HasColumnName("total_debit_minor");
            entity.Property(e => e.TotalCreditMinor).HasColumnName("total_credit_minor");
            entity.Property(e => e.DifferenceMinor).HasColumnName("difference_minor");
            entity.Property(e => e.IsBalanced).HasColumnName("is_balanced");
            entity.Property(e => e.UnbalancedOverride).HasColumnName("unbalanced_override");
            entity.Property(e => e.ImportedAtUtc).HasColumnName("imported_at_utc");
            entity.Property(e => e.ImportedBy).HasColumnName("imported_by");
            entity.Property(e => e.FinalizedAtUtc).HasColumnName("finalized_at_utc");
            entity.Property(e => e.FinalizedBy).HasColumnName("finalized_by");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.EngagementId, e.DatasetKind, e.ImportNo }).IsUnique();
            entity.HasIndex(e => new { e.EngagementId, e.DatasetKind, e.SourceFileSha256 });
        });

        modelBuilder.Entity<TbLine>(entity =>
        {
            entity.ToTable("tb_line");
            entity.HasKey(e => e.TbLineId);
            entity.Property(e => e.TbLineId).HasColumnName("tb_line_id").ValueGeneratedNever();
            entity.Property(e => e.ImportId).HasColumnName("import_id");
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.FinancialPeriodId).HasColumnName("financial_period_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.SourceRowNo).HasColumnName("source_row_no");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.AccountName).HasColumnName("account_name");
            entity.Property(e => e.NormalizedCode).HasColumnName("normalized_code");
            entity.Property(e => e.DebitMinor).HasColumnName("debit_minor");
            entity.Property(e => e.CreditMinor).HasColumnName("credit_minor");
            entity.Property(e => e.BalanceMinor).HasColumnName("balance_minor");
            entity.Property(e => e.CurrencyCode).HasColumnName("currency_code");
            entity.Property(e => e.CostCenter).HasColumnName("cost_center");
            entity.Property(e => e.AccountGroup).HasColumnName("account_group");
            entity.Property(e => e.ExtraColumnsJson).HasColumnName("extra_columns_json");
            entity.Property(e => e.RowHash).HasColumnName("row_hash");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.HasIndex(e => new { e.ImportId, e.AccountCode }).IsUnique();
            entity.HasIndex(e => new { e.ImportId, e.LineNo });
        });

        modelBuilder.Entity<GlJournal>(entity =>
        {
            entity.ToTable("gl_journal");
            entity.HasKey(e => e.GlJournalId);
            entity.Property(e => e.GlJournalId).HasColumnName("gl_journal_id").ValueGeneratedNever();
            entity.Property(e => e.ImportId).HasColumnName("import_id");
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.FinancialPeriodId).HasColumnName("financial_period_id");
            entity.Property(e => e.JournalIdentity).HasColumnName("journal_identity");
            entity.Property(e => e.IdentitySource).HasColumnName("identity_source");
            entity.Property(e => e.JournalNumber).HasColumnName("journal_number");
            entity.Property(e => e.JournalSource).HasColumnName("journal_source");
            entity.Property(e => e.PostingDate).HasColumnName("posting_date");
            entity.Property(e => e.Reference).HasColumnName("reference");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.CurrencyCode).HasColumnName("currency_code");
            entity.Property(e => e.PreparedBy).HasColumnName("prepared_by");
            entity.Property(e => e.LineCount).HasColumnName("line_count");
            entity.Property(e => e.JournalHash).HasColumnName("journal_hash");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.HasIndex(e => new { e.ImportId, e.JournalIdentity }).IsUnique();
        });

        modelBuilder.Entity<GlLine>(entity =>
        {
            entity.ToTable("gl_line");
            entity.HasKey(e => e.GlLineId);
            entity.Property(e => e.GlLineId).HasColumnName("gl_line_id").ValueGeneratedNever();
            entity.Property(e => e.GlJournalId).HasColumnName("gl_journal_id");
            entity.Property(e => e.ImportId).HasColumnName("import_id");
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.FinancialPeriodId).HasColumnName("financial_period_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.LineIdentity).HasColumnName("line_identity");
            entity.Property(e => e.IdentitySource).HasColumnName("identity_source");
            entity.Property(e => e.SourceLineNo).HasColumnName("source_line_no");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.AccountName).HasColumnName("account_name");
            entity.Property(e => e.TransactionDate).HasColumnName("transaction_date");
            entity.Property(e => e.PostingDate).HasColumnName("posting_date");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DebitMinor).HasColumnName("debit_minor");
            entity.Property(e => e.CreditMinor).HasColumnName("credit_minor");
            entity.Property(e => e.AmountMinor).HasColumnName("amount_minor");
            entity.Property(e => e.CurrencyCode).HasColumnName("currency_code");
            entity.Property(e => e.JournalSource).HasColumnName("journal_source");
            entity.Property(e => e.Reference).HasColumnName("reference");
            entity.Property(e => e.PreparedBy).HasColumnName("prepared_by");
            entity.Property(e => e.IsOutOfPeriod).HasColumnName("is_out_of_period");
            entity.Property(e => e.LineHash).HasColumnName("line_hash");
            entity.Property(e => e.ValueHash).HasColumnName("value_hash");
            entity.Property(e => e.AttributeHash).HasColumnName("attribute_hash");
            entity.Property(e => e.ExtraColumnsJson).HasColumnName("extra_columns_json");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.HasIndex(e => new { e.ImportId, e.AccountCode });
            entity.HasIndex(e => new { e.ImportId, e.TransactionDate });
        });

        modelBuilder.Entity<ImportJob>(entity =>
        {
            entity.ToTable("import_job");
            entity.HasKey(e => e.JobId);
            entity.Property(e => e.JobId).HasColumnName("job_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.FinancialPeriodId).HasColumnName("financial_period_id");
            entity.Property(e => e.DatasetKind).HasColumnName("dataset_kind");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Stage).HasColumnName("stage");
            entity.Property(e => e.UploadId).HasColumnName("upload_id");
            entity.Property(e => e.ImportId).HasColumnName("import_id");
            entity.Property(e => e.ColumnMappingJson).HasColumnName("column_mapping_json");
            entity.Property(e => e.AttemptNo).HasColumnName("attempt_no");
            entity.Property(e => e.AllowUnbalanced).HasColumnName("allow_unbalanced");
            entity.Property(e => e.AllowRepeat).HasColumnName("allow_repeat");
            entity.Property(e => e.CancelRequested).HasColumnName("cancel_requested");
            entity.Property(e => e.ProcessedRows).HasColumnName("processed_rows");
            entity.Property(e => e.TotalRows).HasColumnName("total_rows");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.ErrorCode).HasColumnName("error_code");
            entity.Property(e => e.RequestedAtUtc).HasColumnName("requested_at_utc");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc");
            entity.Property(e => e.CompletedAtUtc).HasColumnName("completed_at_utc");
        });

        modelBuilder.Entity<MaterialityRecord>(entity =>
        {
            entity.ToTable("engagement_materiality");
            entity.HasKey(e => e.MaterialityId);
            entity.Property(e => e.MaterialityId).HasColumnName("materiality_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.FinancialPeriodId).HasColumnName("financial_period_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.OverallMaterialityMinor).HasColumnName("overall_materiality_minor");
            entity.Property(e => e.PerformanceMaterialityMinor).HasColumnName("performance_materiality_minor");
            entity.Property(e => e.ClearlyTrivialThresholdMinor).HasColumnName("clearly_trivial_threshold_minor");
            entity.Property(e => e.CurrencyCode).HasColumnName("currency_code");
            entity.Property(e => e.BasisNote).HasColumnName("basis_note");
            entity.Property(e => e.DeterminedAtUtc).HasColumnName("determined_at_utc");
            entity.Property(e => e.DeterminedBy).HasColumnName("determined_by");
            entity.Property(e => e.ApprovedAtUtc).HasColumnName("approved_at_utc");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.SupersedesId).HasColumnName("supersedes_id");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.EngagementId, e.VersionNo }).IsUnique();
        });

        modelBuilder.Entity<AuditArea>(entity =>
        {
            entity.ToTable("audit_area");
            entity.HasKey(e => e.AuditAreaId);
            entity.Property(e => e.AuditAreaId).HasColumnName("audit_area_id").ValueGeneratedNever();
            entity.Property(e => e.EngagementId).HasColumnName("engagement_id");
            entity.Property(e => e.AreaCode).HasColumnName("area_code");
            entity.Property(e => e.AreaName).HasColumnName("area_name");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.EngagementId, e.AreaCode }).IsUnique();
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

        modelBuilder.Entity<FinancialPeriod>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialPeriod>().HasOne<FinancialYear>().WithMany().HasForeignKey(e => e.FinancialYearId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialPeriod>().HasOne<User>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialUpload>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialUpload>().HasOne<User>().WithMany().HasForeignKey(e => e.UploadedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialDatasetImport>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialDatasetImport>().HasOne<FinancialPeriod>().WithMany().HasForeignKey(e => e.FinancialPeriodId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialDatasetImport>().HasOne<FinancialUpload>().WithMany().HasForeignKey(e => e.SourceUploadId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<FinancialDatasetImport>().HasOne<User>().WithMany().HasForeignKey(e => e.ImportedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<TbLine>().HasOne<FinancialDatasetImport>().WithMany().HasForeignKey(e => e.ImportId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<TbLine>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<TbLine>().HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<GlJournal>().HasOne<FinancialDatasetImport>().WithMany().HasForeignKey(e => e.ImportId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<GlJournal>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<GlLine>().HasOne<GlJournal>().WithMany().HasForeignKey(e => e.GlJournalId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<GlLine>().HasOne<FinancialDatasetImport>().WithMany().HasForeignKey(e => e.ImportId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<GlLine>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<GlLine>().HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<ImportJob>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<ImportJob>().HasOne<FinancialPeriod>().WithMany().HasForeignKey(e => e.FinancialPeriodId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<ImportJob>().HasOne<User>().WithMany().HasForeignKey(e => e.RequestedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<MaterialityRecord>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<MaterialityRecord>().HasOne<User>().WithMany().HasForeignKey(e => e.DeterminedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<AuditArea>().HasOne<Engagement>().WithMany().HasForeignKey(e => e.EngagementId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<AuditArea>().HasOne<User>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Account>().HasOne<AuditArea>().WithMany().HasForeignKey(e => e.AuditAreaId).OnDelete(DeleteBehavior.NoAction);

        // SQLite workspaces store canonical UUID text (ADR-017). Central providers
        // retain their native UUID/uniqueidentifier mappings; provider behavior is
        // isolated here rather than leaking into the domain or application layer.
        if (Database.IsSqlite())
        {
            foreach (var property in modelBuilder.Model.GetEntityTypes()
                         .SelectMany(type => type.GetProperties())
                         .Where(property => property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?)))
            {
                property.SetValueConverter(GuidTextConverter.Instance);
            }
        }
    }
}
