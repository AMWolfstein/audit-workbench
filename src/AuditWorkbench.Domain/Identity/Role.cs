namespace AuditWorkbench.Domain.Identity;

public class Role
{
    private Role() { }
    public Guid RoleId { get; private set; }
    public string RoleKey { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public bool IsSystem { get; private set; }
    public string CreatedAtUtc { get; private set; } = string.Empty;
}

public class RolePermission
{
    private RolePermission() { }
    public Guid RoleId { get; private set; }
    public string PermissionKey { get; private set; } = string.Empty;
}

public static class BuiltInRoles
{
    public static readonly Guid PartnerId = new("10000000-0000-4000-8000-000000000001");
    public const string Partner = "PARTNER";
    public const string Manager = "MANAGER";
    public const string Senior = "SENIOR";
    public const string Auditor = "AUDITOR";
    public const string Reviewer = "REVIEWER";
    public const string ReadOnly = "READ_ONLY";
}

public static class Permissions
{
    public const string ViewEngagement = "VIEW_ENGAGEMENT";
    public const string EditEngagement = "EDIT_ENGAGEMENT";
    public const string ManageTeam = "MANAGE_TEAM";
    public const string ManageAssignments = "MANAGE_ASSIGNMENTS";
    public const string EditWorkingPapers = "EDIT_WORKING_PAPERS";
    public const string UploadEvidence = "UPLOAD_EVIDENCE";
    public const string ReviewWorkingPapers = "REVIEW_WORKING_PAPERS";
    public const string ClearReviewNotes = "CLEAR_REVIEW_NOTES";
    public const string FinalizeEngagement = "FINALIZE_ENGAGEMENT";
    public const string ExportEngagement = "EXPORT_ENGAGEMENT";
    public const string ImportEngagement = "IMPORT_ENGAGEMENT";
    public const string ViewAuditTrail = "VIEW_AUDIT_TRAIL";
}
