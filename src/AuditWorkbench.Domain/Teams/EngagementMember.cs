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

    public bool IsActive => Status == "ACTIVE";

    public static EngagementMember Create(Guid engagementId, Guid userId, Guid roleId, string now, Guid addedBy)
    {
        if (engagementId == Guid.Empty || userId == Guid.Empty || roleId == Guid.Empty)
            throw new ValidationException("Engagement, user and role are required for membership.");
        return new EngagementMember { EngagementMemberId = Guid.NewGuid(), EngagementId = engagementId,
            UserId = userId, RoleId = roleId, AddedAtUtc = now, UpdatedAtUtc = now, AddedBy = addedBy };
    }

    /// <summary>Stale commands are refused before the conditional update runs (ADR-022).</summary>
    public void EnsureExpectedVersion(int? expectedRowVersion)
    {
        if (expectedRowVersion is not null && expectedRowVersion != RowVersion)
        {
            throw new ConcurrencyException(
                "This team membership changed in another window. Reload the team page and try again.");
        }
    }

    public void ChangeRole(Guid newRoleId, string now)
    {
        if (newRoleId == Guid.Empty)
            throw new ValidationException("A role is required for membership.");
        if (newRoleId == RoleId)
            throw new ValidationException("The member already has this role.");
        RoleId = newRoleId;
        Touch(now);
    }

    public void Suspend(string now)
    {
        if (!IsActive)
            throw new ValidationException("Only an active membership can be suspended.");
        Status = "SUSPENDED";
        Touch(now);
    }

    public void Reactivate(string now)
    {
        if (Status != "SUSPENDED")
            throw new ValidationException("Only a suspended membership can be reactivated.");
        Status = "ACTIVE";
        Touch(now);
    }

    private void Touch(string now)
    {
        UpdatedAtUtc = now;
        RowVersion++;
    }
}
