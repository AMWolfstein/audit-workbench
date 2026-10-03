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
