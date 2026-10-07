using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Identity;

namespace AuditWorkbench.Application.Common;

/// <summary>
/// Scoped identity holder. The request actor is the normal source of identity;
/// a background import job may replace it with the identity recorded when the job
/// was requested. The replacement is always a server-side record - never a value
/// taken from a form, route or query string.
/// </summary>
public sealed class ImportActorAccessor
{
    public ImportActorAccessor(ICurrentActor ambient) => Ambient = ambient;

    public ICurrentActor Ambient { get; }

    public ICurrentActor? JobActor { get; set; }

    public ICurrentActor Current => JobActor ?? Ambient;
}

/// <summary>Identity of the user who requested a background import (from the job row).</summary>
public sealed class RecordedActor : ICurrentActor
{
    public RecordedActor(Guid userId, string username, string displayName, string authenticationMethod)
    {
        UserId = userId;
        Username = username;
        DisplayName = displayName;
        AuthenticationMethod = authenticationMethod;
    }

    public Guid UserId { get; }

    public string Username { get; }

    public string DisplayName { get; }

    public bool IsAuthenticated => UserId != Guid.Empty;

    public string AuthenticationMethod { get; }

    public bool IsLocalDemoIdentity => AuthenticationMethod == "Local";
}

/// <summary>Permission keys used by the financial-data foundation (foundation.md).</summary>
public static class FinancialDataPermissions
{
    public const string View = Permissions.ViewEngagement;

    public const string Import = Permissions.EditEngagement;

    public const string Finalize = Permissions.FinalizeEngagement;
}
