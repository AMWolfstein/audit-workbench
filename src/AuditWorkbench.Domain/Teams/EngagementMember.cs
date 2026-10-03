using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Teams;

public class EngagementMember
{
    private EngagementMember() { }
    public Guid EngagementMemberId { get; private set; }
    public Guid EngagementId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public string Status { get; private set; } = "ACTIVE";
    public string AddedAtUtc { get; private set; } = string.Empty;
    public Guid AddedBy { get; private set; }
    public string UpdatedAtUtc { get; private set; } = string.Empty;
    public int RowVersion { get; private set; } = 1;

    public static EngagementMember Create(Guid engagementId, Guid userId, Guid roleId, string now, Guid addedBy)
    {
        if (engagementId == Guid.Empty || userId == Guid.Empty || roleId == Guid.Empty)
            throw new ValidationException("Engagement, user and role are required for membership.");
        return new EngagementMember { EngagementMemberId = Guid.NewGuid(), EngagementId = engagementId,
            UserId = userId, RoleId = roleId, AddedAtUtc = now, UpdatedAtUtc = now, AddedBy = addedBy };
    }
}
