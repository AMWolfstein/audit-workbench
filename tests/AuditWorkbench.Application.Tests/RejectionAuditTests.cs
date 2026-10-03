using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Domain.Identity;

namespace AuditWorkbench.Application.Tests;

/// <summary>Refused protected writes are recorded as REJECTED events without breaking the hash chain.</summary>
public class RejectionAuditTests
{
    private static async Task<IReadOnlyList<AuditEventRow>> RejectionsAsync(TestWorkspace workspace) =>
        await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>()
            .ListAsync(eventType: AuditEventType.ProtectedWriteRejected, limit: 500));

    private static async Task AssertChainIntactAsync(TestWorkspace workspace)
    {
        Assert.True(await workspace.UseAsync(scope => scope.GetRequiredService<AuditTrailQuery>().VerifyChainAsync()));
        Assert.Equal(
            await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event"),
            await workspace.ScalarAsync("SELECT MAX(sequence_no) FROM audit_event"));
        Assert.Equal(1L, await workspace.ScalarAsync("SELECT MIN(sequence_no) FROM audit_event"));
    }

    [Fact]
    public async Task AuthorizationFailureIsRecordedAndTheChainStillVerifies()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        Guid auditor = Guid.Empty;
        await workspace.UseAsync(async scope =>
        {
            var teams = scope.GetRequiredService<TeamService>();
            auditor = await teams.CreateUserAsync("aya", "Aya Auditor");
            await teams.AddMemberAsync(engagement, auditor, BuiltInRoles.Auditor);
        });

        actor.Become(auditor, "aya", "Aya Auditor");
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>().AddMemberAsync(engagement, auditor, BuiltInRoles.Manager)));

        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        var rejected = Assert.Single(await RejectionsAsync(workspace));
        Assert.Equal(AuditEventOutcome.Rejected, rejected.Outcome);
        Assert.Equal(auditor, rejected.ActorUserId);
        Assert.Equal(engagement, rejected.EngagementId);
        Assert.Contains("AWB-FORBIDDEN", rejected.DetailsJson);
        await AssertChainIntactAsync(workspace);
    }

    [Fact]
    public async Task WriteAgainstAFinalizedYearIsRecordedWithoutFinancialValues()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var account = await workspace.AddAccountAsync(engagement, "4000", "Revenue", "123456.78");
        await workspace.FinalizeAsync(engagement);
        var before = await workspace.ScalarAsync("SELECT COUNT(*) FROM financial_data");

        await Assert.ThrowsAsync<EngagementFinalizedException>(() =>
            workspace.RecordValueAsync(engagement, account, "987654.32", "late change"));

        Assert.Equal(before, await workspace.ScalarAsync("SELECT COUNT(*) FROM financial_data"));
        var rejected = Assert.Single(await RejectionsAsync(workspace));
        Assert.Equal(AuditEventOutcome.Rejected, rejected.Outcome);
        Assert.Equal(engagement, rejected.EngagementId);
        Assert.Contains("AWB-FINALIZED", rejected.DetailsJson);
        Assert.DoesNotContain("987654", rejected.DetailsJson + rejected.Description);
        Assert.DoesNotContain("late change", rejected.DetailsJson + rejected.Description);
        await AssertChainIntactAsync(workspace);
    }

    [Fact]
    public async Task SuccessfulAndRejectedEventsInterleaveInOneContiguousChain()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var account = await workspace.AddAccountAsync(engagement, "4000", "Revenue", "100");
        await workspace.FinalizeAsync(engagement);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<EngagementFinalizedException>(() =>
                workspace.RecordValueAsync(engagement, account, "1"));
        }

        await workspace.CreateCompanyAsync("XYZ-DEMO");
        Assert.Equal(3, (await RejectionsAsync(workspace)).Count);
        await AssertChainIntactAsync(workspace);
    }

    [Fact]
    public async Task ActorWithoutAnAccountStillGetsTheOriginalRefusalAndTheChainIsUntouched()
    {
        var actor = new MutableActor(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        await using var workspace = await TestWorkspace.CreateAsync(actor);
        var company = await workspace.CreateCompanyAsync();
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);

        actor.Become(Guid.NewGuid(), "ghost", "Ghost");
        await Assert.ThrowsAsync<AuthorizationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TeamService>().AddMemberAsync(engagement, Guid.NewGuid(), BuiltInRoles.Auditor)));

        // The audit_event actor foreign key cannot be satisfied: recording is skipped, not corrupting.
        actor.Become(LocalUser.LocalActorId, LocalUser.LocalActorUsername, LocalUser.LocalActorDisplayName);
        Assert.Empty(await RejectionsAsync(workspace));
        await AssertChainIntactAsync(workspace);
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
