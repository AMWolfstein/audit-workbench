using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Companies;

namespace AuditWorkbench.Infrastructure.Identity;

/// <summary>
/// MVP identity: one local, clearly non-production actor (ADR-013). Release
/// hardening replaces this with approved local or OS-integrated authentication;
/// no other code needs to change because every call site uses ICurrentActor.
/// </summary>
public sealed class LocalActor : ICurrentActor
{
    public Guid UserId => LocalUser.LocalActorId;

    public string Username => LocalUser.LocalActorUsername;

    public string DisplayName => LocalUser.LocalActorDisplayName;

    public bool IsAuthenticated => true;

    public string AuthenticationMethod => "LocalDevelopment";

    public bool IsLocalDemoIdentity => true;
}
