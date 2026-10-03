using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Backup;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Domain.Identity;

namespace AuditWorkbench.Application.Tests;

/// <summary>ADR-024: workspace-level actions require an active Partner/Manager membership.</summary>
public class WorkspacePrivilegeTests
{
    private static MutableActor LocalActor() =>
        new(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);

    private static async Task<(TestWorkspace Workspace, MutableActor Actor, Guid Engagement, Guid Auditor, Guid Manager)>
        ArrangeAsync()
    {
        var actor = LocalActor();
        var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        Guid auditor = Guid.Empty, manager = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            auditor = await teams.CreateUserAsync("aya", "Aya Auditor");
            manager = await teams.CreateUserAsync("maged", "Maged Manager");
            await teams.AddMemberAsync(engagement, auditor, BuiltInRoles.Auditor);
            await teams.AddMemberAsync(engagement, manager, BuiltInRoles.Manager);
        });
        return (workspace, actor, engagement, auditor, manager);
    }

    [Fact]
    public async Task BackupRequiresAPartnerOrManagerMembership()
    {
        var (workspace, actor, _, auditor, manager) = await ArrangeAsync();
        await using var _ws = workspace;

        actor.Become(Guid.NewGuid(), "stranger", "Stranger");
        await Assert.ThrowsAsync<AuthorizationException>(() =>
            workspace.UseAsync(scope => scope.GetRequiredService<BackupService>().CreateAsync()));

        actor.Become(auditor, "aya", "Aya Auditor");
        await Assert.ThrowsAsync<AuthorizationException>(() =>
            workspace.UseAsync(scope => scope.GetRequiredService<BackupService>().CreateAsync()));

        actor.Become(manager, "maged", "Maged Manager");
        var package = await workspace.UseAsync(scope => scope.GetRequiredService<BackupService>().CreateAsync());
        Assert.NotNull(package);
    }

    [Fact]
    public async Task UserAdministrationRequiresAPartnerOrManagerMembership()
    {
        var (workspace, actor, _, auditor, manager) = await ArrangeAsync();
        await using var _ws = workspace;

        foreach (var (id, name) in new[] { (Guid.NewGuid(), "stranger"), (auditor, "aya") })
        {
            actor.Become(id, name, name);
            await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
                scope.GetRequiredService<TeamService>().CreateUserAsync("intruder", "Intruder")));
            await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
                scope.GetRequiredService<TeamService>().DeactivateUserAsync(manager)));
        }

        actor.Become(manager, "maged", "Maged Manager");
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>().DeactivateUserAsync(auditor));
        await workspace.UseAsync(scope => scope.GetRequiredService<TeamService>().ReactivateUserAsync(auditor));
    }

    [Fact]
    public async Task AuditTrailHidesEveryEventFromNonMembersAndMembersWithoutThePermission()
    {
        var (workspace, actor, engagement, auditor, manager) = await ArrangeAsync();
        await using var _ws = workspace;

        foreach (var (id, name) in new[] { (Guid.NewGuid(), "stranger"), (auditor, "aya") })
        {
            actor.Become(id, name, name);
            await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
                scope.GetRequiredService<AuditTrailQuery>().ListAsync(engagementId: engagement)));
            Assert.Empty(await workspace.UseAsync(scope =>
                scope.GetRequiredService<AuditTrailQuery>().ListAsync(limit: 500)));
        }

        actor.Become(manager, "maged", "Maged Manager");
        var visible = await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(engagementId: engagement));
        Assert.NotEmpty(visible);
    }

    [Fact]
    public async Task ManagerCannotDemoteSelfToARoleThatCannotManageTheTeam()
    {
        var (workspace, actor, engagement, _, manager) = await ArrangeAsync();
        await using var _ws = workspace;

        actor.Become(manager, "maged", "Maged Manager");
        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>()
                .ChangeMemberRoleAsync(engagement, manager, BuiltInRoles.Auditor)));
    }

    [Fact]
    public async Task EmptyWorkspaceCanBeBootstrappedByAnyAuthenticatedActor()
    {
        var actor = LocalActor();
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        await workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>().CreateUserAsync("first", "First User"));
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
