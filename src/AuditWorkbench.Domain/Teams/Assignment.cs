using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Teams;

public class Assignment
{
    private Assignment() { }
    public Guid AssignmentId { get; private set; }
    public Guid EngagementId { get; private set; }
    public Guid AssigneeUserId { get; private set; }
    public string ScopeType { get; private set; } = string.Empty;
    public string ScopeId { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Status { get; private set; } = "ACTIVE";
    public string AssignedAtUtc { get; private set; } = string.Empty;
    public Guid AssignedBy { get; private set; }
    public string UpdatedAtUtc { get; private set; } = string.Empty;
    public int RowVersion { get; private set; } = 1;

    public static Assignment Create(Guid engagementId, Guid assignee, string scopeType, string scopeId,
        string title, string now, Guid assignedBy)
    {
        title = (title ?? string.Empty).Trim();
        if (title.Length == 0) throw new ValidationException("Assignment title is required.");
        return new Assignment { AssignmentId = Guid.NewGuid(), EngagementId = engagementId,
            AssigneeUserId = assignee, ScopeType = scopeType.Trim().ToUpperInvariant(), ScopeId = scopeId.Trim(),
            Title = title, AssignedAtUtc = now, UpdatedAtUtc = now, AssignedBy = assignedBy };
    }
}
