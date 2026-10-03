using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Domain.Teams;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Teams;

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
            throw new AuthorizationException("You are not an active engagement member with the required permission.");
    }
}

public sealed class TeamService
{
    private readonly AuditWorkbenchDbContext _db;
    private readonly UnitOfWork _uow;
    private readonly AuditTrailWriter _audit;
    private readonly EngagementAuthorizationService _authorization;
    private readonly ICurrentActor _actor;
    private readonly IClock _clock;

    public TeamService(AuditWorkbenchDbContext db, UnitOfWork uow, AuditTrailWriter audit,
        EngagementAuthorizationService authorization, ICurrentActor actor, IClock clock)
    { _db = db; _uow = uow; _audit = audit; _authorization = authorization; _actor = actor; _clock = clock; }

    public Task<Guid> CreateUserAsync(string username, string displayName, string? email = null,
        CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        EnsureAuthenticated();
        if (await _db.Users.AnyAsync(x => x.Username == username.Trim().ToLower(), token))
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
        var role = await _db.Roles.SingleOrDefaultAsync(r => r.RoleKey == roleKey, token)
            ?? throw new ValidationException("That role does not exist.");
        if (!await _db.Users.AnyAsync(u => u.UserId == userId && u.Status == "ACTIVE", token))
            throw new ValidationException("The user does not exist or is inactive.");
        if (await _db.EngagementMembers.AnyAsync(m => m.EngagementId == engagementId && m.UserId == userId, token))
            throw new ValidationException("The user already has a membership record for this engagement.");
        var member = EngagementMember.Create(engagementId, userId, role.RoleId, IClock.Format(_clock.UtcNow), _actor.UserId);
        _db.EngagementMembers.Add(member);
        await _audit.AppendAsync(AuditEventType.EngagementMemberAdded, AuditEntityType.EngagementMember,
            member.EngagementMemberId.ToString("D"), $"{display(userId)} added to the engagement team.",
            engagementId: engagementId, details: AuditDetails.Empty().With("user_id", userId).With("role", role.RoleKey),
            cancellationToken: token);
    }, cancellationToken);

    public Task<Guid> AssignAsync(Guid engagementId, Guid assignee, string scopeType, string scopeId, string title,
        CancellationToken cancellationToken = default) => _uow.ExecuteAsync(async token =>
    {
        await _authorization.RequireAsync(engagementId, Permissions.ManageAssignments, token);
        if (!await _db.EngagementMembers.AnyAsync(m => m.EngagementId == engagementId && m.UserId == assignee && m.Status == "ACTIVE", token))
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

    private void EnsureAuthenticated()
    {
        if (!_actor.IsAuthenticated || _actor.UserId == Guid.Empty)
            throw new AuthorizationException("An authenticated application user is required.");
    }
    private static string display(Guid id) => $"User {id:D}";
}
