using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Domain.Teams;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Teams;

public sealed record AssignmentRow(
    Guid AssignmentId,
    Guid EngagementId,
    Guid AssigneeUserId,
    string ScopeType,
    string ScopeId,
    string Title,
    string Status,
    string UpdatedAtUtc,
    int RowVersion);

/// <summary>Engagement-scoped access policy. An application account alone grants no client access.</summary>
public sealed class EngagementAuthorizationService
{
    private readonly AuditWorkbenchDbContext _db;
    private readonly ICurrentActor _actor;
    public EngagementAuthorizationService(AuditWorkbenchDbContext db, ICurrentActor actor) { _db = db; _actor = actor; }

    public Task<bool> HasPermissionAsync(Guid engagementId, string permission,
        CancellationToken cancellationToken = default) =>
        _actor.IsAuthenticated && _actor.UserId != Guid.Empty
            ? (from member in _db.EngagementMembers
               join user in _db.Users on member.UserId equals user.UserId
               join rp in _db.RolePermissions on member.RoleId equals rp.RoleId
               where member.EngagementId == engagementId && member.UserId == _actor.UserId
                     && member.Status == "ACTIVE" && user.Status == "ACTIVE" && rp.PermissionKey == permission
               select member.EngagementMemberId).AnyAsync(cancellationToken)
            : Task.FromResult(false);

    public async Task RequireAsync(Guid engagementId, string permission, CancellationToken cancellationToken = default)
    {
        if (!await HasPermissionAsync(engagementId, permission, cancellationToken).ConfigureAwait(false))
            throw new AuthorizationException("You are not an active engagement member with the required permission.")
            {
                EngagementId = engagementId,
            };
    }

    /// <summary>
    /// Workspace-level privilege (ADR-024): an active user holding an active Partner or
    /// Manager membership on any engagement. A workspace with no engagement at all is
    /// unowned and open to any authenticated actor, so it can be bootstrapped.
    /// </summary>
    public async Task<bool> HasWorkspacePrivilegeAsync(CancellationToken cancellationToken = default)
    {
        if (!_actor.IsAuthenticated || _actor.UserId == Guid.Empty)
        {
            return false;
        }

        var privileged = await (
            from member in _db.EngagementMembers.AsNoTracking()
            join user in _db.Users.AsNoTracking() on member.UserId equals user.UserId
            join role in _db.Roles.AsNoTracking() on member.RoleId equals role.RoleId
            where member.UserId == _actor.UserId
                  && member.Status == "ACTIVE"
                  && user.Status == "ACTIVE"
                  && (role.RoleKey == BuiltInRoles.Partner || role.RoleKey == BuiltInRoles.Manager)
            select member.EngagementMemberId).AnyAsync(cancellationToken).ConfigureAwait(false);

        return privileged || !await _db.Engagements.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RequireWorkspacePrivilegeAsync(CancellationToken cancellationToken = default)
    {
        if (!await HasWorkspacePrivilegeAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new AuthorizationException(
                "This action requires an active Partner or Manager membership on an engagement.");
        }
    }

    /// <summary>
    /// The engagements whose year-owned records the actor may read under the given
    /// permission. Deny-by-default: an empty result means no engagement access.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> PermittedEngagementIdsAsync(string permission,
        CancellationToken cancellationToken = default)
    {
        if (!_actor.IsAuthenticated || _actor.UserId == Guid.Empty)
        {
            return Array.Empty<Guid>();
        }

        return await (
            from member in _db.EngagementMembers.AsNoTracking()
            join user in _db.Users.AsNoTracking() on member.UserId equals user.UserId
            join rolePermission in _db.RolePermissions.AsNoTracking() on member.RoleId equals rolePermission.RoleId
            where member.UserId == _actor.UserId
                  && member.Status == "ACTIVE"
                  && user.Status == "ACTIVE"
                  && rolePermission.PermissionKey == permission
            select member.EngagementId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TeamService
{
    private readonly AuditWorkbenchDbContext _db;
    private readonly UnitOfWork _uow;
    private readonly AuditTrailWriter _audit;
    private readonly EngagementAuthorizationService _authorization;
    private readonly EngagementService _engagements;
    private readonly ICurrentActor _actor;
    private readonly IClock _clock;

    public TeamService(AuditWorkbenchDbContext db, UnitOfWork uow, AuditTrailWriter audit,
        EngagementAuthorizationService authorization, EngagementService engagements, ICurrentActor actor,
        IClock clock)
    { _db = db; _uow = uow; _audit = audit; _authorization = authorization; _engagements = engagements; _actor = actor; _clock = clock; }

    public Task<Guid> CreateUserAsync(string username, string displayName, string? email = null,
        CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        EnsureAuthenticated();
        await _authorization.RequireWorkspacePrivilegeAsync(token).ConfigureAwait(false);
        if (await _db.Users.AnyAsync(x => x.Username == (username ?? string.Empty).Trim().ToLowerInvariant(), token)
                .ConfigureAwait(false))
            throw new ValidationException("That username already exists.");
        var user = User.Create(Guid.NewGuid(), username, displayName, email, IClock.Format(_clock.UtcNow));
        _db.Users.Add(user);
        await _audit.AppendAsync(AuditEventType.UserCreated, AuditEntityType.User, user.UserId.ToString("D"),
            $"Application user '{user.DisplayName}' created.",
            details: AuditDetails.Empty().With("username", user.Username), cancellationToken: token);
        return user.UserId;
    }, cancellationToken);

    public Task AddMemberAsync(Guid engagementId, Guid userId, string roleKey,
        CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageTeam, token);
        await EnsureEngagementOpenAsync(engagementId, token);
        var role = await _db.Roles.SingleOrDefaultAsync(r => r.RoleKey == roleKey, token)
            ?? throw new ValidationException("That role does not exist.");
        if (!await _db.Users.AnyAsync(u => u.UserId == userId && u.Status == "ACTIVE", token))
            throw new ValidationException("The user does not exist or is inactive.");
        if (await _db.EngagementMembers.AnyAsync(m => m.EngagementId == engagementId && m.UserId == userId, token))
            throw new ValidationException("The user already has a membership record for this engagement.");
        var member = EngagementMember.Create(engagementId, userId, role.RoleId, IClock.Format(_clock.UtcNow), _actor.UserId);
        _db.EngagementMembers.Add(member);
        await _audit.AppendAsync(AuditEventType.EngagementMemberAdded, AuditEntityType.EngagementMember,
            member.EngagementMemberId.ToString("D"), $"{await DisplayAsync(userId, token)} added to the engagement team.",
            engagementId: engagementId, details: AuditDetails.Empty().With("user_id", userId).With("role", role.RoleKey),
            cancellationToken: token);
    }, cancellationToken);

    public Task<Guid> AssignAsync(Guid engagementId, Guid assignee, string scopeType, string scopeId, string title,
        CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageAssignments, token);
        await EnsureEngagementOpenAsync(engagementId, token);
        if (!await (from member in _db.EngagementMembers
                    join user in _db.Users on member.UserId equals user.UserId
                    where member.EngagementId == engagementId && member.UserId == assignee
                          && member.Status == "ACTIVE" && user.Status == "ACTIVE"
                    select member.EngagementMemberId).AnyAsync(token))
            throw new ValidationException("Assignments may only be given to active engagement members.");
        var assignment = Assignment.Create(engagementId, assignee, scopeType, scopeId, title,
            IClock.Format(_clock.UtcNow), _actor.UserId);
        _db.Assignments.Add(assignment);
        await _audit.AppendAsync(AuditEventType.AssignmentCreated, AuditEntityType.Assignment,
            assignment.AssignmentId.ToString("D"), $"Assignment '{title}' created.", engagementId: engagementId,
            details: AuditDetails.Empty().With("assignee_user_id", assignee).With("scope_type", assignment.ScopeType)
                .With("scope_id", assignment.ScopeId), cancellationToken: token);
        return assignment.AssignmentId;
    }, cancellationToken);

    /// <summary>Changes a member's role. The membership history itself is never rewritten.</summary>
    public Task ChangeMemberRoleAsync(Guid engagementId, Guid userId, string roleKey,
        int? expectedRowVersion = null, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageTeam, token);
        var member = await LoadMemberAsync(engagementId, userId, token);
        await EnsureEngagementOpenAsync(engagementId, token);

        var role = await _db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.RoleKey == roleKey, token)
            ?? throw new ValidationException("That role does not exist.");
        var previousRoleKey = (await _db.Roles.AsNoTracking()
            .SingleAsync(r => r.RoleId == member.RoleId, token)).RoleKey;

        if (userId == _actor.UserId && !await RoleHasPermissionAsync(role.RoleId, Permissions.ManageTeam, token))
            throw new ValidationException(
                "You cannot change your own role to one that cannot manage the team; another team manager must do it.");

        member.EnsureExpectedVersion(expectedRowVersion);
        member.ChangeRole(role.RoleId, IClock.Format(_clock.UtcNow));

        await _audit.AppendAsync(AuditEventType.EngagementMemberRoleChanged, AuditEntityType.EngagementMember,
            member.EngagementMemberId.ToString("D"),
            $"Team role of {await DisplayAsync(userId, token)} changed from {previousRoleKey} to {role.RoleKey}.",
            engagementId: engagementId,
            details: AuditDetails.Empty().With("user_id", userId)
                .With("previous_role", previousRoleKey).With("new_role", role.RoleKey),
            cancellationToken: token);
    }, cancellationToken);

    /// <summary>
    /// Suspends a membership. Access is revoked immediately; the record and its
    /// history are retained (data-model.md section 8: disable, never delete).
    /// </summary>
    public Task SuspendMemberAsync(Guid engagementId, Guid userId,
        int? expectedRowVersion = null, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageTeam, token);
        if (userId == _actor.UserId)
            throw new ValidationException("You cannot suspend your own membership; another team manager must do it.");
        var member = await LoadMemberAsync(engagementId, userId, token);
        await EnsureEngagementOpenAsync(engagementId, token);

        member.EnsureExpectedVersion(expectedRowVersion);
        member.Suspend(IClock.Format(_clock.UtcNow));

        await _audit.AppendAsync(AuditEventType.EngagementMemberSuspended, AuditEntityType.EngagementMember,
            member.EngagementMemberId.ToString("D"), $"{await DisplayAsync(userId, token)} suspended from the engagement team.",
            engagementId: engagementId, details: AuditDetails.Empty().With("user_id", userId),
            cancellationToken: token);
    }, cancellationToken);

    public Task ReactivateMemberAsync(Guid engagementId, Guid userId,
        int? expectedRowVersion = null, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageTeam, token);
        var member = await LoadMemberAsync(engagementId, userId, token);
        await EnsureEngagementOpenAsync(engagementId, token);

        member.EnsureExpectedVersion(expectedRowVersion);
        member.Reactivate(IClock.Format(_clock.UtcNow));

        await _audit.AppendAsync(AuditEventType.EngagementMemberReactivated, AuditEntityType.EngagementMember,
            member.EngagementMemberId.ToString("D"), $"{await DisplayAsync(userId, token)} reactivated on the engagement team.",
            engagementId: engagementId, details: AuditDetails.Empty().With("user_id", userId),
            cancellationToken: token);
    }, cancellationToken);

    /// <summary>
    /// Returns engagement-scoped assignments. Assignment managers see the full list;
    /// other members can see only work assigned to their server-resolved identity.
    /// </summary>
    public async Task<IReadOnlyList<AssignmentRow>> ListAssignmentsAsync(Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var canManage = await _authorization.HasPermissionAsync(
            engagementId, Permissions.ManageAssignments, cancellationToken).ConfigureAwait(false);
        return await _db.Assignments.AsNoTracking()
            .Where(a => a.EngagementId == engagementId && (canManage || a.AssigneeUserId == _actor.UserId))
            .OrderBy(a => a.Status).ThenBy(a => a.Title)
            .Select(a => new AssignmentRow(a.AssignmentId, a.EngagementId, a.AssigneeUserId,
                a.ScopeType, a.ScopeId, a.Title, a.Status, a.UpdatedAtUtc, a.RowVersion))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reassigns active work and retains old/new assignees in the audit history.</summary>
    public Task ReassignAsync(Guid engagementId, Guid assignmentId, Guid newAssignee,
        int? expectedRowVersion = null, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageAssignments, token);
        var assignment = await LoadAssignmentAsync(engagementId, assignmentId, token);
        await EnsureEngagementOpenAsync(engagementId, token);
        if (!await (from member in _db.EngagementMembers
                    join user in _db.Users on member.UserId equals user.UserId
                    where member.EngagementId == engagementId && member.UserId == newAssignee
                          && member.Status == "ACTIVE" && user.Status == "ACTIVE"
                    select member.EngagementMemberId).AnyAsync(token))
            throw new ValidationException("Assignments may only be given to active engagement members.");

        assignment.EnsureExpectedVersion(expectedRowVersion);
        var previous = assignment.AssigneeUserId;
        assignment.Reassign(newAssignee, IClock.Format(_clock.UtcNow));
        await _audit.AppendAsync(AuditEventType.AssignmentReassigned, AuditEntityType.Assignment,
            assignment.AssignmentId.ToString("D"), $"Assignment '{assignment.Title}' reassigned.",
            engagementId: engagementId,
            details: AuditDetails.Empty().With("previous_assignee_user_id", previous)
                .With("new_assignee_user_id", newAssignee).With("scope_type", assignment.ScopeType)
                .With("scope_id", assignment.ScopeId), cancellationToken: token);
    }, cancellationToken);

    /// <summary>Completes an assignment: terminal, the outcome is retained as history.</summary>
    public Task CompleteAssignmentAsync(Guid engagementId, Guid assignmentId,
        int? expectedRowVersion = null, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageAssignments, token);
        var assignment = await LoadAssignmentAsync(engagementId, assignmentId, token);
        await EnsureEngagementOpenAsync(engagementId, token);

        assignment.EnsureExpectedVersion(expectedRowVersion);
        assignment.Complete(IClock.Format(_clock.UtcNow));

        await _audit.AppendAsync(AuditEventType.AssignmentCompleted, AuditEntityType.Assignment,
            assignment.AssignmentId.ToString("D"), $"Assignment '{assignment.Title}' completed.",
            engagementId: engagementId,
            details: AuditDetails.Empty().With("assignee_user_id", assignment.AssigneeUserId)
                .With("scope_type", assignment.ScopeType).With("scope_id", assignment.ScopeId),
            cancellationToken: token);
    }, cancellationToken);

    /// <summary>Cancels an assignment: terminal, the outcome is retained as history.</summary>
    public Task CancelAssignmentAsync(Guid engagementId, Guid assignmentId,
        int? expectedRowVersion = null, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageAssignments, token);
        var assignment = await LoadAssignmentAsync(engagementId, assignmentId, token);
        await EnsureEngagementOpenAsync(engagementId, token);

        assignment.EnsureExpectedVersion(expectedRowVersion);
        assignment.Cancel(IClock.Format(_clock.UtcNow));

        await _audit.AppendAsync(AuditEventType.AssignmentCancelled, AuditEntityType.Assignment,
            assignment.AssignmentId.ToString("D"), $"Assignment '{assignment.Title}' cancelled.",
            engagementId: engagementId,
            details: AuditDetails.Empty().With("assignee_user_id", assignment.AssigneeUserId)
                .With("scope_type", assignment.ScopeType).With("scope_id", assignment.ScopeId),
            cancellationToken: token);
    }, cancellationToken);

    /// <summary>
    /// Disables an application account. All engagement access is revoked at once
    /// because authorization only considers active users; attribution and
    /// membership history are preserved. Users are never deleted.
    /// </summary>
    public Task DeactivateUserAsync(Guid userId, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        EnsureAuthenticated();
        await _authorization.RequireWorkspacePrivilegeAsync(token).ConfigureAwait(false);
        if (userId == _actor.UserId)
            throw new ValidationException("You cannot deactivate your own account.");
        var user = await LoadUserAsync(userId, token);

        user.Deactivate(IClock.Format(_clock.UtcNow));

        await _audit.AppendAsync(AuditEventType.UserDeactivated, AuditEntityType.User, user.UserId.ToString("D"),
            $"Application user '{user.DisplayName}' deactivated.",
            details: AuditDetails.Empty().With("username", user.Username), cancellationToken: token);
    }, cancellationToken);

    public Task ReactivateUserAsync(Guid userId, CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        EnsureAuthenticated();
        await _authorization.RequireWorkspacePrivilegeAsync(token).ConfigureAwait(false);
        var user = await LoadUserAsync(userId, token);

        user.Reactivate(IClock.Format(_clock.UtcNow));

        await _audit.AppendAsync(AuditEventType.UserReactivated, AuditEntityType.User, user.UserId.ToString("D"),
            $"Application user '{user.DisplayName}' reactivated.",
            details: AuditDetails.Empty().With("username", user.Username), cancellationToken: token);
    }, cancellationToken);

    private Task<bool> RoleHasPermissionAsync(Guid roleId, string permission, CancellationToken token) =>
        _db.RolePermissions.AsNoTracking().AnyAsync(p => p.RoleId == roleId && p.PermissionKey == permission, token);

    private async Task<EngagementMember> LoadMemberAsync(Guid engagementId, Guid userId, CancellationToken token) =>
        await _db.EngagementMembers
            .FirstOrDefaultAsync(m => m.EngagementId == engagementId && m.UserId == userId, token)
            .ConfigureAwait(false)
        ?? throw new ValidationException("That user does not have a membership record for this engagement.");

    private async Task<Assignment> LoadAssignmentAsync(Guid engagementId, Guid assignmentId, CancellationToken token) =>
        await _db.Assignments
            .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId && a.EngagementId == engagementId, token)
            .ConfigureAwait(false)
        ?? throw new ValidationException("That assignment does not belong to this engagement.");

    private async Task<User> LoadUserAsync(Guid userId, CancellationToken token) =>
        await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, token).ConfigureAwait(false)
        ?? throw new NotFoundException("That application user does not exist in this workspace.");

    /// <summary>
    /// Team and assignment mutations are engagement-owned: they stop at
    /// finalization (team-architecture.md). The database triggers raise the same
    /// refusal for any code path that misses this guard.
    /// </summary>
    private async Task EnsureEngagementOpenAsync(Guid engagementId, CancellationToken token)
    {
        var engagement = await _engagements.LoadAsync(engagementId, token).ConfigureAwait(false);
        var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, token).ConfigureAwait(false);
        engagement.EnsureOpenForEditing(year.Label);
    }

    private void EnsureAuthenticated()
    {
        if (!_actor.IsAuthenticated || _actor.UserId == Guid.Empty)
            throw new AuthorizationException("An authenticated application user is required.");
    }
    private async Task<string> DisplayAsync(Guid id, CancellationToken token) =>
        await _db.Users.AsNoTracking().Where(u => u.UserId == id).Select(u => u.DisplayName)
            .FirstOrDefaultAsync(token).ConfigureAwait(false) ?? $"User {id:D}";
}
