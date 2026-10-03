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
    public const string AssignmentCompleted = "ASSIGNMENT_COMPLETED";
    public const string AssignmentCancelled = "ASSIGNMENT_CANCELLED";
    public const string UserCreated = "USER_CREATED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string UserReactivated = "USER_REACTIVATED";
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
}
