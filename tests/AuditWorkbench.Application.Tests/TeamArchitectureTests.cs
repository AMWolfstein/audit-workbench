using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Identity;

namespace AuditWorkbench.Application.Tests;

public class TeamArchitectureTests
{
    [Fact]
    public async Task AccountDoesNotGrantAccessWithoutEngagementMembershipAndRolePermission()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagementA = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var engagementB = await workspace.CreateYearAsync(company, "FY2027", 2027);

        Guid auditor = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            auditor = await teams.CreateUserAsync("ahmed", "Ahmed Auditor", "ahmed@example.test");
            await teams.AddMemberAsync(engagementA, auditor, BuiltInRoles.Auditor);
        });

        actor.Become(auditor, "ahmed", "Ahmed Auditor");
        await workspace.UseAsync(async scope =>
        {
            var authorization = scope.GetRequiredService<EngagementAuthorizationService>();
            Assert.True(await authorization.HasPermissionAsync(engagementA, Permissions.ViewEngagement));
            Assert.True(await authorization.HasPermissionAsync(engagementA, Permissions.EditEngagement));
            Assert.False(await authorization.HasPermissionAsync(engagementA, Permissions.ManageTeam));
            Assert.False(await authorization.HasPermissionAsync(engagementB, Permissions.ViewEngagement));
            await Assert.ThrowsAsync<AuthorizationException>(() =>
                authorization.RequireAsync(engagementB, Permissions.ViewEngagement));
            await Assert.ThrowsAsync<AuthorizationException>(() =>
                scope.GetRequiredService<EngagementService>()
                    .ChangeStatusAsync(engagementB, EngagementStatus.InProgress, expectedRowVersion: 1));
        });
    }

    [Fact]
    public async Task AuditEventUsesAuthenticatedUserNotClientSuppliedIdentity()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        Guid manager = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            manager = await teams.CreateUserAsync("mohamed", "Mohamed Manager");
            await teams.AddMemberAsync(engagement, manager, BuiltInRoles.Manager);
        });
        actor.Become(manager, "mohamed", "Mohamed Manager");
        await workspace.UseAsync(scope => scope.GetRequiredService<EngagementService>()
            .ChangeStatusAsync(engagement, EngagementStatus.InProgress, expectedRowVersion: 1));
        var evt = (await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "ENGAGEMENT_STATUS_CHANGED"))).Single();
        Assert.Equal(manager, evt.ActorUserId);
        Assert.Equal("Mohamed Manager", evt.ActorDisplayName);
    }

    [Fact]
    public async Task StaleVersionCannotOverwriteNewerUpdate()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        await workspace.UseAsync(scope => scope.GetRequiredService<EngagementService>()
            .ChangeStatusAsync(engagement, EngagementStatus.InProgress, expectedRowVersion: 1));
        await Assert.ThrowsAsync<ConcurrencyException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>()
                .ChangeStatusAsync(engagement, EngagementStatus.InProgress, expectedRowVersion: 1)));
        var current = await workspace.UseAsync(scope => scope.GetRequiredService<EngagementService>().GetAsync(engagement));
        Assert.Equal(2, current.RowVersion);
    }

    [Fact]
    public async Task MemberRoleChangesAreAuditedAndConcurrencyProtected()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        Guid member = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            member = await teams.CreateUserAsync("sara", "Sara Senior");
            await teams.AddMemberAsync(engagement, member, BuiltInRoles.Auditor);
        });

        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>()
            .ChangeMemberRoleAsync(engagement, member, BuiltInRoles.Senior, expectedRowVersion: 1));

        Assert.Equal(BuiltInRoles.SeniorId.ToString("D"), await workspace.TextScalarAsync(
            "SELECT role_id FROM engagement_member WHERE engagement_id = $e AND user_id = $u",
            ("$e", engagement.ToString("D")), ("$u", member.ToString("D"))));
        Assert.Equal(2L, await workspace.ScalarAsync(
            "SELECT row_version FROM engagement_member WHERE engagement_id = $e AND user_id = $u",
            ("$e", engagement.ToString("D")), ("$u", member.ToString("D"))));

        var roleEvent = (await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "ENGAGEMENT_MEMBER_ROLE_CHANGED"))).Single();
        Assert.Equal("ENGAGEMENT_MEMBER", roleEvent.EntityType);
        Assert.Equal(engagement, roleEvent.EngagementId);
        Assert.Contains("AUDITOR", roleEvent.DetailsJson);
        Assert.Contains("SENIOR", roleEvent.DetailsJson);

        // A stale expected version is refused (ADR-022) and nothing changes.
        await Assert.ThrowsAsync<ConcurrencyException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .ChangeMemberRoleAsync(engagement, member, BuiltInRoles.Manager, expectedRowVersion: 1)));
        Assert.Equal(BuiltInRoles.SeniorId.ToString("D"), await workspace.TextScalarAsync(
            "SELECT role_id FROM engagement_member WHERE engagement_id = $e AND user_id = $u",
            ("$e", engagement.ToString("D")), ("$u", member.ToString("D"))));

        // An auditor cannot manage the team at all.
        actor.Become(member, "sara", "Sara Senior");
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .ChangeMemberRoleAsync(engagement, member, BuiltInRoles.Manager)));
    }

    [Fact]
    public async Task MemberSuspensionRevokesAccessUntilReactivated()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        Guid manager = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            manager = await teams.CreateUserAsync("mona", "Mona Manager");
            await teams.AddMemberAsync(engagement, manager, BuiltInRoles.Manager);
        });

        actor.Become(manager, "mona", "Mona Manager");
        Assert.True(await workspace.UseAsync(scope => scope.GetRequiredService<EngagementAuthorizationService>()
            .HasPermissionAsync(engagement, Permissions.ViewEngagement)));

        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>()
            .SuspendMemberAsync(engagement, manager, expectedRowVersion: 1));

        Assert.Equal("SUSPENDED", await workspace.TextScalarAsync(
            "SELECT status FROM engagement_member WHERE engagement_id = $e AND user_id = $u",
            ("$e", engagement.ToString("D")), ("$u", manager.ToString("D"))));

        // Suspension is audited (the partner still sees the engagement-scoped event).
        Assert.NotEmpty(await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "ENGAGEMENT_MEMBER_SUSPENDED", limit: 500)));

        actor.Become(manager, "mona", "Mona Manager");
        Assert.False(await workspace.UseAsync(scope => scope.GetRequiredService<EngagementAuthorizationService>()
            .HasPermissionAsync(engagement, Permissions.ViewEngagement)));
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().GetAsync(engagement)));
        Assert.Empty(await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().ListByCompanyAsync(company)));

        // Suspension history is retained, never deleted.
        Assert.Equal(1L, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM engagement_member WHERE engagement_id = $e AND user_id = $u",
            ("$e", engagement.ToString("D")), ("$u", manager.ToString("D"))));

        // A suspended member has lost the permission to reactivate themselves; the partner reactivates.
        actor.Become(manager, "mona", "Mona Manager");
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .ReactivateMemberAsync(engagement, manager, expectedRowVersion: 2)));
        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>().SuspendMemberAsync(engagement, LocalUser.LocalActorId)));
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>()
            .ReactivateMemberAsync(engagement, manager, expectedRowVersion: 2));

        actor.Become(manager, "mona", "Mona Manager");
        Assert.True(await workspace.UseAsync(scope => scope.GetRequiredService<EngagementAuthorizationService>()
            .HasPermissionAsync(engagement, Permissions.ViewEngagement)));
        Assert.NotEmpty(await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "ENGAGEMENT_MEMBER_REACTIVATED", limit: 500)));

        // Only a suspended membership can be reactivated.
        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .ReactivateMemberAsync(engagement, manager, expectedRowVersion: 3)));
    }

    [Fact]
    public async Task MemberAndAssignmentChangesStopAtFinalization()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        Guid auditor = Guid.Empty;
        Guid assignment = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            auditor = await teams.CreateUserAsync("omar", "Omar Auditor");
            await teams.AddMemberAsync(engagement, auditor, BuiltInRoles.Auditor);
            assignment = await teams.AssignAsync(engagement, auditor, "AUDIT_AREA", "AREA-1", "Revenue cut-off");
        });
        await workspace.AddAccountAsync(engagement, "4000", "Revenue", "850000000");
        await workspace.FinalizeAsync(engagement);

        // Application guard: membership and assignment mutations refuse the finalized year.
        await Assert.ThrowsAsync<EngagementFinalizedException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .ChangeMemberRoleAsync(engagement, auditor, BuiltInRoles.Senior, expectedRowVersion: 1)));
        await Assert.ThrowsAsync<EngagementFinalizedException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .SuspendMemberAsync(engagement, auditor, expectedRowVersion: 1)));
        await Assert.ThrowsAsync<EngagementFinalizedException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .CompleteAssignmentAsync(engagement, assignment, expectedRowVersion: 1)));

        // Database triggers raise the same refusal for any code path that misses the guard.
        var memberError = await workspace.ExpectRawFailureAsync(
            "UPDATE engagement_member SET status = 'SUSPENDED' WHERE engagement_id = $e AND user_id = $u",
            ("$e", engagement.ToString("D")), ("$u", auditor.ToString("D")));
        Assert.Contains("AWB-GUARD-MEMBER-FINALIZED", memberError.Message);
        var assignmentError = await workspace.ExpectRawFailureAsync(
            "UPDATE assignment SET status = 'CANCELLED' WHERE assignment_id = $a",
            ("$a", assignment.ToString("D")));
        Assert.Contains("AWB-GUARD-ASSIGNMENT-FINALIZED", assignmentError.Message);

        // Account state is platform-level, not engagement-owned: disabling the user
        // stays possible (access is revoked) even though the year is finalized.
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>().DeactivateUserAsync(auditor));
        Assert.Equal("DISABLED", await workspace.TextScalarAsync(
            "SELECT status FROM app_user WHERE user_id = $u", ("$u", auditor.ToString("D"))));
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>().ReactivateUserAsync(auditor));
        Assert.Equal("ACTIVE", await workspace.TextScalarAsync(
            "SELECT status FROM app_user WHERE user_id = $u", ("$u", auditor.ToString("D"))));
    }

    [Fact]
    public async Task AssignmentLifecycleIsAuditedAndConcurrencyProtected()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var otherEngagement = await workspace.CreateYearAsync(company, "FY2027", 2027);
        Guid assignee = Guid.Empty;
        Guid completed = Guid.Empty;
        Guid cancelled = Guid.Empty;
        Guid active = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            assignee = await teams.CreateUserAsync("dina", "Dina Doer");
            await teams.AddMemberAsync(engagement, assignee, BuiltInRoles.Auditor);
            completed = await teams.AssignAsync(engagement, assignee, "AUDIT_AREA", "AREA-1", "Revenue cut-off");
            cancelled = await teams.AssignAsync(engagement, assignee, "WORKING_PAPER", "WP-9", "Receivables lead sheet");
            active = await teams.AssignAsync(engagement, assignee, "PROCEDURE", "PR-4", "Inventory count observation");
        });

        // A nested id from another engagement is refused, not silently resolved.
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .CompleteAssignmentAsync(otherEngagement, completed, expectedRowVersion: 1)));

        // A stale version is refused before anything changes (ADR-022).
        await Assert.ThrowsAsync<ConcurrencyException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .CompleteAssignmentAsync(engagement, completed, expectedRowVersion: 99)));
        Assert.Equal("ACTIVE", await workspace.TextScalarAsync(
            "SELECT status FROM assignment WHERE assignment_id = $a", ("$a", completed.ToString("D"))));

        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>()
            .CompleteAssignmentAsync(engagement, completed, expectedRowVersion: 1));
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>()
            .CancelAssignmentAsync(engagement, cancelled, expectedRowVersion: 1));

        Assert.Equal("COMPLETED", await workspace.TextScalarAsync(
            "SELECT status FROM assignment WHERE assignment_id = $a", ("$a", completed.ToString("D"))));
        Assert.Equal(2L, await workspace.ScalarAsync(
            "SELECT row_version FROM assignment WHERE assignment_id = $a", ("$a", completed.ToString("D"))));
        Assert.Equal("CANCELLED", await workspace.TextScalarAsync(
            "SELECT status FROM assignment WHERE assignment_id = $a", ("$a", cancelled.ToString("D"))));

        var completedEvent = (await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "ASSIGNMENT_COMPLETED", limit: 500))).Single();
        Assert.Equal(engagement, completedEvent.EngagementId);
        Assert.Contains("Revenue cut-off", completedEvent.Description);
        var cancelledEvent = (await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "ASSIGNMENT_CANCELLED", limit: 500))).Single();
        Assert.Contains("Receivables lead sheet", cancelledEvent.Description);

        // Both outcomes are terminal.
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .CompleteAssignmentAsync(engagement, completed, expectedRowVersion: 2)));
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .CancelAssignmentAsync(engagement, cancelled, expectedRowVersion: 2)));

        // Assignment management requires MANAGE_ASSIGNMENTS; an auditor is refused.
        actor.Become(assignee, "dina", "Dina Doer");
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .CancelAssignmentAsync(engagement, active, expectedRowVersion: 1)));
    }

    [Fact]
    public async Task UserDeactivationRevokesAllEngagementAccessAndIsReversible()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        Guid manager = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            manager = await teams.CreateUserAsync("salma", "Salma Manager");
            await teams.AddMemberAsync(engagement, manager, BuiltInRoles.Manager);
        });

        actor.Become(manager, "salma", "Salma Manager");
        Assert.NotNull(await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().GetAsync(engagement)));

        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>().DeactivateUserAsync(LocalUser.LocalActorId)));
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>().DeactivateUserAsync(manager));

        Assert.Equal("DISABLED", await workspace.TextScalarAsync(
            "SELECT status FROM app_user WHERE user_id = $u", ("$u", manager.ToString("D"))));
        Assert.Equal(1L, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM app_user WHERE user_id = $u", ("$u", manager.ToString("D"))));

        actor.Become(manager, "salma", "Salma Manager");
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().GetAsync(engagement)));
        Assert.False(await workspace.UseAsync(scope => scope.GetRequiredService<EngagementAuthorizationService>()
            .HasPermissionAsync(engagement, Permissions.ViewEngagement)));
        Assert.Empty(await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().ListByCompanyAsync(company)));

        // Membership history is untouched: only the account is disabled.
        Assert.Equal("ACTIVE", await workspace.TextScalarAsync(
            "SELECT status FROM engagement_member WHERE engagement_id = $e AND user_id = $u",
            ("$e", engagement.ToString("D")), ("$u", manager.ToString("D"))));

        var deactivatedEvent = (await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "USER_DEACTIVATED", limit: 500))).Single();
        Assert.Contains("salma", deactivatedEvent.DetailsJson);

        // Deactivation is terminal until an explicit reactivation.
        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>().DeactivateUserAsync(manager)));
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>().ReactivateUserAsync(manager));

        actor.Become(manager, "salma", "Salma Manager");
        Assert.NotNull(await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().GetAsync(engagement)));
        Assert.NotEmpty(await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "USER_REACTIVATED", limit: 500)));
    }

    [Fact]
    public async Task AuditTrailReadsRequireViewAuditTrailPermission()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagementA = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var engagementB = await workspace.CreateYearAsync(company, "FY2027", 2027);
        Guid reviewer = Guid.Empty;
        Guid preparer = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            reviewer = await teams.CreateUserAsync("rania", "Rania Reviewer");
            preparer = await teams.CreateUserAsync("tamer", "Tamer Preparer");
            await teams.AddMemberAsync(engagementA, reviewer, BuiltInRoles.ReadOnly);
            await teams.AddMemberAsync(engagementA, preparer, BuiltInRoles.Auditor);
        });
        await workspace.UseAsync(scope => scope.GetRequiredService<EngagementService>()
            .ChangeStatusAsync(engagementA, EngagementStatus.InProgress, expectedRowVersion: 1));

        // An auditor holds VIEW_ENGAGEMENT but not VIEW_AUDIT_TRAIL.
        actor.Become(preparer, "tamer", "Tamer Preparer");
        Assert.True(await workspace.UseAsync(scope => scope.GetRequiredService<EngagementAuthorizationService>()
            .HasPermissionAsync(engagementA, Permissions.ViewEngagement)));
        Assert.False(await workspace.UseAsync(scope => scope.GetRequiredService<EngagementAuthorizationService>()
            .HasPermissionAsync(engagementA, Permissions.ViewAuditTrail)));

        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(engagementId: engagementA)));
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(engagementId: engagementB)));

        // Unscoped reads never leak another engagement's events.
        var visibleToPreparer = await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(limit: 500));
        Assert.All(visibleToPreparer, row => Assert.Null(row.EngagementId));
        Assert.Contains(visibleToPreparer, row => row.EventType == "COMPANY_CREATED");
        Assert.Empty(await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: "ENGAGEMENT_STATUS_CHANGED", limit: 500)));

        // A read-only member holds VIEW_AUDIT_TRAIL and sees the scoped trail.
        actor.Become(reviewer, "rania", "Rania Reviewer");
        var scoped = await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(engagementId: engagementA, limit: 500));
        Assert.All(scoped, row => Assert.Equal(engagementA, row.EngagementId));
        Assert.Contains(scoped, row => row.EventType == "ENGAGEMENT_STATUS_CHANGED");
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(engagementId: engagementB)));

        // The workspace custodian (partner on both years) still sees everything.
        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        var all = await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(limit: 500));
        Assert.Contains(all, row => row.EngagementId == engagementA);
        Assert.Contains(all, row => row.EngagementId == engagementB);
        Assert.True(await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .VerifyChainAsync()));
    }

    private sealed class MutableActor : ICurrentActor
    {
        public MutableActor(Guid id, string username, string displayName) => Become(id, username, displayName);
        public Guid UserId { get; private set; }
        public string Username { get; private set; } = string.Empty;
        public string DisplayName { get; private set; } = string.Empty;
        public bool IsAuthenticated => true;
        public string AuthenticationMethod => "Test";
        public bool IsLocalDemoIdentity => false;
        public void Become(Guid id, string username, string displayName)
        { UserId = id; Username = username; DisplayName = displayName; }
    }
}
