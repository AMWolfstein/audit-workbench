namespace AuditWorkbench.Domain.Auditing;

/// <summary>Stable vocabulary of recorded events (requirements.md FR-M17).</summary>
public static class AuditEventType
{
    public const string CompanyCreated = "COMPANY_CREATED";
    public const string EngagementCreated = "ENGAGEMENT_CREATED";
    public const string EngagementStatusChanged = "ENGAGEMENT_STATUS_CHANGED";
    public const string AccountCreated = "ACCOUNT_CREATED";
    public const string FinancialDataAdded = "FINANCIAL_DATA_ADDED";
    public const string FinancialDataChanged = "FINANCIAL_DATA_CHANGED";
    public const string EngagementFinalized = "ENGAGEMENT_FINALIZED";
    public const string PriorYearLinked = "PRIOR_YEAR_LINKED";
    public const string BackupCreated = "BACKUP_CREATED";
    public const string DemoDataSeeded = "DEMO_DATA_SEEDED";
    public const string ProtectedWriteRejected = "PROTECTED_WRITE_REJECTED";
    public const string IntegrityCheckFailed = "INTEGRITY_CHECK_FAILED";
    public const string EngagementMemberAdded = "ENGAGEMENT_MEMBER_ADDED";
    public const string EngagementMemberRoleChanged = "ENGAGEMENT_MEMBER_ROLE_CHANGED";
    public const string EngagementMemberSuspended = "ENGAGEMENT_MEMBER_SUSPENDED";
    public const string EngagementMemberReactivated = "ENGAGEMENT_MEMBER_REACTIVATED";
    public const string AssignmentCreated = "ASSIGNMENT_CREATED";
    public const string AssignmentReassigned = "ASSIGNMENT_REASSIGNED";
    public const string AssignmentCompleted = "ASSIGNMENT_COMPLETED";
    public const string AssignmentCancelled = "ASSIGNMENT_CANCELLED";
    public const string UserCreated = "USER_CREATED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string UserReactivated = "USER_REACTIVATED";
    public const string ClientExported = "CLIENT_EXPORTED";
    public const string ClientImported = "CLIENT_IMPORTED";

    // Financial data foundation (TB/GL import & financial period)
    public const string FinancialUploadReceived = "FINANCIAL_UPLOAD_RECEIVED";
    public const string ImportJobQueued = "IMPORT_JOB_QUEUED";
    public const string TbImportStarted = "TB_IMPORT_STARTED";
    public const string TbImportCompleted = "TB_IMPORT_COMPLETED";
    public const string TbImportFailed = "TB_IMPORT_FAILED";
    public const string TbFinalized = "TB_FINALIZED";
    public const string GlImportStarted = "GL_IMPORT_STARTED";
    public const string GlImportCompleted = "GL_IMPORT_COMPLETED";
    public const string GlImportFailed = "GL_IMPORT_FAILED";
    public const string GlFinalized = "GL_FINALIZED";
    public const string DatasetActivated = "DATASET_ACTIVATED";
    public const string DatasetSuperseded = "DATASET_SUPERSEDED";
    public const string FinancialPeriodUpdated = "FINANCIAL_PERIOD_UPDATED";
    public const string FinancialPeriodStatusChanged = "FINANCIAL_PERIOD_STATUS_CHANGED";
    public const string MaterialityRecorded = "MATERIALITY_RECORDED";
    public const string MaterialityApproved = "MATERIALITY_APPROVED";
    public const string AuditAreaCreated = "AUDIT_AREA_CREATED";
    public const string AccountAuditAreaAssigned = "ACCOUNT_AUDIT_AREA_ASSIGNED";
    public const string ImportJobCancelled = "IMPORT_JOB_CANCELLED";
}

public static class AuditEventOutcome
{
    public const string Success = "SUCCESS";
    public const string Rejected = "REJECTED";
}

public static class AuditEntityType
{
    public const string Company = "COMPANY";
    public const string Engagement = "ENGAGEMENT";
    public const string Account = "ACCOUNT";
    public const string FinancialData = "FINANCIAL_DATA";
    public const string PriorYearRelationship = "PRIOR_YEAR_RELATIONSHIP";
    public const string Workspace = "WORKSPACE";
    public const string EngagementMember = "ENGAGEMENT_MEMBER";
    public const string Assignment = "ASSIGNMENT";
    public const string User = "USER";
    public const string FinancialPeriod = "FINANCIAL_PERIOD";
    public const string FinancialUpload = "FINANCIAL_UPLOAD";
    public const string DatasetImport = "DATASET_IMPORT";
    public const string TbLine = "TB_LINE";
    public const string GlLine = "GL_LINE";
    public const string ImportJob = "IMPORT_JOB";
    public const string Materiality = "MATERIALITY";
    public const string AuditArea = "AUDIT_AREA";
}
